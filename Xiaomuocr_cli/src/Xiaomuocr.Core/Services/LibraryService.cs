using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 文献库管理服务：文件夹和文献条目的增删改查。
/// 数据存储在客户端 SQLite 数据库中。
/// </summary>
public interface ILibraryService
{
    // ---- 文件夹 ----
    Task<List<LibraryFolder>> GetFoldersAsync();
    Task<LibraryFolder> AddFolderAsync(string name, string? parentId = null);
    Task RenameFolderAsync(string folderId, string newName);
    Task DeleteFolderAsync(string folderId);

    // ---- 文献 ----
    Task<List<LibraryItem>> GetItemsAsync(string? folderId = null);
    Task<LibraryItem> AddItemAsync(LibraryItem item);
    Task UpdateItemAsync(LibraryItem item);
    Task DeleteItemAsync(string itemId);
    Task<LibraryItem?> GetItemAsync(string itemId);
    /// <summary>将文献移动到目标文件夹（folderId 通常为 root 或其他文件夹 id）。</summary>
    Task MoveItemToFolderAsync(string itemId, string folderId);

    /// <summary>文献库中是否仍有条目使用该 OCR 输出目录（删除后应停止结果落盘）。</summary>
    Task<bool> IsOutputDirInLibraryAsync(string? outputDir);

    /// <summary>
    /// 静默按磁盘 OCR 结果校准 ocr_done_pages（不挡 UI）。
    /// 返回实际写库变更的 (id, newCount)；已在跑则返回空列表。
    /// 大批量文献时限并发扫盘、分批写库并让出 CPU。
    /// </summary>
    Task<IReadOnlyList<(string Id, int OcrDonePages)>> SilentSyncOcrDonePagesAsync(
        CancellationToken ct = default);
}

