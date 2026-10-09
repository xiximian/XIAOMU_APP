using System.Text.Json;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>批量上传服务：边渲染压缩边分片取凭证并上传。</summary>
public interface IBatchUploadService
{
    Task<List<string>> UploadAllPagesAsync(
        LibraryItem item, int startPage, int endPage,
        IProgress<BatchUploadProgress> progress, CancellationToken ct);

    /// <summary>
    /// 仅上传指定页（0-based）。返回与输入页序对齐的 (1-based 页码, file_key)；失败页不会出现在结果中。
    /// </summary>
    Task<List<(int PageNumber, string FileKey)>> UploadPagesAsync(
        LibraryItem item, IReadOnlyList<int> pageIndices0Based,
        IProgress<BatchUploadProgress> progress, CancellationToken ct);
}

public class BatchUploadService : IBatchUploadService
{
    private readonly IPdfRenderService _pdfRender;
    private readonly IOssService _oss;
    private readonly IAppLogService? _log;

    public BatchUploadService(IPdfRenderService pdfRender, IOssService oss, IAppLogService? log = null)
    {
        _pdfRender = pdfRender;
        _oss = oss;
        _log = log;
    }

    /// <summary>判断页上传异常是否可重试（超时 / 网络 / 5xx / 429）。</summary>
    private static bool IsRetryableUploadError(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException or HttpRequestException)
            return true;
        var msg = ex.Message ?? "";
        if (msg.Contains("429", StringComparison.Ordinal) ||
            msg.Contains("请求过于频繁", StringComparison.Ordinal) ||
            msg.Contains("服务繁忙", StringComparison.Ordinal) ||
            msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("HTTP 5", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>上传单个已渲染页面；超时/网络/5xx/429 指数退避；用全局页并发闸门。</summary>
    private async Task<(int pageIndex, string? fileKey, string? error)> UploadOnePageAsync(
        int pageIdx, string outputDir, Models.BatchTokenItem tokenItem,
        CancellationToken ct)
    {
        await GlobalUploadGate.Pages.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            var cached = UploadUrlStore.TryLoadKey(outputDir, pageIdx);
            if (!string.IsNullOrEmpty(cached))
            {
                _log?.Info($"BatchUpload: 第{pageIdx + 1}页 续传跳过（已有 key）");
                return (pageIdx, cached, null);
            }

            var imgPath = System.IO.Path.Combine(outputDir, $"page_{pageIdx + 1}.jpg");
            if (!System.IO.File.Exists(imgPath))
            {
                imgPath = System.IO.Path.Combine(outputDir, $"page_{pageIdx + 1}.png");
                if (!System.IO.File.Exists(imgPath))
                    return (pageIdx, null, $"渲染文件不存在: page_{pageIdx + 1}.jpg");
            }

            const int maxRetries = 4;
            Exception? lastEx = null;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var uploadResult = await _oss.UploadFileAsync(
                        imgPath, tokenItem.Token, tokenItem.UploadUrl, tokenItem.Key);

                    var fileKey = !string.IsNullOrEmpty(uploadResult.Key) ? uploadResult.Key : tokenItem.Key;
                    if (string.IsNullOrEmpty(fileKey))
                        return (pageIdx, null, "上传成功但未得到 file_key");

                    if (attempt > 0)
                        _log?.Info($"BatchUpload: 第{pageIdx + 1}页 重试{attempt}次后上传成功");
                    else
                        _log?.Info($"BatchUpload: 第{pageIdx + 1}页 上传完成 key={fileKey}");

                    await UploadUrlStore.SaveAsync(outputDir, pageIdx, fileKey, ct);
                    return (pageIdx, fileKey, null);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastEx = new TimeoutException("上传超时");
                    if (attempt >= maxRetries) break;
                    var delay = (int)Math.Pow(2, attempt);
                    _log?.Warn($"BatchUpload: 第{pageIdx + 1}页 超时，{delay}s 后重试 (第{attempt + 1}次)");
                    await Task.Delay(delay * 1000, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < maxRetries && IsRetryableUploadError(ex))
                {
                    lastEx = ex;
                    var delay = (int)Math.Pow(2, attempt);
                    _log?.Warn($"BatchUpload: 第{pageIdx + 1}页 可重试失败，{delay}s 后重试 (第{attempt + 1}次): {ex.Message}");
                    await Task.Delay(delay * 1000, ct);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    break;
                }
            }

            var err = lastEx?.Message ?? "上传失败：已达最大重试次数";
            _log?.Error($"BatchUpload: 第{pageIdx + 1}页 上传失败: {err}");
            return (pageIdx, null, err);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Error($"BatchUpload: 第{pageIdx + 1}页 上传失败: {ex.Message}");
            return (pageIdx, null, ex.Message);
        }
        finally
        {
            GlobalUploadGate.Pages.Release();
        }
    }

