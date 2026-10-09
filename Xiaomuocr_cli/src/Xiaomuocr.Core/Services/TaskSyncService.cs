using System.Collections.ObjectModel;
using ReactiveUI;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>任务中心条目（侧栏卡片 / 任务中心列表共用）。</summary>
public class RecognitionTaskItem : ReactiveObject
{
    private string _batchUuid = "";
    public string BatchUuid
    {
        get => _batchUuid;
        set => this.RaiseAndSetIfChanged(ref _batchUuid, value);
    }

    private string _documentName = "";
    public string DocumentName
    {
        get => _documentName;
        set
        {
            this.RaiseAndSetIfChanged(ref _documentName, value);
            this.RaisePropertyChanged(nameof(DisplayName));
        }
    }

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(DocumentName) ? "未命名文献" : DocumentName;
            return name.Length > 18 ? name[..16] + "…" : name;
        }
    }

    private int _totalPages;
    public int TotalPages
    {
        get => _totalPages;
        set
        {
            this.RaiseAndSetIfChanged(ref _totalPages, value);
            this.RaisePropertyChanged(nameof(PagesLabel));
            this.RaisePropertyChanged(nameof(ProgressText));
        }
    }

    private int _completedPages;
    public int CompletedPages
    {
        get => _completedPages;
        set
        {
            this.RaiseAndSetIfChanged(ref _completedPages, value);
            this.RaisePropertyChanged(nameof(ProgressText));
        }
    }

    private int _failedPages;
    public int FailedPages
    {
        get => _failedPages;
        set => this.RaiseAndSetIfChanged(ref _failedPages, value);
    }

    private int _cancelledPages;
    public int CancelledPages
    {
        get => _cancelledPages;
        set => this.RaiseAndSetIfChanged(ref _cancelledPages, value);
    }

    private int _uploadedPages;
    public int UploadedPages
    {
        get => _uploadedPages;
        set
        {
            this.RaiseAndSetIfChanged(ref _uploadedPages, value);
            this.RaisePropertyChanged(nameof(ProgressText));
        }
    }

    private string _status = "queued";
    public string Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(ProgressText));
            this.RaisePropertyChanged(nameof(CanCancel));
            this.RaisePropertyChanged(nameof(IsActive));
        }
    }

    private string _outputDir = "";
    public string OutputDir
    {
        get => _outputDir;
        set => this.RaiseAndSetIfChanged(ref _outputDir, value);
    }

    private string _failReason = "";
    public string FailReason
    {
        get => _failReason;
        set
        {
            this.RaiseAndSetIfChanged(ref _failReason, value);
            this.RaisePropertyChanged(nameof(ProgressText));
        }
    }

    private double _costTotal;
    public double CostTotal
    {
        get => _costTotal;
        set => this.RaiseAndSetIfChanged(ref _costTotal, value);
    }

    private string? _updatedAt;
    public string? UpdatedAt
    {
        get => _updatedAt;
        set => this.RaiseAndSetIfChanged(ref _updatedAt, value);
    }

    private string? _createdAt;
    public string? CreatedAt
    {
        get => _createdAt;
        set
        {
            this.RaiseAndSetIfChanged(ref _createdAt, value);
            this.RaisePropertyChanged(nameof(SubmittedAtLabel));
            this.RaisePropertyChanged(nameof(TimeLineLabel));
        }
    }

    private string? _completedAt;
    public string? CompletedAt
    {
        get => _completedAt;
        set
        {
            this.RaiseAndSetIfChanged(ref _completedAt, value);
            this.RaisePropertyChanged(nameof(CompletedAtLabel));
            this.RaisePropertyChanged(nameof(TimeLineLabel));
        }
    }

    /// <summary>提交时间展示。</summary>
    public string SubmittedAtLabel => FormatTimeLabel(CreatedAt);

    /// <summary>完成时间展示；未完成显示 —。</summary>
    public string CompletedAtLabel =>
        string.IsNullOrEmpty(CompletedAt) ? "—" : FormatTimeLabel(CompletedAt);

    /// <summary>任务卡片时间行。</summary>
    public string TimeLineLabel =>
        $"提交：{SubmittedAtLabel}    完成：{CompletedAtLabel}";

    public string PagesLabel => $"{TotalPages} 页";

    /// <summary>列表排序键：越近越大（UpdatedAt → CompletedAt → CreatedAt）。</summary>
    public DateTimeOffset RecencySortKey =>
        TryParseTime(UpdatedAt)
        ?? TryParseTime(CompletedAt)
        ?? TryParseTime(CreatedAt)
        ?? DateTimeOffset.MinValue;

    private static string FormatTimeLabel(string? iso)
    {
        if (TryParseTime(iso) is { } dto)
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (string.IsNullOrWhiteSpace(iso)) return "—";
        return iso.Length > 16 ? iso[..16].Replace('T', ' ') : iso;
    }

    internal static DateTimeOffset? TryParseTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        var s = iso.Trim();
        // 服务端存 naive UTC；旧接口无 Z。无显式时区时按 UTC 解读再转本地，
        // 否则中国区会把 UTC 墙钟当本地显示（慢约 8 小时）。
        if (!HasExplicitTimeZone(s))
            s += "Z";
        if (DateTimeOffset.TryParse(
                s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dto))
            return dto;
        return null;
    }

    private static bool HasExplicitTimeZone(string s)
    {
        if (s.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            return true;
        var t = s.IndexOf('T');
        if (t < 0) t = s.IndexOf(' ');
        if (t < 0) return false;
        // 2026-07-30T03:17:05+08:00 / ...-05:00
        return s.IndexOf('+', t) >= 0 || s.LastIndexOf('-') > t;
    }

    public bool IsActive => Status is "queued_upload" or "uploading" or "queued" or "processing";
    public bool CanCancel => Status is "queued" or "processing";

    public string ProgressText => Status switch
    {
        "queued_upload" => "排队上传",
        "uploading" => $"上传 {UploadedPages}/{TotalPages}",
        "queued" or "processing" =>
            CompletedPages > 0
                ? $"已上传·识别中 {CompletedPages}/{TotalPages}"
                : "已上传·识别中",
        // 终态但未落盘：绝不显示「识别完成」，避免底栏/任务中心抢跑
        "completed" => LocalSyncVerified
            ? "识别完成"
            : CompletedPages > 0
                ? $"结果同步中 {CompletedPages}/{Math.Max(TotalPages, CompletedPages)}"
                : "结果同步中…",
        "partial_failed" => LocalSyncVerified
            ? $"部分完成 {CompletedPages}/{TotalPages}"
            : $"结果同步中 {CompletedPages}/{Math.Max(TotalPages, 1)}",
        "cancelled" => CancelledPages > 0
            ? $"已取消（完成 {CompletedPages}）"
            : "已取消",
        "failed" => string.IsNullOrEmpty(FailReason) ? "失败" : $"失败: {FailReason}",
        _ => Status,
    };

    private bool _localSyncVerified;
    /// <summary>
    /// 本批次已完成页是否已确认落盘。禁止用目录全局 JSON 数对比 CompletedPages。
    /// </summary>
    public bool LocalSyncVerified
    {
        get => _localSyncVerified;
        set
        {
            if (_localSyncVerified == value) return;
            _localSyncVerified = value;
            this.RaisePropertyChanged(nameof(LocalSyncVerified));
            this.RaisePropertyChanged(nameof(ProgressText));
        }
    }

    public void ApplyFromListItem(BatchListItem item, string? outputDir = null)
    {
        BatchUuid = item.BatchUuid;
        DocumentName = item.DocumentName ?? "";
        TotalPages = item.TotalPages;
        CompletedPages = item.CompletedPages;
        FailedPages = item.FailedPages;
        CancelledPages = item.CancelledPages;
        Status = item.Status;
        CostTotal = item.CostTotal;
        CreatedAt = item.CreatedAt;
        UpdatedAt = item.UpdatedAt ?? item.CreatedAt;
        CompletedAt = item.CompletedAt;
        if (!string.IsNullOrEmpty(outputDir))
            OutputDir = outputDir;
        if (Status is "queued" or "processing")
            LocalSyncVerified = false;
    }

    public void ApplyFromStatus(BatchStatusResponse status)
    {
        Status = status.Status;
        TotalPages = status.TotalPages;
        CompletedPages = status.CompletedPages;
        FailedPages = status.FailedPages;
        CancelledPages = status.CancelledPages;
        CostTotal = status.CostTotal;
        if (!string.IsNullOrEmpty(status.DocumentName))
            DocumentName = status.DocumentName;
        UpdatedAt = DateTime.UtcNow.ToString("o");
        // 进行中绝不能保留「已验证落盘」，否则底栏会提前显示「识别完成」
        if (Status is "queued" or "processing" or "queued_upload" or "uploading")
            LocalSyncVerified = false;
        // 不向用户展示后端 OCR 具体报错文案
    }
}

