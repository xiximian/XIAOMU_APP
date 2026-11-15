using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Xiaomuocr.Core.Services;

public sealed class SearchIndexService : ISearchIndexService
{
    public const int DefaultPageThreshold = 1000;

    private readonly object _gate = new();

    public int PageThreshold => DefaultPageThreshold;

    public bool ShouldUseIndex(int totalPages) => totalPages > PageThreshold;

    public bool IsIndexReady(string documentId, string jsonDir, int totalPages)
    {
        if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(jsonDir))
            return false;

        var dbPath = ClientDataPaths.GetSearchIndexDbPath(documentId);
        if (!File.Exists(dbPath)) return false;

        var expected = BuildFingerprint(jsonDir, totalPages);
        try
        {
            lock (_gate)
            {
                using var conn = Open(dbPath, readOnly: true);
                var actual = ReadMeta(conn, "fingerprint");
                return string.Equals(actual, expected, StringComparison.Ordinal);
            }
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureIndexAsync(
        string documentId,
        string jsonDir,
        int totalPages,
        IProgress<(int done, int total, string message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("documentId 为空", nameof(documentId));
        if (string.IsNullOrWhiteSpace(jsonDir) || !Directory.Exists(jsonDir))
            throw new DirectoryNotFoundException($"OCR 目录不存在: {jsonDir}");

        if (IsIndexReady(documentId, jsonDir, totalPages))
        {
            progress?.Report((totalPages, totalPages, "检索索引已就绪"));
            return;
        }

        var dbPath = ClientDataPaths.GetSearchIndexDbPath(documentId);
        var tmpPath = dbPath + ".building";
        try
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
        catch { /* ignore */ }

        progress?.Report((0, Math.Max(1, totalPages), "正在建立检索索引…"));

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildIndexFile(tmpPath, jsonDir, totalPages, progress, cancellationToken);

            lock (_gate)
            {
                // 替换正式库
                try
                {
                    if (File.Exists(dbPath)) File.Delete(dbPath);
                }
                catch { /* ignore */ }

                File.Move(tmpPath, dbPath);
            }
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report((totalPages, Math.Max(1, totalPages), "检索索引已建立"));
    }

    public IReadOnlyList<IndexedTextBlock>? TryLoadBlocks(string documentId, string jsonDir, int totalPages)
    {
        if (!IsIndexReady(documentId, jsonDir, totalPages))
            return null;

        var dbPath = ClientDataPaths.GetSearchIndexDbPath(documentId);
        try
        {
            lock (_gate)
            {
                using var conn = Open(dbPath, readOnly: true);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT page_index, block_index, text FROM blocks ORDER BY page_index, block_index;";
                var list = new List<IndexedTextBlock>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new IndexedTextBlock
                    {
                        PageIndex = reader.GetInt32(0),
                        BlockIndex = reader.GetInt32(1),
                        Text = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    });
                }
                return list;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void BuildIndexFile(
        string dbPath,
        string jsonDir,
        int totalPages,
        IProgress<(int done, int total, string message)>? progress,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var conn = Open(dbPath, readOnly: false);
        EnsureSchema(conn);

        using (var clear = conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM blocks; DELETE FROM meta;";
            clear.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO blocks(page_index, block_index, text, text_simp) VALUES($p,$b,$t,$s);";
        var pP = insert.Parameters.Add("$p", SqliteType.Integer);
        var pB = insert.Parameters.Add("$b", SqliteType.Integer);
        var pT = insert.Parameters.Add("$t", SqliteType.Text);
        var pS = insert.Parameters.Add("$s", SqliteType.Text);

        var pages = Math.Max(0, totalPages);
        for (var pageIdx = 0; pageIdx < pages; pageIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var jsonPath = Path.Combine(jsonDir, $"page_{pageIdx + 1}_ocr_result.json");
            if (File.Exists(jsonPath))
            {
                var blocks = OcrJsonTextLoader.LoadPageTexts(jsonPath);
                for (var i = 0; i < blocks.Count; i++)
                {
                    var text = blocks[i] ?? "";
                    if (string.IsNullOrEmpty(text)) continue;
                    pP.Value = pageIdx;
                    pB.Value = i;
                    pT.Value = text;
                    pS.Value = ChineseTextConverter.ToSimplified(text);
                    insert.ExecuteNonQuery();
                }
            }

            if (pageIdx % 25 == 0 || pageIdx + 1 == pages)
            {
                progress?.Report((pageIdx + 1, pages,
                    $"正在建立检索索引… {pageIdx + 1}/{pages}"));
            }
        }

        var fingerprint = BuildFingerprint(jsonDir, totalPages);
        WriteMeta(conn, tx, "fingerprint", fingerprint);
        WriteMeta(conn, tx, "json_dir", Path.GetFullPath(jsonDir));
        WriteMeta(conn, tx, "total_pages", pages.ToString(CultureInfo.InvariantCulture));
        WriteMeta(conn, tx, "built_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        tx.Commit();

        using var vacuum = conn.CreateCommand();
        vacuum.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        try { vacuum.ExecuteNonQuery(); } catch { /* ignore */ }
    }

    /// <summary>OCR 指纹：页数 + 已有结果文件数量 + 最大修改时间 + 目录路径哈希。</summary>
    private static string BuildFingerprint(string jsonDir, int totalPages)
    {
        long maxTicks = 0;
        var fileCount = 0;
        try
        {
            if (Directory.Exists(jsonDir))
            {
                foreach (var f in Directory.EnumerateFiles(jsonDir, "page_*_ocr_result.json"))
                {
                    fileCount++;
                    try
                    {
                        var t = File.GetLastWriteTimeUtc(f).Ticks;
                        if (t > maxTicks) maxTicks = t;
                    }
                    catch { /* skip */ }
                }
            }
        }
        catch { /* skip */ }

        var dirKey = "";
        try { dirKey = Path.GetFullPath(jsonDir); } catch { dirKey = jsonDir; }

        var raw = $"{totalPages}|{fileCount}|{maxTicks}|{dirKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private static SqliteConnection Open(string dbPath, bool readOnly)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        if (!readOnly)
        {
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }
        }
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS meta (
              key TEXT PRIMARY KEY NOT NULL,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS blocks (
              page_index INTEGER NOT NULL,
              block_index INTEGER NOT NULL,
              text TEXT NOT NULL,
              text_simp TEXT NOT NULL,
              PRIMARY KEY (page_index, block_index)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? ReadMeta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES($k,$v);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