public class LibraryService : ILibraryService, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _dbGate = new();
    private int _silentSyncRunning;
    private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public LibraryService(string dbPath)
    {
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitTables();
    }

    private void InitTables()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS library_folders (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                parent_id TEXT
            );
            CREATE TABLE IF NOT EXISTS library_items (
                id TEXT PRIMARY KEY,
                pdf_path TEXT NOT NULL,
                name TEXT NOT NULL,
                folder_id TEXT DEFAULT 'root',
                total_pages INTEGER DEFAULT 0,
                ocr_done_pages INTEGER DEFAULT 0,
                added_time TEXT NOT NULL,
                notes TEXT,
                output_dir TEXT
            );
            -- 确保默认根文件夹存在
            INSERT OR IGNORE INTO library_folders (id, name, parent_id)
            VALUES ('root', '全部文献', NULL);
        ";
        cmd.ExecuteNonQuery();

        // 兼容旧库：补充 source 列
        try
        {
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE library_items ADD COLUMN source TEXT";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) { /* 列已存在 */ }
    }

    // ================ 文件夹 ================

    public async Task<List<LibraryFolder>> GetFoldersAsync()
    {
        var list = new List<LibraryFolder>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, parent_id FROM library_folders ORDER BY name";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new LibraryFolder
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? null : reader.GetString(2),
            });
        }
        return list;
    }

    public async Task<LibraryFolder> AddFolderAsync(string name, string? parentId = null)
    {
        var folder = new LibraryFolder { Name = name, ParentId = parentId };
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO library_folders (id, name, parent_id) VALUES (@id, @name, @pid)";
        cmd.Parameters.AddWithValue("@id", folder.Id);
        cmd.Parameters.AddWithValue("@name", folder.Name);
        cmd.Parameters.AddWithValue("@pid", (object?)folder.ParentId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return folder;
    }

    public async Task RenameFolderAsync(string folderId, string newName)
    {
        if (folderId == "root")
            throw new InvalidOperationException("不能重命名根文件夹");
        var name = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("文件夹名称不能为空");

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE library_folders SET name = @name WHERE id = @id";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@id", folderId);
        var n = await cmd.ExecuteNonQueryAsync();
        if (n == 0)
            throw new InvalidOperationException("文件夹不存在");
    }

    public async Task DeleteFolderAsync(string folderId)
    {
        if (folderId == "root") return;

        string parentId = "root";
        using (var q = _db.CreateCommand())
        {
            q.CommandText = "SELECT parent_id FROM library_folders WHERE id = @id";
            q.Parameters.AddWithValue("@id", folderId);
            var val = await q.ExecuteScalarAsync();
            if (val is string s && !string.IsNullOrWhiteSpace(s))
                parentId = s;
        }

        using var cmd = _db.CreateCommand();
        // 文献与子文件夹上移到被删文件夹的父级
        cmd.CommandText = "UPDATE library_items SET folder_id = @pid WHERE folder_id = @id";
        cmd.Parameters.AddWithValue("@pid", parentId);
        cmd.Parameters.AddWithValue("@id", folderId);
        await cmd.ExecuteNonQueryAsync();

        cmd.Parameters.Clear();
        cmd.CommandText = "UPDATE library_folders SET parent_id = @pid WHERE parent_id = @id";
        cmd.Parameters.AddWithValue("@pid", parentId);
        cmd.Parameters.AddWithValue("@id", folderId);
        await cmd.ExecuteNonQueryAsync();

        cmd.Parameters.Clear();
        cmd.CommandText = "DELETE FROM library_folders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", folderId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ================ 文献 ================

    public async Task<List<LibraryItem>> GetItemsAsync(string? folderId = null)
    {
        var list = new List<LibraryItem>();
        using var cmd = _db.CreateCommand();
        if (folderId == null || folderId == "root")
            cmd.CommandText = "SELECT * FROM library_items ORDER BY added_time DESC";
        else
        {
            cmd.CommandText = "SELECT * FROM library_items WHERE folder_id = @fid ORDER BY added_time DESC";
            cmd.Parameters.AddWithValue("@fid", folderId);
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadItem(reader));
        }
        return list;
    }

    public async Task<LibraryItem> AddItemAsync(LibraryItem item)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO library_items (id, pdf_path, name, folder_id, total_pages, ocr_done_pages, added_time, notes, output_dir, source)
            VALUES (@id, @path, @name, @fid, @total, @ocr, @time, @notes, @odir, @source)";
        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.Parameters.AddWithValue("@path", item.PdfPath);
        cmd.Parameters.AddWithValue("@name", item.Name);
        cmd.Parameters.AddWithValue("@fid", item.FolderId);
        cmd.Parameters.AddWithValue("@total", item.TotalPages);
        cmd.Parameters.AddWithValue("@ocr", item.OcrDonePages);
        cmd.Parameters.AddWithValue("@time", item.AddedTime);
        cmd.Parameters.AddWithValue("@notes", (object?)item.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@odir", (object?)item.OutputDir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source", (object?)item.Source ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return item;
    }

    public Task UpdateItemAsync(LibraryItem item)
    {
        lock (_dbGate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                UPDATE library_items SET name=@name, folder_id=@fid, total_pages=@total,
                    ocr_done_pages=@ocr, notes=@notes, output_dir=@odir, pdf_path=@path, source=@source
                WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", item.Id);
            cmd.Parameters.AddWithValue("@name", item.Name);
            cmd.Parameters.AddWithValue("@fid", item.FolderId);
            cmd.Parameters.AddWithValue("@total", item.TotalPages);
            cmd.Parameters.AddWithValue("@ocr", item.OcrDonePages);
            cmd.Parameters.AddWithValue("@notes", (object?)item.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@odir", (object?)item.OutputDir ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@path", item.PdfPath);
            cmd.Parameters.AddWithValue("@source", (object?)item.Source ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Id, int OcrDonePages)>> SilentSyncOcrDonePagesAsync(
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _silentSyncRunning, 1, 0) != 0)
            return Array.Empty<(string, int)>();

        try
        {
            // 让首屏/登录完成后再扫盘，避免启动卡顿
            await Task.Delay(800, ct).ConfigureAwait(false);

            // 只取校准所需列，避免上万文献时 SELECT * 占内存
            var pending = await LoadOcrScanRowsAsync(ct).ConfigureAwait(false);
            if (pending.Count == 0)
                return Array.Empty<(string, int)>();

            // 文献很多时压低并行度，优先保 UI 流畅
            var dop = pending.Count >= 800
                ? 1
                : pending.Count >= 200
                    ? Math.Clamp(Environment.ProcessorCount / 2, 1, 2)
                    : Math.Clamp(Environment.ProcessorCount / 2, 1, 3);

            var bag = new System.Collections.Concurrent.ConcurrentBag<(string Id, int Count)>();
            var scanned = 0;

            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                async (row, token) =>
                {
                    int count;
                    try
                    {
                        var dir = row.OutputDir;
                        count = await Task.Run(() =>
                        {
                            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                                return 0;
                            return Directory.GetFiles(dir, "page_*_ocr_result.json").Length;
                        }, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (count != row.OcrDonePages)
                        bag.Add((row.Id, count));

                    // 每扫一批让出时间片，降低大批量时的 CPU 尖峰
                    if (Interlocked.Increment(ref scanned) % 40 == 0)
                        await Task.Delay(1, token).ConfigureAwait(false);
                }).ConfigureAwait(false);

            if (bag.IsEmpty)
                return Array.Empty<(string, int)>();

            var applied = new List<(string Id, int OcrDonePages)>(bag.Count);
            var n = 0;
            foreach (var (id, count) in bag)
            {
                ct.ThrowIfCancellationRequested();
                lock (_dbGate)
                {
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText =
                        "UPDATE library_items SET ocr_done_pages=@ocr WHERE id=@id AND ocr_done_pages<>@ocr";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@ocr", count);
                    if (cmd.ExecuteNonQuery() > 0)
                        applied.Add((id, count));
                }

                // 大批量写库时更勤地让出，减轻 UI/主线程争用
                var yieldEvery = pending.Count >= 500 ? 10 : 25;
                if (++n % yieldEvery == 0)
                    await Task.Delay(1, ct).ConfigureAwait(false);
            }

            return applied;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<(string, int)>();
        }
        finally
        {
            Interlocked.Exchange(ref _silentSyncRunning, 0);
        }
    }

    private async Task<List<(string Id, int OcrDonePages, string OutputDir)>> LoadOcrScanRowsAsync(
        CancellationToken ct)
    {
        var list = new List<(string, int, string)>();
        // 读库放线程池，避免阻塞调用方
        await Task.Run(() =>
        {
            lock (_dbGate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText =
                    "SELECT id, ocr_done_pages, output_dir FROM library_items " +
                    "WHERE output_dir IS NOT NULL AND TRIM(output_dir) <> ''";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var id = reader.GetString(0);
                    var ocr = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    var dir = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (!string.IsNullOrWhiteSpace(dir))
                        list.Add((id, ocr, dir));
                }
            }
        }, ct).ConfigureAwait(false);
        return list;
    }

    public async Task MoveItemToFolderAsync(string itemId, string folderId)
    {
        var item = await GetItemAsync(itemId)
            ?? throw new InvalidOperationException("文献不存在");
        item.FolderId = string.IsNullOrWhiteSpace(folderId) ? "root" : folderId;
        await UpdateItemAsync(item);
    }

    public async Task DeleteItemAsync(string itemId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM library_items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsOutputDirInLibraryAsync(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
            return false;

        string target;
        try
        {
            target = NormalizeDir(outputDir);
        }
        catch
        {
            return false;
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT output_dir FROM library_items WHERE output_dir IS NOT NULL AND TRIM(output_dir) != ''";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0)) continue;
            try
            {
                if (string.Equals(NormalizeDir(reader.GetString(0)), target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                /* ignore bad paths */
            }
        }
        return false;
    }

    private static string NormalizeDir(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public async Task<LibraryItem?> GetItemAsync(string itemId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM library_items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return ReadItem(reader);
        return null;
    }

    private static LibraryItem ReadItem(SqliteDataReader reader)
    {
        string? GetOpt(string col)
        {
            try
            {
                var i = reader.GetOrdinal(col);
                return reader.IsDBNull(i) ? null : reader.GetString(i);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        int GetInt(string col, int fallback = 0)
        {
            var i = reader.GetOrdinal(col);
            return reader.IsDBNull(i) ? fallback : reader.GetInt32(i);
        }

        return new LibraryItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            PdfPath = reader.GetString(reader.GetOrdinal("pdf_path")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            FolderId = reader.GetString(reader.GetOrdinal("folder_id")),
            TotalPages = GetInt("total_pages"),
            OcrDonePages = GetInt("ocr_done_pages"),
            AddedTime = reader.GetString(reader.GetOrdinal("added_time")),
            Notes = GetOpt("notes"),
            OutputDir = GetOpt("output_dir"),
            Source = GetOpt("source"),
        };
    }

    public void Dispose() => _db?.Dispose();
}
