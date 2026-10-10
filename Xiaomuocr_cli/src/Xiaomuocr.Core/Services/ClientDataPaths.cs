namespace Xiaomuocr.Core.Services;

/// <summary>
/// 客户端持久化数据路径。
/// Windows：勿放在 Velopack packId 安装根（%LocalAppData%\xiaomu-ocr\）内，Setup 重装会删光。
/// 用户库统一：LocalApplicationData/xiaomu-ocr-user/data/xiaomuocr_client.db
/// （macOS 上为 ~/Library/Application Support/xiaomu-ocr-user/...）
/// 大体量 imports：若存在非 C: 的内置固定盘，优先落到该盘的 xiaomu-ocr-user\imports\；
/// 排除 U 盘、移动硬盘（USB 外接盘）及网络盘。
/// </summary>
public static class ClientDataPaths
{
    /// <summary>Velopack packId / 安装根目录名（勿在此存用户库）。</summary>
    public const string AppFolderName = "xiaomu-ocr";

    /// <summary>用户数据根目录（在安装根之外，Setup 重装不会删）。</summary>
    public const string UserDataFolderName = "xiaomu-ocr-user";

    public const string DataFolderName = "data";
    public const string BackupsFolderName = "backups";
    public const string ImportsFolderName = "imports";
    public const string DatabaseFileName = "xiaomuocr_client.db";

    /// <summary>imports 根位置标记（始终写在 LocalAppData 下，便于跨盘定位）。</summary>
    public const string ImportsLocationFileName = "imports_location.txt";

    /// <summary>外部备份最多保留份数（按修改时间，新的优先）。</summary>
    public const int MaxBackupCopies = 3;

    /// <summary>备份最长保留天数；超过则删（仍至少保留最新一份）。</summary>
    public const int MaxBackupAgeDays = 14;

    private static string? _importsRootCached;
    private static readonly object _importsGate = new();