public interface ITaskSyncService
{
    ObservableCollection<RecognitionTaskItem> Tasks { get; }
    ObservableCollection<RecognitionTaskItem> RecentTasks { get; }
    event Action? TasksChanged;
    event Action<string>? StatusBarMessage;
    event Action<string>? ResultsSynced;

    Task StartAsync();
    void Stop();
    Task RefreshAsync();
    /// <summary>主动同步：强制对本地缺页的任务拉 status 并落盘。返回状态文案。</summary>
    Task<string> SyncLocalResultsAsync();
    Task CancelAsync(string batchUuid);

    /// <summary>文献已从库中删除：停止该输出目录的结果拉取与落盘。</summary>
    void AbandonOutputDir(string? outputDir);

    string BeginLocalUpload(string documentName, int totalPages, string outputDir);
    void MarkLocalQueued(string localId);
    void MarkLocalUploading(string localId);
    void UpdateLocalUpload(string localId, int uploadedPages);
    void FailLocalUpload(string localId, string reason);
    Task TrackSubmittedAsync(string? localId, BatchSubmitResponse response,
        string documentName, string outputDir, int uploadedPages);
    Task SyncResultsToDiskAsync(string batchUuid, string outputDir, BatchStatusResponse status);

    /// <summary>
    /// 本地阻塞轮询结束时回写任务状态，使左下角「最近任务」与底栏同时变为完成。
    /// </summary>
    void NotifyBatchSettled(
        string batchUuid, string status, int completedPages, int failedPages, int cancelledPages,
        bool localSyncVerified);
}

