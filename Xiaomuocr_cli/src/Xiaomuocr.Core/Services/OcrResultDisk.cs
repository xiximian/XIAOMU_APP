using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// OCR 结果落盘约定：用户识别成功后，对应 page_N_ocr_result.json 必须存在。
/// 判定「是否齐」只能按本批次页码检查，不能用目录内全局文件数对比 CompletedPages。
/// </summary>
public static class OcrResultDisk
{
    public static string JsonPath(string outputDir, int pageNumber1Based)
        => Path.Combine(outputDir, $"page_{pageNumber1Based}_ocr_result.json");

    public static int PageNumberOf(BatchPageResult page)
        => page.PageNumber is > 0 ? page.PageNumber.Value : page.PageIndex + 1;

    public static bool LocalFileExists(string? outputDir, int pageNumber1Based)
    {
        if (string.IsNullOrEmpty(outputDir) || pageNumber1Based <= 0)
            return false;
        try { return File.Exists(JsonPath(outputDir, pageNumber1Based)); }
        catch { return false; }
    }

    public static bool IsTerminalBatchStatus(string? status)
        => status is "completed" or "partial_failed" or "cancelled";

    /// <summary>
    /// 服务端批次已收尾（终态且无排队/处理中，页数对得上）。
    /// </summary>
    public static bool IsBatchServerSettled(BatchStatusResponse status)
    {
        if (!IsTerminalBatchStatus(status.Status))
            return false;
        if (status.QueuedPages > 0 || status.ProcessingPages > 0)
            return false;
        if (status.TotalPages <= 0)
            return true;
        return status.CompletedPages + status.FailedPages + status.CancelledPages >= status.TotalPages;
    }

    /// <summary>
    /// 本批次可视为「识别+落盘都完成」：仅终态批次；且已完成页均已落盘。
    /// 进行中即便部分页已落盘也必须返回 false，否则底栏会抢先显示「识别完成」。
    /// </summary>
    public static bool BatchCompletedPagesOnDisk(string? outputDir, BatchStatusResponse status)
    {
        if (!IsBatchServerSettled(status))
            return false;
        if (status.ResultsTruncated)
            return false;
        // 终态但无可落盘页（全失败/取消）
        if (status.CompletedPages <= 0)
            return true;
        if (string.IsNullOrEmpty(outputDir))
            return false;

        var listed = status.NewlyCompleted ?? new List<BatchPageResult>();
        if (listed.Count < status.CompletedPages)
            return false;

        foreach (var page in listed)
        {
            if (!LocalFileExists(outputDir, PageNumberOf(page)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 已完成但本地缺失且本次未带 result_json 的页 → 下次请求需从 have_pages 排除以便下发。
    /// </summary>
    public static List<int> PagesNeedingForceFetch(string? outputDir, BatchStatusResponse status)
    {
        var need = new List<int>();
        if (string.IsNullOrEmpty(outputDir) || status.NewlyCompleted == null)
            return need;
        foreach (var page in status.NewlyCompleted)
        {
            if (!string.IsNullOrEmpty(page.ResultJson))
                continue;
            var n = PageNumberOf(page);
            if (!LocalFileExists(outputDir, n))
                need.Add(n);
        }
        return need;
    }

    public static void ClearPages(
        string? outputDir,
        IEnumerable<int> pageNumbers1Based,
        IAppLogService? log = null)
    {
        if (string.IsNullOrEmpty(outputDir)) return;
        foreach (var pageNo in pageNumbers1Based.Distinct())
        {
            if (pageNo <= 0) continue;
            var path = JsonPath(outputDir, pageNo);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    log?.Info($"OCR落盘: 已清除旧结果 page={pageNo}（待重新拉取）");
                }
                catch (Exception ex)
                {
                    log?.Warn($"OCR落盘: 清除旧结果失败 page={pageNo}: {ex.Message}");
                }
            }
            // 服务端 OCR 终态（成功/失败）会删临时图；旧 upload sidecar 必须一并清掉
            UploadUrlStore.TryDelete(outputDir, pageNo - 1);
        }
    }

    /// <summary>
    /// 提交识别前清除上传缓存，强制重新上传。
    /// 避免复用已被服务端删除的 local/...jpg file_key（表现为「无法加载图片」）。
    /// </summary>
    public static void InvalidateUploadCache(
        string? outputDir,
        IEnumerable<int> pageNumbers1Based,
        IAppLogService? log = null)
    {
        if (string.IsNullOrEmpty(outputDir)) return;
        foreach (var pageNo in pageNumbers1Based.Distinct())
        {
            if (pageNo <= 0) continue;
            var path = UploadUrlStore.SidecarPath(outputDir, pageNo);
            if (!File.Exists(path)) continue;
            UploadUrlStore.TryDelete(outputDir, pageNo - 1);
            log?.Info($"OCR上传: 已清除陈旧 key 缓存 page={pageNo}（将重新上传）");
        }
    }

    /// <summary>OCR 失败页：服务端通常已删图，清本地 upload sidecar 以便重试时重传。</summary>
    public static void InvalidateUploadCacheForFailed(
        string? outputDir,
        BatchStatusResponse status,
        IAppLogService? log = null)
    {
        if (string.IsNullOrEmpty(outputDir) || status.NewlyFailed == null || status.NewlyFailed.Count == 0)
            return;
        var pages = status.NewlyFailed.Select(p =>
            p.PageNumber is > 0 ? p.PageNumber.Value : p.PageIndex + 1);
        InvalidateUploadCache(outputDir, pages, log);
    }

    public static async Task<int> SaveCompletedPagesAsync(
        string? outputDir,
        BatchStatusResponse status,
        CancellationToken ct = default,
        IAppLogService? log = null)
    {
        if (string.IsNullOrEmpty(outputDir) || status.NewlyCompleted == null
            || status.NewlyCompleted.Count == 0)
            return 0;

        Directory.CreateDirectory(outputDir);
        int saved = 0;
        foreach (var page in status.NewlyCompleted)
        {
            if (string.IsNullOrEmpty(page.ResultJson))
                continue;

            var pageNo = PageNumberOf(page);
            var jsonPath = JsonPath(outputDir, pageNo);
            var jsonToWrite = page.ResultJson;
            var sidecar = OcrPreprocessStore.TryLoadSidecar(outputDir, pageNo - 1);
            if (sidecar != null)
                jsonToWrite = OcrPreprocessStore.InjectIntoResultJson(jsonToWrite, sidecar);

            var overwritten = File.Exists(jsonPath);
            await File.WriteAllTextAsync(jsonPath, jsonToWrite, ct);
            UploadUrlStore.TryDelete(outputDir, pageNo - 1);
            saved++;
            log?.Info(
                $"OCR落盘: {(overwritten ? "已覆盖" : "已保存")} page_{pageNo} " +
                $"len={jsonToWrite.Length}" +
                (sidecar != null ? $" scale={sidecar.ResizeScale:F4}" : ""));
        }
        return saved;
    }
}
