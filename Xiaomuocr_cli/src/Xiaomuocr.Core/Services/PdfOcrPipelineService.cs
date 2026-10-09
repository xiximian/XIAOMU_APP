using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// PDF OCR 管道：统一走排队池批次（单页 = 1 页批次）。
/// 渲染 → OSS 上传 → batch/submit → 轮询落盘。
/// </summary>
public interface IPdfOcrPipelineService
{
    Task RunOcrAsync(
        LibraryItem item,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Dictionary<string, object>? options = null,
        bool waitForCompletion = true);

    Task<bool> RunSinglePageOcrAsync(
        LibraryItem item,
        int pageIndex,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default,
        Dictionary<string, object>? options = null);

    Task RunPageRangeOcrAsync(
        LibraryItem item, int startPageIndex, int endPageIndex,
        bool skipExisting = true,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Dictionary<string, object>? options = null,
        bool waitForCompletion = true);
}

public class OcrProgress
{
    public int CurrentPage { get; init; }
    public int TotalPages { get; init; }
    public string Status { get; init; } = "";
    public bool IsError { get; init; }
    public bool IsFinished { get; init; }
    public bool IsCancelled { get; init; }
    /// <summary>提交成功后的批次 ID（供跳转任务中心）。</summary>
    public string? BatchUuid { get; init; }
}

public class PdfOcrPipelineService : IPdfOcrPipelineService
{
    private readonly IPdfRenderService _pdfRender;
    private readonly IBatchUploadService _upload;
    private readonly IBatchOcrService _batchOcr;
    private readonly IBatchCacheService _cache;
    private readonly ITaskSyncService? _taskSync;
    private readonly IUploadJobQueue _uploadQueue;
    private readonly ILibraryService _library;
    private readonly IAppLogService? _log;

    public PdfOcrPipelineService(
        IPdfRenderService pdfRender,
        IBatchUploadService upload,
        IBatchOcrService batchOcr,
        IBatchCacheService cache,
        ILibraryService library,
        IUploadJobQueue uploadQueue,
        ITaskSyncService? taskSync = null,
        IAppLogService? log = null)
    {
        _pdfRender = pdfRender;
        _upload = upload;
        _batchOcr = batchOcr;
        _cache = cache;
        _library = library;
        _uploadQueue = uploadQueue;
        _taskSync = taskSync;
        _log = log;
    }