/// <summary>
/// 全局任务同步：登录后拉列表、活跃任务 5s 轮询、空闲 60s 轻量刷新；服务端权威。
/// </summary>
public class TaskSyncService : ITaskSyncService, IDisposable
{
    private readonly IBatchOcrService _batchOcr;
    private readonly IBatchCacheService _cache;
    private readonly ILibraryService? _library;
    private readonly IAppLogService? _log;

    private CancellationTokenSource? _loopCts;
    private SynchronizationContext? _uiCtx;
    private readonly object _gate = new();
    private readonly Dictionary<string, RecognitionTaskItem> _byUuid = new();
    private readonly HashSet<string> _abandonedDirs = new(StringComparer.OrdinalIgnoreCase);
    private int _localSeq;

    public ObservableCollection<RecognitionTaskItem> Tasks { get; } = new();
    public ObservableCollection<RecognitionTaskItem> RecentTasks { get; } = new();

    public event Action? TasksChanged;
    public event Action<string>? StatusBarMessage;
    public event Action<string>? ResultsSynced;

    public TaskSyncService(
        IBatchOcrService batchOcr,
        IBatchCacheService cache,
        ILibraryService? library = null,
        IAppLogService? log = null)
    {
        _batchOcr = batchOcr;
        _cache = cache;
        _library = library;
        _log = log;
    }

    public Task StartAsync()
    {
        _uiCtx = SynchronizationContext.Current;
        StopLoopOnly();
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _ = Task.Run(() => LoopAsync(ct), ct);
        return RefreshAsync();
    }

    public void Stop()
    {
        StopLoopOnly();
        UiPost(() =>
        {
            Tasks.Clear();
            RecentTasks.Clear();
        });
        lock (_gate) { _byUuid.Clear(); }
    }

    private void StopLoopOnly()
    {
        try { _loopCts?.Cancel(); } catch { }
        _loopCts?.Dispose();
        _loopCts = null;
    }

    public void Dispose() => Stop();

    private void UiPost(Action action)
    {
        if (_uiCtx != null)
            _uiCtx.Post(_ => action(), null);
        else
            action();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool needsFastPoll;
                lock (_gate)
                {
                    needsFastPoll = _byUuid.Values.Any(t =>
                        !t.BatchUuid.StartsWith("local:", StringComparison.Ordinal)
                        && NeedsStatusPoll(t));
                }
                await Task.Delay(needsFastPoll ? 5000 : 60000, ct);
                if (ct.IsCancellationRequested) break;
                await RefreshAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log?.Warn($"TaskSync 循环异常: {ex.Message}");
            }
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            BatchListResponse? list = null;
            try
            {
                list = await _batchOcr.ListBatchesAsync(50, 0);
            }
            catch (Exception ex)
            {
                _log?.Warn($"TaskSync list 失败: {ex.Message}");
            }