    /// <summary>
    /// 稳定数据目录：%LocalAppData%\xiaomu-ocr-user\data\
    /// </summary>
    public static string GetDataDirectory()
    {
        var dir = Path.Combine(GetLocalUserRoot(), DataFolderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 图片/截图导入受管目录。优先非 C: 内置固定盘；不用移动硬盘/U 盘/网络盘。
    /// </summary>
    public static string GetImportsDirectory()
    {
        var dir = Path.Combine(GetImportsUserRoot(), ImportsFolderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>单篇受管导入目录：imports\{itemId}\</summary>
    public static string GetImportItemDirectory(string itemId)
    {
        var dir = Path.Combine(GetImportsDirectory(), itemId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>路径是否落在任一已知受管 imports 根下（含历史 C: 路径）。</summary>
    public static bool IsManagedImportPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var imports in EnumerateKnownImportsDirectories())
            {
                var root = Path.GetFullPath(imports)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>若 PdfPath 属于受管导入，返回其 item 根目录；否则 null。</summary>
    public static string? TryGetManagedImportItemDirectory(string? pdfPath)
    {
        if (!IsManagedImportPath(pdfPath)) return null;
        try
        {
            var full = Path.GetFullPath(pdfPath!);
            foreach (var imports in EnumerateKnownImportsDirectories())
            {
                var importsFull = Path.GetFullPath(imports);
                var rootPrefix = importsFull
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rel = Path.GetRelativePath(importsFull, full);
                var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (string.IsNullOrEmpty(first) || first == "." || first == "..")
                    return null;
                return Path.Combine(importsFull, first);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>更新前安全备份目录（同样在安装根外）。</summary>
    public static string GetBackupDirectory()
    {
        var dir = Path.Combine(GetLocalUserRoot(), BackupsFolderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 返回应使用的数据库完整路径；必要时从旧位置迁移/抢救。
    /// </summary>
    public static string GetDatabasePath()
    {
        var dest = Path.Combine(GetDataDirectory(), DatabaseFileName);
        TryMigrateOrRecover(dest);
        return dest;
    }

    /// <summary>
    /// 清理冗余库备份与 data 旁路副本。启动与更新备份后调用。
    /// </summary>
    public static void CleanupRedundantDataFiles()
    {
        try
        {
            PruneDataSidecarBackups();
            PruneExternalBackups();
        }
        catch
        {
            // 清理失败不影响正常使用
        }
    }

    /// <summary>
    /// 删除 imports 下已无对应文献库记录的孤儿目录（仅匹配受管 itemId 形目录名）。
    /// </summary>
    /// <returns>删除的目录数。</returns>
    public static int CleanupOrphanImportDirectories(IEnumerable<string> keepItemIds)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in keepItemIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
                keep.Add(id.Trim());
        }

        var removed = 0;
        try
        {
            foreach (var importsRoot in EnumerateKnownImportsDirectories())
            {
                if (!Directory.Exists(importsRoot)) continue;

                string[] dirs;
                try { dirs = Directory.GetDirectories(importsRoot); }
                catch { continue; }

                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || keep.Contains(name))
                        continue;
                    // 受管导入目录名为 12 位 hex（Guid N 截断）；其它名称一律不碰
                    if (!LooksLikeManagedItemId(name))
                        continue;
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                    catch
                    {
                        /* 占用中则跳过 */
                    }
                }
            }
        }
        catch
        {
            /* ignore */
        }

        return removed;
    }

    /// <summary>当前实际使用的 imports 根路径（可能已在非 C: 盘）。</summary>
    public static string GetImportsRootForDiagnostics()
    {
        try { return GetImportsDirectory(); }
        catch { return "(unavailable)"; }
    }

    /// <summary>
    /// 旧路径（v1.1.25 及更早）：%LocalAppData%\xiaomu-ocr\data\
    /// 位于 Velopack 安装根内，Setup 重装会被整夹删除。
    /// </summary>
    public static string GetLegacyVelopackDataDbPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, AppFolderName, DataFolderName, DatabaseFileName);
    }

    /// <summary>
    /// 更旧路径：安装目录 current\xiaomuocr_client.db（更新会被覆盖）。
    /// </summary>
    public static string GetLegacyDatabasePath()
        => Path.Combine(AppContext.BaseDirectory, DatabaseFileName);

    /// <summary>
    /// 更新安装前调用（须在关闭所有 SQLite 连接之后）：
    /// 把当前库备份到安装根之外的 backups\（Setup 删不掉）。
    /// 不再在 data\ 旁双写 .preupdate（与 backups 冗余）。
    /// </summary>
    public static void BackupBeforeUpdate()
    {
        try
        {
            var dest = Path.Combine(GetDataDirectory(), DatabaseFileName);
            // 若新位置尚空，先尝试从旧 Velopack data 抢救到新位置再备份
            TryMigrateOrRecover(dest);
            CheckpointWal(dest);

            var destScore = ScoreDatabase(dest);
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");

            if (destScore.libraryItems > 0 || destScore.size > 0)
            {
                var external = Path.Combine(GetBackupDirectory(), $"xiaomuocr_client.preupdate-{stamp}.db");
                TryCopyDb(dest, external, overwrite: true);
                CleanupRedundantDataFiles();
                return;
            }

            // 新位置仍空：直接从旧位置做外部备份（哪怕主库暂时还没迁过来）
            foreach (var candidate in EnumerateCandidateDatabases())
            {
                if (PathsEqual(candidate, dest)) continue;
                var s = ScoreDatabase(candidate);
                if (s.libraryItems <= 0) continue;
                var external = Path.Combine(GetBackupDirectory(), $"xiaomuocr_client.preupdate-{stamp}.db");
                TryCopyDb(candidate, external, overwrite: true);
                TryCopyDb(candidate, dest, overwrite: true);
                CleanupRedundantDataFiles();
                return;
            }
        }
        catch
        {
            // 备份失败不阻断更新
        }
    }

    // ------------------------------------------------------------------
    // 路径解析：LocalAppData 根 / imports 根（可非 C:）
    // ------------------------------------------------------------------

    private static string GetLocalUserRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, UserDataFolderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>imports 所在的用户根（可能是 D:\xiaomu-ocr-user 等）。</summary>
    private static string GetImportsUserRoot()
    {
        lock (_importsGate)
        {
            if (!string.IsNullOrEmpty(_importsRootCached))
                return _importsRootCached;

            _importsRootCached = ResolveImportsUserRoot();
            return _importsRootCached;
        }
    }

    private static string ResolveImportsUserRoot()
    {
        var markerPath = Path.Combine(GetLocalUserRoot(), ImportsLocationFileName);
        var localRoot = GetLocalUserRoot();

        // 1) 有非 C: 内置固定盘 → 优先（排除移动硬盘/USB 外接盘）
        var offC = TryPickOffSystemDriveUserRoot();
        if (!string.IsNullOrEmpty(offC) && TryEnsureWritableDirectory(offC))
        {
            TryWriteImportsLocation(markerPath, offC);
            return offC;
        }

        // 2) 已有标记且可写，且不在移动硬盘/外接盘上
        var marked = TryReadImportsLocation(markerPath);
        if (!string.IsNullOrEmpty(marked)
            && IsEligibleImportsUserRoot(marked)
            && TryEnsureWritableDirectory(marked))
            return marked;

        // 3) 回退 LocalAppData（标记曾指向移动盘时会走到这里并重写标记）
        TryWriteImportsLocation(markerPath, localRoot);
        return localRoot;
    }

    private static string? TryPickOffSystemDriveUserRoot()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (!IsEligibleOffSystemImportsDrive(d)) continue;
                    var letter = d.Name.TrimEnd('\\', '/');
                    if (letter.Equals("C:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (best == null || d.AvailableFreeSpace > best.AvailableFreeSpace)
                        best = d;
                }
                catch
                {
                    /* skip drive */
                }
            }

            if (best == null) return null;
            return Path.Combine(best.RootDirectory.FullName, UserDataFolderName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>imports 用户根是否可用（LocalAppData 或内置固定盘；拒绝移动硬盘等）。</summary>
    private static bool IsEligibleImportsUserRoot(string userRoot)
    {
        if (string.IsNullOrWhiteSpace(userRoot)) return false;
        try
        {
            var full = Path.GetFullPath(userRoot);
            if (PathsEqual(full, GetLocalUserRoot()))
                return true;

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            return IsEligibleOffSystemImportsDrive(new DriveInfo(root));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>可作为 imports 的非 C: 盘：内置 Fixed，非 USB/Removable/Network。</summary>
    private static bool IsEligibleOffSystemImportsDrive(DriveInfo drive)
    {
        if (!drive.IsReady) return false;
        if (drive.DriveType != DriveType.Fixed) return false;

        var letter = drive.Name.TrimEnd('\\', '/');
        if (letter.Equals("C:", StringComparison.OrdinalIgnoreCase))
            return false;

        return !IsExternallyAttachedDrive(drive);
    }

    /// <summary>是否 U 盘、移动硬盘（USB 外接）等不宜存放 imports 的盘。</summary>
    private static bool IsExternallyAttachedDrive(DriveInfo drive)
    {
        if (drive.DriveType is DriveType.Removable or DriveType.Network or DriveType.CDRom)
            return true;

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return IsWindowsUsbAttachedDrive(drive.Name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>通过 WMI 判断盘符是否落在 USB 物理磁盘上（含报告为 Fixed 的移动硬盘）。</summary>
    private static bool IsWindowsUsbAttachedDrive(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\', '/');
        if (letter.Length >= 2 && letter[^1] == ':')
            letter = letter[..2];

        using var diskSearcher = new System.Management.ManagementObjectSearcher(
            "SELECT DeviceID, InterfaceType FROM Win32_DiskDrive WHERE InterfaceType='USB'");
        foreach (System.Management.ManagementObject disk in diskSearcher.Get())
        {
            var diskId = disk["DeviceID"]?.ToString();
            if (string.IsNullOrEmpty(diskId)) continue;

            var escapedDisk = diskId.Replace("\\", "\\\\");
            using var partSearcher = new System.Management.ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escapedDisk}'}} " +
                "WHERE AssocClass=Win32_DiskDriveToDiskPartition");
            foreach (System.Management.ManagementObject partition in partSearcher.Get())
            {
                var partId = partition["DeviceID"]?.ToString();
                if (string.IsNullOrEmpty(partId)) continue;

                var escapedPart = partId.Replace("\\", "\\\\");
                using var logicalSearcher = new System.Management.ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{escapedPart}'}} " +
                    "WHERE AssocClass=Win32_LogicalDiskToPartition");
                foreach (System.Management.ManagementObject logical in logicalSearcher.Get())
                {
                    var logicalId = logical["DeviceID"]?.ToString();
                    if (string.Equals(logicalId, letter, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool TryEnsureWritableDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadImportsLocation(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath)) return null;
            var line = File.ReadAllText(markerPath).Trim();
            if (string.IsNullOrWhiteSpace(line)) return null;
            return Path.GetFullPath(line);
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteImportsLocation(string markerPath, string root)
    {
        try
        {
            File.WriteAllText(markerPath, Path.GetFullPath(root));
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>当前 imports + 历史 LocalAppData imports（识别旧文献路径）。</summary>
    private static IEnumerable<string> EnumerateKnownImportsDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>(3);

        void Consider(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var full = Path.GetFullPath(dir);
                if (seen.Add(full))
                    list.Add(full);
            }
            catch
            {
                /* ignore */
            }
        }

        Consider(Path.Combine(GetImportsUserRoot(), ImportsFolderName));
        Consider(Path.Combine(GetLocalUserRoot(), ImportsFolderName));

        try
        {
            var markedRoot = TryReadImportsLocation(
                Path.Combine(GetLocalUserRoot(), ImportsLocationFileName));
            if (!string.IsNullOrEmpty(markedRoot))
                Consider(Path.Combine(markedRoot, ImportsFolderName));
        }
        catch
        {
            /* ignore */
        }

        return list;
    }

    // ------------------------------------------------------------------
    // 冗余清理
    // ------------------------------------------------------------------

    /// <summary>删除 data\ 旁的 .preupdate-*（与 backups\ 重复）。</summary>
    private static void PruneDataSidecarBackups()
    {
        var dataDir = GetDataDirectory();
        if (!Directory.Exists(dataDir)) return;

        foreach (var f in Directory.EnumerateFiles(dataDir, DatabaseFileName + ".*"))
        {
            var name = Path.GetFileName(f);
            // 保留主库相关 wal/shm：xiaomuocr_client.db-wal / .db-shm
            if (name.Equals(DatabaseFileName + "-wal", StringComparison.OrdinalIgnoreCase)
                || name.Equals(DatabaseFileName + "-shm", StringComparison.OrdinalIgnoreCase))
                continue;

            // xiaomuocr_client.db.preupdate-... 及误拷的 wal/shm
            if (name.Contains(".preupdate", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".bak", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFileAndSidecars(f);
            }
        }
    }

    /// <summary>backups\ 只保留最近 MaxBackupCopies，并丢弃过期文件。</summary>
    private static void PruneExternalBackups()
    {
        var backupDir = GetBackupDirectory();
        if (!Directory.Exists(backupDir)) return;

        var files = Directory.EnumerateFiles(backupDir, "xiaomuocr_client*.db")
            .Select(p =>
            {
                try { return (Path: p, Time: File.GetLastWriteTimeUtc(p)); }
                catch { return (Path: p, Time: DateTime.MinValue); }
            })
            .OrderByDescending(x => x.Time)
            .ToList();

        if (files.Count == 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-MaxBackupAgeDays);
        for (var i = 0; i < files.Count; i++)
        {
            var keepByCount = i < MaxBackupCopies;
            var keepByAge = files[i].Time >= cutoff;
            // 至少保留最新 1 份；其余：超出份数或过期则删
            var keep = i == 0 || (keepByCount && keepByAge);
            if (!keep)
                TryDeleteFileAndSidecars(files[i].Path);
        }
    }

    private static void TryDeleteFileAndSidecars(string path)
    {
        TryDeleteQuiet(path);
        TryDeleteQuiet(path + "-wal");
        TryDeleteQuiet(path + "-shm");
        // 旁路命名：xxx.db-wal 已覆盖；另有 File.Copy 成 dest-wal 的情况
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(name))
        {
            TryDeleteQuiet(Path.Combine(dir, name + "-wal"));
            TryDeleteQuiet(Path.Combine(dir, name + "-shm"));
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static bool LooksLikeManagedItemId(string name)
    {
        if (name.Length is < 8 or > 32) return false;
        foreach (var c in name)
        {
            var ok = (c >= '0' && c <= '9')
                     || (c >= 'a' && c <= 'f')
                     || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // 迁移 / 评分 / 拷贝（原逻辑）
    // ------------------------------------------------------------------

    private static void TryMigrateOrRecover(string destPath)
    {
        try
        {
            var destScore = ScoreDatabase(destPath);

            // 新位置已有文献 → 绝对不要被别处覆盖
            if (destScore.libraryItems > 0)
                return;

            var best = PickRichestDatabase(
                EnumerateCandidateDatabases()
                    .Where(p => !PathsEqual(p, destPath)));

            if (best == null || best.Value.libraryItems <= 0)
                return;

            // 只接受「真实文献数更多」的库；安装目录种子（0 条）永远不会覆盖
            if (!File.Exists(destPath) || best.Value.libraryItems > destScore.libraryItems)
            {
                if (!PathsEqual(best.Value.path, destPath))
                    TryCopyDb(best.Value.path, destPath, overwrite: true);
            }
        }
        catch
        {
            // 迁移失败则继续用现有/空库，避免阻断启动
        }
    }

    private static IEnumerable<string> EnumerateCandidateDatabases()
    {
        // 1) 旧 Velopack 安装根内的 data（最常见的待迁移源）
        yield return GetLegacyVelopackDataDbPath();

        // 2) current\ 旁遗留
        yield return GetLegacyDatabasePath();

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 3) 新用户数据目录 + 旁路 .preupdate（清理前仍可能存在，作抢救源）
        var newDataDir = Path.Combine(local, UserDataFolderName, DataFolderName);
        yield return Path.Combine(newDataDir, DatabaseFileName);
        if (Directory.Exists(newDataDir))
        {
            foreach (var f in Directory.EnumerateFiles(newDataDir, DatabaseFileName + ".*"))
                yield return f;
        }

        // 4) 外部 backups\
        var backupDir = Path.Combine(local, UserDataFolderName, BackupsFolderName);
        if (Directory.Exists(backupDir))
        {
            foreach (var f in Directory.EnumerateFiles(backupDir, "xiaomuocr_client*.db"))
                yield return f;
        }

        // 5) 旧安装根下所有同名库（含 data\.preupdate，若尚未被 Setup 删掉）
        var packRoot = Path.Combine(local, AppFolderName);
        if (Directory.Exists(packRoot))
        {
            string[] hits;
            try
            {
                hits = Directory.GetFiles(packRoot, DatabaseFileName, SearchOption.AllDirectories);
            }
            catch
            {
                hits = Array.Empty<string>();
            }
            foreach (var f in hits)
                yield return f;

            var oldDataDir = Path.Combine(packRoot, DataFolderName);
            if (Directory.Exists(oldDataDir))
            {
                foreach (var f in Directory.EnumerateFiles(oldDataDir, DatabaseFileName + ".*"))
                    yield return f;
            }
        }
    }

    private static (string path, int libraryItems, long size)? PickRichestDatabase(IEnumerable<string> paths)
    {
        (string path, int libraryItems, long size)? best = null;
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(p)) continue;
            var s = ScoreDatabase(p);
            // 安装目录 current\ 下的 0 文献种子永不选用
            if (IsUnderAppContentDir(p) && s.libraryItems <= 0)
                continue;

            if (best == null
                || s.libraryItems > best.Value.libraryItems
                || (s.libraryItems == best.Value.libraryItems && s.size > best.Value.size))
            {
                best = (p, s.libraryItems, s.size);
            }
        }
        return best;
    }

    /// <summary>
    /// 只统计真实 library_items 行数。禁止把「仅有 settings」的种子库伪装成有文献。
    /// </summary>
    private static (int libraryItems, long size) ScoreDatabase(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (0, 0);
            var size = new FileInfo(path).Length;
            if (size < 512)
                return (0, size);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='library_items'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                return (0, size);

            cmd.CommandText = "SELECT COUNT(*) FROM library_items";
            var n = Convert.ToInt32(cmd.ExecuteScalar());
            return (n, size);
        }
        catch
        {
            try { return (0, new FileInfo(path).Length); }
            catch { return (0, 0); }
        }
    }

    private static void CheckpointWal(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath)) return;
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private static void TryCopyDb(string src, string dest, bool overwrite = false)
    {
        if (!File.Exists(src)) return;
        if (PathsEqual(src, dest)) return;
        if (File.Exists(dest) && !overwrite) return;

        // 安全阀：绝不把 0 文献的 current\ 种子覆盖到主库
        var destIsMain = PathsEqual(dest, Path.Combine(GetDataDirectory(), DatabaseFileName));
        if (destIsMain && IsUnderAppContentDir(src) && ScoreDatabase(src).libraryItems <= 0)
            return;

        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        File.Copy(src, dest, overwrite);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var s = src + suffix;
            var d = dest + suffix;
            if (File.Exists(s))
            {
                try { File.Copy(s, d, overwrite); }
                catch { }
            }
            else if (overwrite && File.Exists(d))
            {
                try { File.Delete(d); } catch { }
            }
        }
    }

    /// <summary>是否位于 Velopack current\（随更新整夹替换）。</summary>
    private static bool IsUnderAppContentDir(string path)
    {
        try
        {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            return full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(baseDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Path.GetDirectoryName(full), baseDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
