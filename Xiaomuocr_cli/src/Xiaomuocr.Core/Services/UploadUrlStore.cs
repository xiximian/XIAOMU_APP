using System.Text.Json;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 页上传成功 file_key 的本地 sidecar，供断点续传跳过已传页。
/// 文件：page_{N}.upload.json → {"file_key":"..."}（兼容读旧 file_url）
/// </summary>
public static class UploadUrlStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static string SidecarPath(string outputDir, int pageIndex1Based)
        => Path.Combine(outputDir, $"page_{pageIndex1Based}.upload.json");

    /// <summary>读取已缓存的 file_key；兼容旧 sidecar 的 file_url 字段。</summary>
    public static string? TryLoadUrl(string outputDir, int pageIndex0Based)
        => TryLoadKey(outputDir, pageIndex0Based);

    public static string? TryLoadKey(string outputDir, int pageIndex0Based)
    {
        var path = SidecarPath(outputDir, pageIndex0Based + 1);
        if (!File.Exists(path)) return null;
        try
        {
            // local OSS 会在 OCR 后删盘 / TTL GC；过期 sidecar 会导致「本地图片不存在」
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > TimeSpan.FromHours(3))
            {
                TryDelete(outputDir, pageIndex0Based);
                return null;
            }

            // 该页已有 OCR 结果时，服务端文件通常已热删除，必须重传
            var ocrPath = Path.Combine(outputDir, $"page_{pageIndex0Based + 1}_ocr_result.json");
            if (File.Exists(ocrPath))
            {
                TryDelete(outputDir, pageIndex0Based);
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("file_key", out var keyEl))
            {
                var key = keyEl.GetString();
                if (!string.IsNullOrWhiteSpace(key)) return key;
            }
            // 旧版 sidecar（URL 契约）不再可用于 submit，视为未上传
            if (doc.RootElement.TryGetProperty("file_url", out _))
                return null;
        }
        catch
        {
            /* ignore corrupt sidecar */
        }
        return null;
    }

    public static async Task SaveAsync(string outputDir, int pageIndex0Based, string fileKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileKey)) return;
        Directory.CreateDirectory(outputDir);
        var path = SidecarPath(outputDir, pageIndex0Based + 1);
        var json = JsonSerializer.Serialize(new { file_key = fileKey }, JsonOpts);
        await File.WriteAllTextAsync(path, json, ct);
    }

    public static void TryDelete(string outputDir, int pageIndex0Based)
    {
        try
        {
            var path = SidecarPath(outputDir, pageIndex0Based + 1);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }
}