    public async Task<bool> RunSinglePageOcrAsync(
        LibraryItem item, int pageIndex, IProgress<string>? log = null,
        CancellationToken cancellationToken = default,
        Dictionary<string, object>? options = null)
    {
        try
        {
            await RunPageRangeOcrAsync(
                item, pageIndex, pageIndex, skipExisting: false,
                progress: new Progress<OcrProgress>(p => log?.Report(p.Status)),
                cancellationToken, options, waitForCompletion: true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Report($"第 {pageIndex + 1} 页: ✗ 失败: {ex.Message}");
            _log?.Error($"OCR管道: 单页失败 page={pageIndex + 1} err={ex.Message}");
            return false;
        }
    }

    public async Task RunOcrAsync(
        LibraryItem item,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Dictionary<string, object>? options = null,
        bool waitForCompletion = true)
    {
        if (item.TotalPages <= 0)
        {
            var info = await _pdfRender.ProbePdfAsync(item.PdfPath);
            item.TotalPages = info.TotalPages;
        }
        await RunPageRangeOcrAsync(
            item, 0, item.TotalPages - 1, skipExisting: true, progress, cancellationToken, options,
            waitForCompletion);
    }

    public async Task RunPageRangeOcrAsync(
        LibraryItem item, int startPageIndex, int endPageIndex,
        bool skipExisting = true, IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Dictionary<string, object>? options = null,
        bool waitForCompletion = true)
    {
        var outputDir = item.OutputDir ?? Path.Combine(
            Path.GetDirectoryName(item.PdfPath) ?? "",
            Path.GetFileNameWithoutExtension(item.PdfPath));
        Directory.CreateDirectory(outputDir);

        if (item.TotalPages <= 0)
        {
            var info = await _pdfRender.ProbePdfAsync(item.PdfPath);
            item.TotalPages = info.TotalPages;
        }

        int start = Math.Max(0, startPageIndex);
        int end = Math.Min(item.TotalPages - 1, endPageIndex);
        if (end < start)
            throw new ArgumentException($"无效页码范围: {startPageIndex + 1}-{endPageIndex + 1}");

        // 收集待识别页
        var pageIndices = new List<int>();
        for (int p = start; p <= end; p++)
        {
            var jsonPath = Path.Combine(outputDir, $"page_{p + 1}_ocr_result.json");
            if (skipExisting && File.Exists(jsonPath))
            {
                progress?.Report(new OcrProgress
                {
                    CurrentPage = p + 1, TotalPages = item.TotalPages,
                    Status = $"第 {p + 1} 页: 已完成，跳过"
                });
                continue;
            }
            pageIndices.Add(p);
        }

        if (pageIndices.Count == 0)
        {
            item.OcrDonePages = CountOcrResults(outputDir);
            progress?.Report(new OcrProgress
            {
                CurrentPage = item.TotalPages, TotalPages = item.TotalPages,
                Status = $"无需识别（已完成 {item.OcrDonePages}/{item.TotalPages} 页）",
                IsFinished = true,
            });
            return;
        }

        int total = pageIndices.Count;
        string? localId = null;
        try
        {
            localId = _taskSync?.BeginLocalUpload(item.Name, total, outputDir);

            progress?.Report(new OcrProgress
            {
                CurrentPage = 0, TotalPages = total,
                Status = "排队上传…",
            });

            var submitResult = await _uploadQueue.EnqueueAsync(localId, async ct =>
            {
                List<string> keys;
                List<int> nums;

                // 上传前清陈旧 file_key：上次 OCR 终态后服务端已删图，续传会 oss_missing
                OcrResultDisk.InvalidateUploadCache(
                    outputDir, pageIndices.Select(i => i + 1), _log);

                bool contiguous = pageIndices[^1] - pageIndices[0] + 1 == pageIndices.Count;
                if (contiguous)
                {
                    var uploadProgress = new Progress<BatchUploadProgress>(p =>
                    {
                        if (localId != null)
                            _taskSync?.UpdateLocalUpload(localId, p.UploadedPages);
                        progress?.Report(new OcrProgress
                        {
                            CurrentPage = p.UploadedPages > 0 ? p.UploadedPages : p.PreparedPages,
                            TotalPages = total,
                            Status = p.Status,
                        });
                    });
                    var uploaded = await _upload.UploadPagesAsync(
                        item, pageIndices, uploadProgress, ct);
                    keys = uploaded.Select(x => x.FileKey).ToList();
                    nums = uploaded.Select(x => x.PageNumber).ToList();
                }
                else
                {
                    keys = new List<string>();
                    nums = new List<int>();
                    int done = 0;
                    foreach (var pi in pageIndices)
                    {
                        ct.ThrowIfCancellationRequested();
                        var part = await _upload.UploadAllPagesAsync(
                            item, pi, pi,
                            new Progress<BatchUploadProgress>(_ => { }),
                            ct);
                        if (part.Count > 0)
                        {
                            keys.Add(part[0]);
                            nums.Add(pi + 1);
                        }
                        done++;
                        if (localId != null)
                            _taskSync?.UpdateLocalUpload(localId, done);
                        progress?.Report(new OcrProgress
                        {
                            CurrentPage = pi + 1, TotalPages = item.TotalPages,
                            Status = $"上传 {done}/{total}",
                        });
                    }
                }

                if (keys.Count == 0)
                    throw new Exception("上传失败：无有效文件");

                progress?.Report(new OcrProgress
                {
                    CurrentPage = nums[0], TotalPages = item.TotalPages,
                    Status = $"提交批次 ({keys.Count} 页)...",
                });

                var response = await _batchOcr.SubmitBatchAsync(
                    keys, item.Name, options, nums);

                // 必须在 Track/轮询之前清旧 JSON，否则 have_pages 会跳过下发
                OcrResultDisk.ClearPages(outputDir, nums, _log);

                if (_taskSync != null)
                {
                    await _taskSync.TrackSubmittedAsync(
                        localId, response, item.Name, outputDir, keys.Count);
                    localId = null; // 已替换为服务端任务
                }
                else
                {
                    await _cache.SaveBatchAsync(
                        response.BatchUuid, item.Name, response.TotalPages, outputDir);
                }

                return (Keys: keys, PageNumbers: nums, Response: response);
            }, cancellationToken);

            var fileKeys = submitResult.Keys;
            var pageNumbers = submitResult.PageNumbers;
            var batchResponse = submitResult.Response;

            progress?.Report(new OcrProgress
            {
                CurrentPage = pageNumbers[0], TotalPages = item.TotalPages,
                Status = waitForCompletion
                    ? $"已上传·识别中 0/{batchResponse.TotalPages}（可关闭客户端）"
                    : $"已提交 {fileKeys.Count} 页，可在任务中心查看（关闭客户端不影响识别）",
                BatchUuid = batchResponse.BatchUuid,
                IsFinished = !waitForCompletion,
            });

            if (!waitForCompletion)
                return;

            BatchStatusResponse? lastStatus = null;
            await _batchOcr.StartPollingAsync(
                batchResponse.BatchUuid, batchResponse.TotalPages, outputDir,
                async status =>
                {
                    lastStatus = status;
                    var synced = OcrResultDisk.BatchCompletedPagesOnDisk(outputDir, status);
                    string line;
                    if (status.Status is "completed" or "partial_failed" or "cancelled")
                    {
                        // 与任务中心一致：未齐盘绝不说「识别完成」
                        line = synced
                            ? $"识别完成（本批 {status.CompletedPages} 页）"
                            : $"结果同步中 {status.CompletedPages}/{status.TotalPages}…";
                    }
                    else
                    {
                        line = $"已上传·识别中 {status.CompletedPages}/{status.TotalPages}";
                    }

                    progress?.Report(new OcrProgress
                    {
                        CurrentPage = Math.Max(1, status.CompletedPages),
                        TotalPages = item.TotalPages,
                        Status = line,
                        BatchUuid = batchResponse.BatchUuid,
                    });
                    await Task.CompletedTask;
                },
                cancellationToken);

            item.OcrDonePages = CountOcrResults(outputDir);
            await _library.UpdateItemAsync(item);

            var settledStatus = lastStatus?.Status ?? "completed";
            var settledCompleted = lastStatus?.CompletedPages ?? batchResponse.TotalPages;
            var settledFailed = lastStatus?.FailedPages ?? 0;
            var settledCancelled = lastStatus?.CancelledPages ?? 0;
            _taskSync?.NotifyBatchSettled(
                batchResponse.BatchUuid, settledStatus, settledCompleted,
                settledFailed, settledCancelled, localSyncVerified: true);

            progress?.Report(new OcrProgress
            {
                CurrentPage = item.TotalPages,
                TotalPages = item.TotalPages,
                Status = $"识别完成（本批 {settledCompleted} 页已落盘）",
                IsFinished = true,
                BatchUuid = batchResponse.BatchUuid,
            });
        }
        catch (OperationCanceledException)
        {
            if (localId != null)
                _taskSync?.FailLocalUpload(localId, "已取消");
            item.OcrDonePages = CountOcrResults(outputDir);
            progress?.Report(new OcrProgress
            {
                CurrentPage = Math.Min(end + 1, item.TotalPages),
                TotalPages = item.TotalPages,
                Status = $"已取消识别（已完成 {item.OcrDonePages}/{item.TotalPages} 页）",
                IsFinished = true,
                IsCancelled = true,
            });
            throw;
        }
        catch (Exception ex)
        {
            if (localId != null)
                _taskSync?.FailLocalUpload(localId, ex.Message);
            progress?.Report(new OcrProgress
            {
                CurrentPage = 0, TotalPages = item.TotalPages,
                Status = $"上传失败: {ex.Message}",
                IsError = true,
                IsFinished = true,
            });
            throw;
        }
    }

    private static int CountOcrResults(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return 0;
        return Directory.GetFiles(outputDir, "page_*_ocr_result.json").Length;
    }
}