    public async Task<List<string>> UploadAllPagesAsync(
        LibraryItem item, int startPage, int endPage,
        IProgress<BatchUploadProgress> progress, CancellationToken ct)
    {
        var pages = Enumerable.Range(startPage, endPage - startPage + 1).ToList();
        var uploaded = await UploadPagesAsync(item, pages, progress, ct);
        return uploaded.Select(x => x.FileKey).ToList();
    }

    public async Task<List<(int PageNumber, string FileKey)>> UploadPagesAsync(
        LibraryItem item, IReadOnlyList<int> pageIndices0Based,
        IProgress<BatchUploadProgress> progress, CancellationToken ct)
    {
        if (pageIndices0Based == null || pageIndices0Based.Count == 0)
            return new List<(int, string)>();

        var pages = pageIndices0Based.Distinct().OrderBy(i => i).ToList();
        int total = pages.Count;
        var outputDir = item.OutputDir ?? System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(item.PdfPath) ?? "",
            System.IO.Path.GetFileNameWithoutExtension(item.PdfPath));
        System.IO.Directory.CreateDirectory(outputDir);

        var pdfInfo = await _pdfRender.ProbePdfAsync(item.PdfPath);
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var pdfPath = item.PdfPath;

        // 边准备边上传：准备队列 → 每 50 页取凭证 → 上传（全局并发 GlobalUploadGate）
        const int tokenChunkSize = 50;
        const int renderConcurrency = 2;
        int workTotal = total * 2; // 准备 + 上传，进度条不回退

        int prepared = 0;
        int uploadedOk = 0;
        var results = new Dictionary<int, string?>();
        var resultsLock = new object();

        void ReportProgress(string status, bool isError = false, bool isFinished = false)
        {
            var prep = Volatile.Read(ref prepared);
            var up = Volatile.Read(ref uploadedOk);
            progress.Report(new BatchUploadProgress
            {
                PreparedPages = prep,
                UploadedPages = up,
                CurrentPage = prep + up,
                TotalPages = workTotal,
                Status = status,
                IsError = isError,
                IsFinished = isFinished,
            });
        }

        ReportProgress($"本地准备 0/{total}…");

        var preparedChannel = System.Threading.Channels.Channel.CreateUnbounded<(int pageIdx, long fileSize)>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var uploadTasks = new List<Task>();
        var uploadTasksLock = new object();