            var localCache = await _cache.LoadAllBatchesAsync();
            var outputMap = localCache.ToDictionary(c => c.BatchUuid, c => c.OutputDir);

            if (list != null)
            {
                foreach (var item in list.Batches)
                {
                    outputMap.TryGetValue(item.BatchUuid, out var dir);
                    UpsertFromServer(item, dir);
                }
            }

            // 进行中：照常拉状态；已完成但本地缺页：补拉一次落盘（重启后常见）
            List<RecognitionTaskItem> toPoll;
            lock (_gate)
            {
                toPoll = _byUuid.Values
                    .Where(t => !t.BatchUuid.StartsWith("local:", StringComparison.Ordinal)
                                && NeedsStatusPoll(t))
                    .ToList();
            }

            foreach (var task in toPoll)
            {
                try
                {
                    var wasVerified = task.LocalSyncVerified;
                    var wasActive = task.IsActive;
                    await PollAndSaveTaskAsync(task);
                    // 底栏与任务卡片必须同源 ProgressText；仅进行中刷新，或刚确认落盘完成时提示
                    if (ShouldPublishStatusBar(task, wasActive, wasVerified))
                        StatusBarMessage?.Invoke(FormatStatusBar(task));
                }
                catch (Exception ex)
                {
                    var shortId = task.BatchUuid.Length > 8 ? task.BatchUuid[..8] : task.BatchUuid;
                    _log?.Warn($"TaskSync status {shortId}: {ex.Message}");
                }
            }

