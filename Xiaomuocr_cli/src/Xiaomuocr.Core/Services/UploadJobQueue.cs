namespace Xiaomuocr.Core.Services;

/// <summary>
/// 本机文献级上传队列：同时只跑 1 个文档的上传/提交，避免多篇并发打满本机与中继。
/// 页级并发由 <see cref="GlobalUploadGate"/> 另行限制。
/// </summary>
public interface IUploadJobQueue
{
    /// <summary>排队执行文档上传任务；进入执行前将 localId 标为 uploading。</summary>
    Task<T> EnqueueAsync<T>(
        string? localId,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct = default);
}

public static class GlobalUploadGate
{
    /// <summary>全进程页上传并发上限（所有文献共享）。</summary>
    public const int MaxPageUploads = 4;

    public static readonly SemaphoreSlim Pages = new(MaxPageUploads, MaxPageUploads);
}

public class UploadJobQueue : IUploadJobQueue
{
    private readonly SemaphoreSlim _docGate = new(1, 1);
    private readonly ITaskSyncService? _taskSync;
    private readonly IAppLogService? _log;

    public UploadJobQueue(ITaskSyncService? taskSync = null, IAppLogService? log = null)
    {
        _taskSync = taskSync;
        _log = log;
    }

    public async Task<T> EnqueueAsync<T>(
        string? localId,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(localId))
            _taskSync?.MarkLocalQueued(localId);

        _log?.Info($"UploadQueue: 等待文献槽位 localId={localId}");
        await _docGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(localId))
                _taskSync?.MarkLocalUploading(localId);

            _log?.Info($"UploadQueue: 开始文献上传 localId={localId}");
            return await work(ct);
        }
        finally
        {
            _docGate.Release();
            _log?.Info($"UploadQueue: 释放文献槽位 localId={localId}");
        }
    }
}