        // ---- 上传：拿到凭证后立即开传 ----
        async Task EnqueueUploadsAsync(List<(int pageIdx, long fileSize)> chunk)
        {
            if (chunk.Count == 0) return;

            // 已有 URL 的页直接计入结果，不再取凭证
            var needToken = new List<(int pageIdx, long fileSize)>();
            foreach (var (pageIdx, fileSize) in chunk)
            {
                var cached = UploadUrlStore.TryLoadUrl(outputDir, pageIdx);
                if (!string.IsNullOrEmpty(cached))
                {
                    lock (resultsLock) { results[pageIdx] = cached; }
                    var n = Interlocked.Increment(ref uploadedOk);
                    ReportProgress($"上传中 {n}/{total}（已准备 {Volatile.Read(ref prepared)}/{total}）· 续传");
                    _log?.Info($"BatchUpload: 第{pageIdx + 1}页 使用已缓存 URL");
                    _log?.Info($"OCRTRACE|page={pageIdx + 1}|event=cache_hit|key={cached}");
                    continue;
                }
                needToken.Add((pageIdx, fileSize));
            }

            if (needToken.Count == 0) return;

            var tokenFiles = needToken.Select(p => new Models.BatchTokenFileItem
            {
                FileName = $"page_{p.pageIdx + 1}.jpg",
                FileSize = p.fileSize,
                ContentType = "image/jpeg",
            }).ToList();

            Models.BatchTokenResponse tokenBatch;
            try
            {
                var tokenSw = System.Diagnostics.Stopwatch.StartNew();
                tokenBatch = await _oss.GetUploadTokensBatchAsync(tokenFiles);
                tokenSw.Stop();
                _log?.Info($"BatchUpload: 分片凭证 {needToken.Count} 个 elapsed={tokenSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _log?.Error($"BatchUpload: 获取凭证失败: {ex.Message}");
                ReportProgress($"获取上传凭证失败: {ex.Message}", isError: true, isFinished: true);
                throw;
            }

            var tokenMap = new Dictionary<int, Models.BatchTokenItem>();
            foreach (var tok in tokenBatch.Tokens)
            {
                var match = System.Text.RegularExpressions.Regex.Match(tok.FileName, @"page_(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var pn))
                    tokenMap[pn - 1] = tok;
            }

            foreach (var (pageIdx, _) in needToken)
            {
                if (!tokenMap.TryGetValue(pageIdx, out var tokenItem))
                {
                    _log?.Warn($"BatchUpload: 第{pageIdx + 1}页缺少上传凭证");
                    lock (resultsLock) { results[pageIdx] = null; }
                    continue;
                }

                var capturedIdx = pageIdx;
                var capturedToken = tokenItem;
                Task uploadTask = Task.Run(async () =>
                {
                    var (pi, fileKey, error) = await UploadOnePageAsync(
                        capturedIdx, outputDir, capturedToken, ct);
                    lock (resultsLock) { results[pi] = fileKey; }
                    if (error != null)
                    {
                        _log?.Warn($"BatchUpload: 第{pi + 1}页上传失败: {error}");
                        ReportProgress(
                            $"上传中 {Volatile.Read(ref uploadedOk)}/{total}（已准备 {Volatile.Read(ref prepared)}/{total}）— 第{pi + 1}页失败",
                            isError: true);
                    }
                    else
                    {
                        var n = Interlocked.Increment(ref uploadedOk);
                        ReportProgress(
                            $"上传中 {n}/{total}（已准备 {Volatile.Read(ref prepared)}/{total}）");
                    }
                }, ct);

                lock (uploadTasksLock) { uploadTasks.Add(uploadTask); }
            }
        }

        // ---- Token 协调：积满 50 或生产者结束 ----
        var tokenCoordinator = Task.Run(async () =>
        {
            var buffer = new List<(int pageIdx, long fileSize)>(tokenChunkSize);
            await foreach (var itemPrep in preparedChannel.Reader.ReadAllAsync(ct))
            {
                buffer.Add(itemPrep);
                if (buffer.Count >= tokenChunkSize)
                {
                    var chunk = buffer.ToList();
                    buffer.Clear();
                    await EnqueueUploadsAsync(chunk);
                }
            }
            if (buffer.Count > 0)
                await EnqueueUploadsAsync(buffer);
        }, ct);

        // ---- 生产者：有限并行渲染/压缩（已有 jpg 则跳过渲染）----
        var renderSemaphore = new SemaphoreSlim(renderConcurrency);
        var renderTasks = pages.Select(async pi =>
        {
            await renderSemaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var imgPath = System.IO.Path.Combine(outputDir, $"page_{pi + 1}.jpg");
                long fileSize;

                if (System.IO.File.Exists(imgPath))
                {
                    fileSize = new System.IO.FileInfo(imgPath).Length;
                    _log?.Info($"BatchUpload: 第{pi + 1}页 跳过渲染（已有 jpg {fileSize / 1024}KB）");
                }
                else
                {
                    var (pw, ph) = pdfInfo.GetPageSize(pi);
                    var pageImage = await _pdfRender.RenderPageForOcrAsync(
                        pdfPath, pi, pw, ph);
                    await System.IO.File.WriteAllBytesAsync(imgPath, pageImage.ImageBytes, ct);
                    var legacyPng = System.IO.Path.Combine(outputDir, $"page_{pi + 1}.png");
                    try { if (System.IO.File.Exists(legacyPng)) System.IO.File.Delete(legacyPng); } catch { /* ignore */ }
                    await OcrPreprocessStore.SaveAsync(outputDir, pi, pageImage.PreprocessInfo, ct);
                    fileSize = pageImage.ImageBytes.Length;
                }

                var n = Interlocked.Increment(ref prepared);
                var up = Volatile.Read(ref uploadedOk);
                ReportProgress(
                    up > 0
                        ? $"上传中 {up}/{total}（已准备 {n}/{total}）"
                        : $"本地准备 {n}/{total}（{fileSize / 1024}KB）");

                await preparedChannel.Writer.WriteAsync((pi, fileSize), ct);
            }
            finally
            {
                renderSemaphore.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(renderTasks);
            preparedChannel.Writer.Complete();
            await tokenCoordinator;
            Task[] pendingUploads;
            lock (uploadTasksLock) { pendingUploads = uploadTasks.ToArray(); }
            await Task.WhenAll(pendingUploads);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            preparedChannel.Writer.TryComplete(ex);
            throw;
        }
        finally
        {
            renderSemaphore.Dispose();
        }

        totalSw.Stop();
        _log?.Info($"BatchUpload: 全部完成 prepared={prepared} uploaded={uploadedOk}/{total} elapsed={totalSw.ElapsedMilliseconds}ms");
        ReportProgress($"全部上传完成 {uploadedOk}/{total} 页", isFinished: true);

        return pages
            .Where(i => !string.IsNullOrEmpty(results.GetValueOrDefault(i)))
            .Select(i => (PageNumber: i + 1, FileKey: results[i]!))
            .ToList();
    }
}


/// <summary>批量 OCR 服务：提交批次 + 后台轮询。</summary>
public interface IBatchOcrService
{
    Task<BatchSubmitResponse> SubmitBatchAsync(
        List<string> fileKeys, string documentName,
        Dictionary<string, object>? options = null,
        List<int>? pageNumbers = null);

    Task StartPollingAsync(
        string batchUuid, int totalPages, string outputDir,
        Func<BatchStatusResponse, Task> onUpdate,
        CancellationToken ct);

    Task<List<BatchResumeItem>> GetPendingBatchesAsync();

    Task<BatchListResponse> ListBatchesAsync(int limit = 50, int offset = 0);

    /// <param name="forceFetchPages">1-based 页码：即使本地已有文件也要从服务端重新下发 result_json</param>
    Task<BatchStatusResponse> GetStatusAsync(
        string batchUuid, string? outputDir = null,
        IEnumerable<int>? forceFetchPages = null);

    Task<BatchCancelResponse> CancelBatchAsync(string batchUuid);
}

public class BatchOcrService : IBatchOcrService
{
    private readonly IApiService _api;
    private readonly IAppLogService? _log;
    private readonly ILibraryService? _library;

    public BatchOcrService(
        IApiService api,
        IAppLogService? log = null,
        ILibraryService? library = null)
    {
        _api = api;
        _log = log;
        _library = library;
    }

    public async Task<BatchSubmitResponse> SubmitBatchAsync(
        List<string> fileKeys, string documentName,
        Dictionary<string, object>? options = null,
        List<int>? pageNumbers = null)
    {
        var pagesHint = pageNumbers == null || pageNumbers.Count == 0
            ? $"{fileKeys.Count}p"
            : pageNumbers.Count == 1
                ? $"page={pageNumbers[0]}"
                : $"pages={pageNumbers[0]}-{pageNumbers[^1]}({pageNumbers.Count})";
        _log?.Info($"BatchOCR: 提交批次 doc={documentName} {pagesHint} keys={fileKeys.Count}");
        // 显式 snake_case 字典，避免 JsonPropertyName + SnakeCaseLower 组合在部分运行时产生歧义
        var payload = new Dictionary<string, object?>
        {
            ["file_keys"] = fileKeys,
            ["document_name"] = documentName,
        };
        if (pageNumbers != null && pageNumbers.Count > 0)
            payload["page_numbers"] = pageNumbers;
        if (options != null && options.Count > 0)
            payload["options"] = options;
        var resp = await _api.PostAsync<BatchSubmitResponse>(Constants.Endpoints.BatchSubmit, payload);
        var batchShort = string.IsNullOrEmpty(resp.BatchUuid)
            ? "?"
            : resp.BatchUuid[..Math.Min(16, resp.BatchUuid.Length)];
        _log?.Info(
            $"OCRTRACE|batch={batchShort}|event=submit|doc={documentName}|{pagesHint}|total={resp.TotalPages}");
        return resp;
    }

    public async Task StartPollingAsync(
        string batchUuid, int totalPages, string outputDir,
        Func<BatchStatusResponse, Task> onUpdate,
        CancellationToken ct)
    {
        int pollCount = 0;
        var batchShort = batchUuid[..Math.Min(16, batchUuid.Length)];
        IReadOnlyList<int>? forceFetch = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                pollCount++;

                var status = await GetStatusAsync(batchUuid, outputDir, forceFetch);
                forceFetch = null;

                _log?.Info(
                    $"BatchOCR poll#{pollCount}: status={status.Status} " +
                    $"pages_in_resp={status.NewlyCompleted.Count} " +
                    $"batch_completed={status.CompletedPages}/{status.TotalPages} " +
                    $"omitted={status.ResultsOmitted}");

                if (status.FailedPages > 0 && pollCount <= 3)
                {
                    _log?.Warn(
                        $"OCRTRACE|batch={batchShort}|event=poll_fail_hint|" +
                        $"failed={status.FailedPages}|completed={status.CompletedPages}|total={status.TotalPages}");
                    foreach (var fp in status.NewlyFailed.Take(5))
                    {
                        _log?.Warn(
                            $"OCRTRACE|batch={batchShort}|event=page_fail|" +
                            $"page={fp.PageNumber ?? (fp.PageIndex + 1)}|" +
                            $"err={(fp.ErrorMessage ?? "")[..Math.Min(80, (fp.ErrorMessage ?? "").Length)]}");
                    }
                }

                // 仅显式删除文献（Abandon）后停止落盘；勿用 library output_dir 未落库误判停同步
                // （与 TaskSyncService.ShouldSkipSaveAsync 一致）
                if (_library != null
                    && !string.IsNullOrWhiteSpace(outputDir)
                    && !await _library.IsOutputDirInLibraryAsync(outputDir))
                {
                    _log?.Warn(
                        $"BatchOCR: 文献库暂无此 OutputDir，仍继续落盘 batch={batchShort} dir={outputDir}");
                }

                OcrResultDisk.InvalidateUploadCacheForFailed(outputDir, status, _log);

                var saved = await OcrResultDisk.SaveCompletedPagesAsync(
                    outputDir, status, ct, _log);
                if (saved > 0)
                    _log?.Info($"BatchOCR poll#{pollCount}: 本轮落盘 {saved} 页");

                // 已完成但本地缺文件且未带 JSON → 下轮强制下发（破 have_pages 死锁）
                var needForce = OcrResultDisk.PagesNeedingForceFetch(outputDir, status);
                if (needForce.Count > 0)
                    forceFetch = needForce;

                await onUpdate(status);

                // 终态：必须本批次已完成页都已落盘，才结束（禁止用目录全局文件数冒充）
                if (status.Status is "completed" or "partial_failed" or "cancelled")
                {
                    if (!OcrResultDisk.BatchCompletedPagesOnDisk(outputDir, status))
                    {
                        if (pollCount >= 120)
                        {
                            _log?.Error(
                                $"BatchOCR: 终态后仍未齐盘，停止轮询 batch={batchShort} " +
                                $"completed={status.CompletedPages} force={needForce.Count}");
                            return;
                        }
                        _log?.Info(
                            $"BatchOCR: 批次已终态但本批页未齐盘 completed={status.CompletedPages} " +
                            $"truncated={status.ResultsTruncated} force={needForce.Count}，继续拉取");
                        continue;
                    }

                    _log?.Info(
                        $"OCRTRACE|batch={batchShort}|event=done|status={status.Status}|" +
                        $"completed={status.CompletedPages}|failed={status.FailedPages}|" +
                        $"cancelled={status.CancelledPages}|polls={pollCount}");
                    _log?.Info(
                        $"BatchOCR: 批次完成 {batchShort}... " +
                        $"completed={status.CompletedPages} failed={status.FailedPages} " +
                        $"cancelled={status.CancelledPages} total polls={pollCount}");
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Error($"OCRTRACE|batch={batchShort}|event=poll_error|err={ex.Message}");
                _log?.Error($"BatchOCR: 轮询异常 poll#{pollCount}: {ex.Message}");
            }
        }
    }

    public async Task<List<BatchResumeItem>> GetPendingBatchesAsync()
    {
        try
        {
            var resp = await _api.GetAsync<BatchResumeResponse>(Constants.Endpoints.BatchResume);
            return resp.Batches;
        }
        catch
        {
            return new List<BatchResumeItem>();
        }
    }

    public async Task<BatchListResponse> ListBatchesAsync(int limit = 50, int offset = 0)
    {
        var url = $"{Constants.Endpoints.BatchList}?limit={limit}&offset={offset}";
        return await _api.GetAsync<BatchListResponse>(url);
    }

    public async Task<BatchStatusResponse> GetStatusAsync(
        string batchUuid, string? outputDir = null,
        IEnumerable<int>? forceFetchPages = null)
    {
        var have = BuildHavePagesQuery(outputDir, forceFetchPages);
        var url = Constants.Endpoints.BatchStatus(batchUuid)
                  + "?result_limit=20"
                  + (string.IsNullOrEmpty(have) ? "" : "&have_pages=" + Uri.EscapeDataString(have));
        return await _api.GetAsync<BatchStatusResponse>(url);
    }

    /// <summary>扫描本地已落盘的 page_N_ocr_result.json，拼成 1-based 页码列表。</summary>
    internal static string BuildHavePagesQuery(
        string? outputDir, IEnumerable<int>? excludePages = null)
    {
        if (string.IsNullOrEmpty(outputDir) || !System.IO.Directory.Exists(outputDir))
            return "";
        var exclude = excludePages != null
            ? new HashSet<int>(excludePages.Where(p => p > 0))
            : null;
        var pages = new List<int>();
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(outputDir, "page_*_ocr_result.json"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                // page_12_ocr_result
                if (!name.StartsWith("page_", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rest = name["page_".Length..];
                var under = rest.IndexOf('_');
                var numPart = under >= 0 ? rest[..under] : rest;
                if (int.TryParse(numPart, out var n) && n > 0)
                {
                    if (exclude != null && exclude.Contains(n))
                        continue;
                    pages.Add(n);
                }
            }
        }
        catch { /* ignore */ }
        pages.Sort();
        return pages.Count == 0 ? "" : string.Join(",", pages);
    }

    public async Task<BatchCancelResponse> CancelBatchAsync(string batchUuid)
    {
        return await _api.PostAsync<BatchCancelResponse>(
            Constants.Endpoints.BatchCancel(batchUuid), null);
    }
}


/// <summary>本地批次缓存：客户端 SQLite 持久化 BatchID，用于离线复活与任务中心镜像。</summary>
public interface IBatchCacheService
{
    Task SaveBatchAsync(string batchUuid, string documentName, int totalPages, string outputDir,
        string status = "queued", int completedPages = 0, int failedPages = 0, int uploadedPages = 0);
    Task UpdateProgressAsync(string batchUuid, string status, int completedPages, int failedPages,
        int uploadedPages = -1, int cancelledPages = 0);
    Task<List<BatchCacheEntry>> LoadPendingBatchesAsync();
    Task<List<BatchCacheEntry>> LoadAllBatchesAsync();
    Task RemoveBatchAsync(string batchUuid);
}

public class BatchCacheEntry
{
    public string BatchUuid { get; set; } = "";
    public string DocumentName { get; set; } = "";
    public int TotalPages { get; set; }
    public string OutputDir { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string Status { get; set; } = "queued";
    public int CompletedPages { get; set; }
    public int FailedPages { get; set; }
    public int CancelledPages { get; set; }
    public int UploadedPages { get; set; }
}

public class BatchCacheService : IBatchCacheService
{
    private readonly string _dbPath;

    public BatchCacheService(string dbPath)
    {
        _dbPath = dbPath;
        EnsureTable();
    }

    public BatchCacheService(ITokenStorage tokenStorage)
        : this(ClientDataPaths.GetDatabasePath())
    {
        // 兼容旧构造：tokenStorage 未使用，仅占位
        _ = tokenStorage;
    }

    private void EnsureTable()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS batch_cache (
                batch_uuid TEXT PRIMARY KEY,
                document_name TEXT,
                total_pages INTEGER DEFAULT 0,
                output_dir TEXT,
                created_at TEXT,
                status TEXT DEFAULT 'queued',
                completed_pages INTEGER DEFAULT 0,
                failed_pages INTEGER DEFAULT 0,
                cancelled_pages INTEGER DEFAULT 0,
                uploaded_pages INTEGER DEFAULT 0
            )";
        cmd.ExecuteNonQuery();

        // 兼容旧表：补列
        foreach (var col in new[]
        {
            ("status", "TEXT DEFAULT 'queued'"),
            ("completed_pages", "INTEGER DEFAULT 0"),
            ("failed_pages", "INTEGER DEFAULT 0"),
            ("cancelled_pages", "INTEGER DEFAULT 0"),
            ("uploaded_pages", "INTEGER DEFAULT 0"),
        })
        {
            try
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE batch_cache ADD COLUMN {col.Item1} {col.Item2}";
                alter.ExecuteNonQuery();
            }
            catch { /* column exists */ }
        }
    }

    public async Task SaveBatchAsync(string batchUuid, string documentName, int totalPages, string outputDir,
        string status = "queued", int completedPages = 0, int failedPages = 0, int uploadedPages = 0)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO batch_cache
                (batch_uuid, document_name, total_pages, output_dir, created_at,
                 status, completed_pages, failed_pages, cancelled_pages, uploaded_pages)
            VALUES (@uuid, @doc, @pages, @dir, @now, @status, @done, @fail, 0, @up)";
        cmd.Parameters.AddWithValue("@uuid", batchUuid);
        cmd.Parameters.AddWithValue("@doc", documentName ?? "");
        cmd.Parameters.AddWithValue("@pages", totalPages);
        cmd.Parameters.AddWithValue("@dir", outputDir);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@done", completedPages);
        cmd.Parameters.AddWithValue("@fail", failedPages);
        cmd.Parameters.AddWithValue("@up", uploadedPages);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateProgressAsync(string batchUuid, string status, int completedPages, int failedPages,
        int uploadedPages = -1, int cancelledPages = 0)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        if (uploadedPages >= 0)
        {
            cmd.CommandText = @"
                UPDATE batch_cache SET status=@status, completed_pages=@done, failed_pages=@fail,
                    cancelled_pages=@cancel, uploaded_pages=@up WHERE batch_uuid=@uuid";
            cmd.Parameters.AddWithValue("@up", uploadedPages);
        }
        else
        {
            cmd.CommandText = @"
                UPDATE batch_cache SET status=@status, completed_pages=@done, failed_pages=@fail,
                    cancelled_pages=@cancel WHERE batch_uuid=@uuid";
        }
        cmd.Parameters.AddWithValue("@uuid", batchUuid);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@done", completedPages);
        cmd.Parameters.AddWithValue("@fail", failedPages);
        cmd.Parameters.AddWithValue("@cancel", cancelledPages);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<BatchCacheEntry>> LoadPendingBatchesAsync()
    {
        var all = await LoadAllBatchesAsync();
        return all.Where(b =>
            b.Status is "queued" or "processing" or "uploading").ToList();
    }

    public async Task<List<BatchCacheEntry>> LoadAllBatchesAsync()
    {
        var list = new List<BatchCacheEntry>();
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT batch_uuid, document_name, total_pages, output_dir, created_at,
                       COALESCE(status,'queued'), COALESCE(completed_pages,0),
                       COALESCE(failed_pages,0), COALESCE(cancelled_pages,0),
                       COALESCE(uploaded_pages,0)
                FROM batch_cache ORDER BY created_at DESC";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new BatchCacheEntry
                {
                    BatchUuid = reader.GetString(0),
                    DocumentName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    TotalPages = reader.GetInt32(2),
                    OutputDir = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    CreatedAt = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Status = reader.IsDBNull(5) ? "queued" : reader.GetString(5),
                    CompletedPages = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    FailedPages = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    CancelledPages = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    UploadedPages = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                });
            }
        }
        catch { /* table may not exist yet */ }
        return list;
    }

    public async Task RemoveBatchAsync(string batchUuid)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM batch_cache WHERE batch_uuid = @uuid";
        cmd.Parameters.AddWithValue("@uuid", batchUuid);
        await cmd.ExecuteNonQueryAsync();
    }
}