            try
            {
                var pending = await _batchOcr.GetPendingBatchesAsync();
                foreach (var p in pending)
                {
                    bool exists;
                    lock (_gate) { exists = _byUuid.ContainsKey(p.BatchUuid); }
                    if (exists) continue;

                    outputMap.TryGetValue(p.BatchUuid, out var dir);
                    var item = new RecognitionTaskItem
                    {
                        BatchUuid = p.BatchUuid,
                        DocumentName = p.DocumentName ?? "",
                        TotalPages = p.TotalPages,
                        CompletedPages = p.CompletedPages > 0 ? p.CompletedPages : p.ReceivedCount,
                        FailedPages = p.FailedPages,
                        Status = p.Status,
                        OutputDir = dir ?? "",
                        LocalSyncVerified = false,
                    };
                    InsertOrUpdate(item);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        await _cache.SaveBatchAsync(
                            p.BatchUuid, p.DocumentName ?? "", p.TotalPages, dir, p.Status,
                            item.CompletedPages, p.FailedPages);
                    }
                }
            }
            catch { /* resume 可选 */ }

            RebuildRecent();
            TasksChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.Error($"TaskSync Refresh 失败: {ex.Message}");
        }
    }

    public void AbandonOutputDir(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir)) return;
        string key;
        try { key = NormalizeDir(outputDir); }
        catch { return; }

        lock (_gate)
        {
            if (!_abandonedDirs.Add(key))
                return;

            foreach (var t in _byUuid.Values)
            {
                if (string.IsNullOrEmpty(t.OutputDir)) continue;
                try
                {
                    if (!string.Equals(NormalizeDir(t.OutputDir), key, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch { continue; }

                // 停止补拉落盘；进行中任务仍可更新进度，但不写结果文件
                t.LocalSyncVerified = true;
            }
        }

        _log?.Info($"TaskSync: 文献已删，停止结果落盘 dir={key}");
    }

    public async Task<string> SyncLocalResultsAsync()
    {
        await RefreshAsync();

        List<RecognitionTaskItem> toSync;
        lock (_gate)
        {
            toSync = _byUuid.Values
                .Where(t => !t.BatchUuid.StartsWith("local:", StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(t.OutputDir)
                            && !IsAbandonedUnlocked(t.OutputDir)
                            && (NeedsStatusPoll(t)
                                || t.Status is "completed" or "partial_failed" or "cancelled"
                                    or "queued" or "processing"))
                .ToList();
        }

        var syncedFiles = 0;
        foreach (var task in toSync)
        {
            try
            {
                if (await ShouldSkipSaveAsync(task.OutputDir))
                {
                    task.LocalSyncVerified = true;
                    continue;
                }

                // 分批拉取 result_json，直到本地齐或没有更多 omitted
                IReadOnlyList<int>? forceFetch = null;
                for (var round = 0; round < 40; round++)
                {
                    if (await ShouldSkipSaveAsync(task.OutputDir))
                    {
                        task.LocalSyncVerified = true;
                        break;
                    }

                    var status = await _batchOcr.GetStatusAsync(
                        task.BatchUuid, task.OutputDir, forceFetch);
                    forceFetch = null;
                    task.ApplyFromStatus(status);
                    await _cache.UpdateProgressAsync(
                        task.BatchUuid, status.Status, status.CompletedPages,
                        status.FailedPages, cancelledPages: status.CancelledPages);

                    var saved = 0;
                    if (!string.IsNullOrEmpty(task.OutputDir))
                        saved = await SavePagesAsync(task.OutputDir, status);
                    if (saved > 0)
                    {
                        syncedFiles += saved;
                        ResultsSynced?.Invoke(task.BatchUuid);
                    }

                    var fullySynced = !task.IsActive
                                      && OcrResultDisk.BatchCompletedPagesOnDisk(task.OutputDir, status);
                    task.LocalSyncVerified = fullySynced;
                    if (fullySynced)
                        break;

                    var needForce = OcrResultDisk.PagesNeedingForceFetch(task.OutputDir, status);
                    if (needForce.Count > 0)
                        forceFetch = needForce;
                    else if (!status.ResultsTruncated && saved == 0)
                        break; // 无可拉取内容且无法强制（异常态）
                }
            }
            catch (Exception ex)
            {
                var shortId = task.BatchUuid.Length > 8 ? task.BatchUuid[..8] : task.BatchUuid;
                _log?.Warn($"TaskSync SyncLocal {shortId}: {ex.Message}");
            }
        }

        RebuildRecent();
        TasksChanged?.Invoke();
        var msg = syncedFiles > 0
            ? $"同步完成：新写入 {syncedFiles} 页结果到本地"
            : $"同步完成：已检查 {toSync.Count} 个任务";
        StatusBarMessage?.Invoke(msg);
        return msg;
    }

    /// <summary>拉 status → 落盘 → 必要时强制补拉缺失页 JSON。</summary>
    private async Task PollAndSaveTaskAsync(RecognitionTaskItem task)
    {
        var skipSave = await ShouldSkipSaveAsync(task.OutputDir);
        if (skipSave && !task.IsActive)
        {
            // 空 OutputDir：不能假装已齐盘，否则永远不再拉 status
            if (!string.IsNullOrEmpty(task.OutputDir) && IsAbandoned(task.OutputDir))
                task.LocalSyncVerified = true;
            return;
        }

        IReadOnlyList<int>? forceFetch = null;
        for (var round = 0; round < 8; round++)
        {
            if (!skipSave && await ShouldSkipSaveAsync(task.OutputDir))
                skipSave = true;

            // 已删文献：只拉进度、不 force 补 JSON、不落盘
            var status = await _batchOcr.GetStatusAsync(
                task.BatchUuid,
                skipSave ? null : task.OutputDir,
                skipSave ? null : forceFetch);
            forceFetch = null;
            task.ApplyFromStatus(status);
            await _cache.UpdateProgressAsync(
                task.BatchUuid, status.Status, status.CompletedPages,
                status.FailedPages, cancelledPages: status.CancelledPages);

            if (status.FailedPages > 0 && round == 0)
            {
                _log?.Warn(
                    $"TaskSync 失败页数 batch={task.BatchUuid[..Math.Min(8, task.BatchUuid.Length)]} " +
                    $"failed={status.FailedPages}");
            }

            if (skipSave || string.IsNullOrEmpty(task.OutputDir))
            {
                // 仅显式 abandoned 的终态可停止轮询；空 OutputDir 保持未验证以便后续补目录后重试
                if (skipSave && !string.IsNullOrEmpty(task.OutputDir) && IsAbandoned(task.OutputDir))
                    task.LocalSyncVerified = !task.IsActive;
                return;
            }

            // 失败终态服务端会删临时图；清 sidecar，避免下次误用陈旧 key
            OcrResultDisk.InvalidateUploadCacheForFailed(task.OutputDir, status, _log);

            var saved = await SavePagesAsync(task.OutputDir, status);
            // 进行中永远不算 verified；只有服务端终态且本批完成页齐盘才算
            var fullySynced = !task.IsActive
                              && OcrResultDisk.BatchCompletedPagesOnDisk(task.OutputDir, status);
            task.LocalSyncVerified = fullySynced;

            if (saved > 0)
            {
                ResultsSynced?.Invoke(task.BatchUuid);
                // 进行中只报落盘进度，禁止出现「识别完成」
                if (task.IsActive)
                    StatusBarMessage?.Invoke(
                        $"{task.DisplayName}: 识别中 {status.CompletedPages}/{status.TotalPages}（已落盘 +{saved}）");
                _log?.Info(
                    $"TaskSync 落盘 batch={task.BatchUuid[..Math.Min(8, task.BatchUuid.Length)]} " +
                    $"saved={saved} synced={fullySynced} completed={status.CompletedPages} " +
                    $"omitted={status.ResultsOmitted}");
            }

            if (fullySynced || task.IsActive)
                return;

            var needForce = OcrResultDisk.PagesNeedingForceFetch(task.OutputDir, status);
            if (needForce.Count > 0)
            {
                forceFetch = needForce;
                continue;
            }
            if (status.ResultsTruncated)
                continue;
            return;
        }
    }

    /// <summary>
    /// 是否需要拉 status：进行中；或已结束但尚未确认本批页落盘。
    /// </summary>
    private bool NeedsStatusPoll(RecognitionTaskItem t)
    {
        // 文献已删：不再拉取 status / result_json
        if (!string.IsNullOrEmpty(t.OutputDir) && IsAbandoned(t.OutputDir))
            return false;

        if (t.IsActive)
            return true;

        if (t.Status is not ("completed" or "partial_failed" or "cancelled"))
            return false;

        if (string.IsNullOrEmpty(t.OutputDir))
            return false;

        // 终态：未验证落盘前持续拉取（禁止用全书本地文件数对比本批 CompletedPages）
        return !t.LocalSyncVerified;
    }

    private Task<bool> ShouldSkipSaveAsync(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
            return Task.FromResult(true);
        // 仅显式删除文献时 AbandonOutputDir。
        // 勿因 library_items.output_dir 尚未落库 / 路径暂不一致而永久停同步，
        // 否则终态批次会被标成 LocalSyncVerified 且不再请求 /status（表现为后台成功、客户端无结果）。
        return Task.FromResult(IsAbandoned(outputDir));
    }

    private bool IsAbandoned(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir)) return false;
        lock (_gate)
            return IsAbandonedUnlocked(outputDir);
    }

    /// <summary>调用方须已持有 <see cref="_gate"/>。</summary>
    private bool IsAbandonedUnlocked(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir)) return false;
        try { return _abandonedDirs.Contains(NormalizeDir(outputDir)); }
        catch { return false; }
    }

    private static string NormalizeDir(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public async Task CancelAsync(string batchUuid)
    {
        if (string.IsNullOrEmpty(batchUuid) || batchUuid.StartsWith("local:", StringComparison.Ordinal))
            return;

        var resp = await _batchOcr.CancelBatchAsync(batchUuid);
        RecognitionTaskItem? task;
        lock (_gate) { _byUuid.TryGetValue(batchUuid, out task); }
        if (task != null)
        {
            task.Status = resp.Status;
            task.CancelledPages = resp.CancelledPages;
            task.CompletedPages = resp.CompletedPages;
            task.FailedPages = resp.FailedPages;
            await _cache.UpdateProgressAsync(
                batchUuid, resp.Status, resp.CompletedPages, resp.FailedPages,
                cancelledPages: resp.CancelledPages);
        }
        StatusBarMessage?.Invoke(resp.Message);
        RebuildRecent();
        TasksChanged?.Invoke();
        await RefreshAsync();
    }

    public string BeginLocalUpload(string documentName, int totalPages, string outputDir)
    {
        var id = $"local:{Interlocked.Increment(ref _localSeq)}";
        var item = new RecognitionTaskItem
        {
            BatchUuid = id,
            DocumentName = documentName,
            TotalPages = totalPages,
            UploadedPages = 0,
            Status = "queued_upload",
            OutputDir = outputDir,
        };
        InsertOrUpdate(item);
        RebuildRecent();
        TasksChanged?.Invoke();
        StatusBarMessage?.Invoke($"{item.DisplayName}: 排队上传");
        return id;
    }

    public void MarkLocalQueued(string localId)
    {
        RecognitionTaskItem? task;
        lock (_gate) { _byUuid.TryGetValue(localId, out task); }
        if (task == null) return;
        if (task.Status is "uploading" or "failed") return;
        task.Status = "queued_upload";
        StatusBarMessage?.Invoke($"{task.DisplayName}: 排队上传");
        RebuildRecent();
        TasksChanged?.Invoke();
    }

    public void MarkLocalUploading(string localId)
    {
        RecognitionTaskItem? task;
        lock (_gate) { _byUuid.TryGetValue(localId, out task); }
        if (task == null) return;
        task.Status = "uploading";
        StatusBarMessage?.Invoke($"{task.DisplayName}: 上传 {task.UploadedPages}/{task.TotalPages}");
        RebuildRecent();
        TasksChanged?.Invoke();
    }

    public void UpdateLocalUpload(string localId, int uploadedPages)
    {
        RecognitionTaskItem? task;
        lock (_gate) { _byUuid.TryGetValue(localId, out task); }
        if (task == null) return;
        task.UploadedPages = uploadedPages;
        if (task.Status == "queued_upload")
            task.Status = "uploading";
        StatusBarMessage?.Invoke($"{task.DisplayName}: {task.ProgressText}");
        RebuildRecent();
        TasksChanged?.Invoke();
    }

    public void FailLocalUpload(string localId, string reason)
    {
        if (string.IsNullOrEmpty(localId)) return;
        RecognitionTaskItem? task;
        lock (_gate) { _byUuid.TryGetValue(localId, out task); }
        if (task == null) return;
        task.Status = "failed";
        task.FailReason = string.IsNullOrWhiteSpace(reason) ? "上传失败" : reason;
        if (task.FailReason.Length > 80)
            task.FailReason = task.FailReason[..77] + "...";
        StatusBarMessage?.Invoke($"{task.DisplayName}: {task.ProgressText}");
        RebuildRecent();
        TasksChanged?.Invoke();
        _log?.Error($"TaskSync: 本地上传失败 id={localId} reason={task.FailReason}");
    }

    public async Task TrackSubmittedAsync(string? localId, BatchSubmitResponse response,
        string documentName, string outputDir, int uploadedPages)
    {
        if (!string.IsNullOrEmpty(localId))
        {
            RecognitionTaskItem? old = null;
            lock (_gate)
            {
                _byUuid.Remove(localId, out old);
            }
            if (old != null)
                UiPost(() => Tasks.Remove(old));
        }

        // 重新提交同一目录：清除误标的 abandoned，恢复落盘
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            try
            {
                var key = NormalizeDir(outputDir);
                lock (_gate) { _abandonedDirs.Remove(key); }
            }
            catch { /* ignore */ }
        }

        var now = DateTime.UtcNow.ToString("o");
        var item = new RecognitionTaskItem
        {
            BatchUuid = response.BatchUuid,
            DocumentName = documentName,
            TotalPages = response.TotalPages,
            UploadedPages = uploadedPages,
            Status = string.IsNullOrEmpty(response.Status) ? "queued" : response.Status,
            CostTotal = response.CostTotal,
            OutputDir = outputDir,
            LocalSyncVerified = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        InsertOrUpdate(item);

        await _cache.SaveBatchAsync(
            response.BatchUuid, documentName, response.TotalPages, outputDir,
            item.Status, 0, 0, uploadedPages);

        RebuildRecent();
        TasksChanged?.Invoke();
        StatusBarMessage?.Invoke($"{item.DisplayName}: {item.ProgressText}");

        // 勿等 Loop 最长 60s 空闲周期才拉 status
        _ = RefreshAsync();
    }

    public async Task SyncResultsToDiskAsync(string batchUuid, string outputDir, BatchStatusResponse status)
    {
        if (await ShouldSkipSaveAsync(outputDir))
            return;
        await SavePagesAsync(outputDir, status);
    }

    public void NotifyBatchSettled(
        string batchUuid, string status, int completedPages, int failedPages, int cancelledPages,
        bool localSyncVerified)
    {
        if (string.IsNullOrEmpty(batchUuid)) return;
        RecognitionTaskItem? task;
        lock (_gate) { _byUuid.TryGetValue(batchUuid, out task); }
        if (task == null) return;

        task.Status = string.IsNullOrEmpty(status) ? task.Status : status;
        task.CompletedPages = completedPages;
        task.FailedPages = failedPages;
        task.CancelledPages = cancelledPages;
        // 仅在调用方确认已落盘时标记；进行中传入 true 也忽略
        var terminal = OcrResultDisk.IsTerminalBatchStatus(task.Status);
        task.LocalSyncVerified = terminal && localSyncVerified;
        RebuildRecent();
        TasksChanged?.Invoke();
        StatusBarMessage?.Invoke(FormatStatusBar(task));
        _ = _cache.UpdateProgressAsync(
            batchUuid, task.Status, completedPages, failedPages, cancelledPages: cancelledPages);
    }

    private static string FormatStatusBar(RecognitionTaskItem task)
        => $"{task.DisplayName}: {task.ProgressText}";

    /// <summary>底栏是否应刷新：进行中跟进度；或刚从「未齐盘」变为「已齐盘完成」。</summary>
    private static bool ShouldPublishStatusBar(
        RecognitionTaskItem task, bool wasActive, bool wasVerified)
    {
        if (task.IsActive)
            return true;
        // 终态：只有确认落盘后才发「识别完成」，避免抢跑
        if (task.LocalSyncVerified && !wasVerified)
            return true;
        if (wasActive != task.IsActive && !task.LocalSyncVerified)
            return true; // 刚进终态但仍在同步 → 显示「结果同步中」
        return false;
    }

    private async Task<int> SavePagesAsync(string outputDir, BatchStatusResponse status)
        => await OcrResultDisk.SaveCompletedPagesAsync(outputDir, status, default, _log);

    private void UpsertFromServer(BatchListItem item, string? outputDir)
    {
        RecognitionTaskItem? existing;
        lock (_gate) { _byUuid.TryGetValue(item.BatchUuid, out existing); }
        if (existing != null)
        {
            if (existing.Status is "uploading" or "queued_upload") return;
            var wasActive = existing.IsActive;
            existing.ApplyFromListItem(item, outputDir);
            // 刚进入终态或尚无输出目录对齐 → 需要再验证落盘
            if (wasActive && !existing.IsActive)
                existing.LocalSyncVerified = false;
            if (!string.IsNullOrEmpty(outputDir) && string.IsNullOrEmpty(existing.OutputDir))
                existing.LocalSyncVerified = false;
        }
        else
        {
            var task = new RecognitionTaskItem();
            task.ApplyFromListItem(item, outputDir);
            task.LocalSyncVerified = false;
            InsertOrUpdate(task);
        }
    }

    private void InsertOrUpdate(RecognitionTaskItem item)
    {
        lock (_gate)
        {
            if (_byUuid.TryGetValue(item.BatchUuid, out var existing))
            {
                existing.DocumentName = item.DocumentName;
                existing.TotalPages = item.TotalPages;
                existing.CompletedPages = item.CompletedPages;
                existing.FailedPages = item.FailedPages;
                existing.CancelledPages = item.CancelledPages;
                existing.UploadedPages = item.UploadedPages;
                existing.Status = item.Status;
                existing.CostTotal = item.CostTotal;
                if (!string.IsNullOrEmpty(item.OutputDir))
                    existing.OutputDir = item.OutputDir;
                existing.UpdatedAt = item.UpdatedAt;
                if (!string.IsNullOrEmpty(item.CreatedAt))
                    existing.CreatedAt = item.CreatedAt;
                if (!string.IsNullOrEmpty(item.CompletedAt))
                    existing.CompletedAt = item.CompletedAt;
            }
            else
            {
                if (string.IsNullOrEmpty(item.CreatedAt))
                    item.CreatedAt = DateTime.UtcNow.ToString("o");
                if (string.IsNullOrEmpty(item.UpdatedAt))
                    item.UpdatedAt = item.CreatedAt;
                _byUuid[item.BatchUuid] = item;
                UiPost(() =>
                {
                    if (!Tasks.Contains(item))
                        Tasks.Insert(0, item);
                });
            }
        }
    }

    /// <summary>最近优先：进行中靠前，其余按更新/完成/提交时间倒序。</summary>
    internal static IEnumerable<RecognitionTaskItem> OrderByRecent(
        IEnumerable<RecognitionTaskItem> tasks)
        => tasks
            .OrderByDescending(t => t.IsActive || t.Status is "queued_upload" or "uploading")
            .ThenByDescending(t => t.RecencySortKey);

    private void RebuildRecent()
    {
        UiPost(() =>
        {
            var ordered = OrderByRecent(Tasks).ToList();
            // 同步主列表顺序，任务中心与左下角最近三条一致
            for (var i = 0; i < ordered.Count; i++)
            {
                var idx = Tasks.IndexOf(ordered[i]);
                if (idx >= 0 && idx != i)
                    Tasks.Move(idx, i);
            }

            RecentTasks.Clear();
            foreach (var t in ordered.Take(3))
                RecentTasks.Add(t);
        });
    }
}
