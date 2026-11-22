using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 批量 OCR 上传图的预处理元数据落盘 / 注入。
/// 排队池路径不会把 preprocess 传到服务端，必须在客户端本地保留，
/// 否则压缩图（MaxImageDim）上的 bbox 无法还原到 PDF 坐标，表现为框明显偏小。
/// </summary>
public static class OcrPreprocessStore
{
    public const int MaxImageDim = 2000;
    public const float OcrScale = 2.0f;

    public static string SidecarPath(string outputDir, int pageIndex1Based)
        => Path.Combine(outputDir, $"page_{pageIndex1Based}_preprocess.json");

    public static async Task SaveAsync(string outputDir, int pageIndex0Based, PreprocessInfo info, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var path = SidecarPath(outputDir, pageIndex0Based + 1);
        var obj = new
        {
            resize_scale = info.ResizeScale,
            original_size = info.OriginalSize,
            resized_size = info.ResizedSize ?? info.OriginalSize,
        };
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(path, json, ct);
    }

    public static PreprocessInfo? TryLoadSidecar(string outputDir, int pageIndex0Based)
    {
        var path = SidecarPath(outputDir, pageIndex0Based + 1);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new PreprocessInfo
            {
                ResizeScale = root.TryGetProperty("resize_scale", out var rs) ? rs.GetDouble() : 1.0,
                OriginalSize = ReadIntArray(root, "original_size"),
                ResizedSize = ReadIntArray(root, "resized_size"),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将 sidecar / 推断出的 preprocess 写入 OCR 结果 JSON（已有 _preprocess 则不覆盖）。
    /// </summary>
    public static string InjectIntoResultJson(string resultJson, PreprocessInfo? info)
    {
        if (info == null || string.IsNullOrWhiteSpace(resultJson))
            return resultJson;
        if (info.ResizeScale <= 0 || info.ResizeScale >= 1.0 - 1e-9)
        {
            // scale≈1 也写入，方便排查；但无 original/resized 时跳过
            if (info.OriginalSize == null || info.ResizedSize == null)
                return resultJson;
        }

        try
        {
            var root = JsonNode.Parse(resultJson) as JsonObject;
            if (root == null) return resultJson;
            if (root.ContainsKey("_preprocess"))
                return resultJson;

            var orig = info.OriginalSize ?? Array.Empty<int>();
            var resized = info.ResizedSize ?? info.OriginalSize ?? Array.Empty<int>();
            if (orig.Length < 2 || resized.Length < 2)
                return resultJson;

            root["_preprocess"] = new JsonObject
            {
                ["resize_scale"] = info.ResizeScale,
                ["original_size"] = new JsonArray(orig[0], orig[1]),
                ["resized_size"] = new JsonArray(resized[0], resized[1]),
            };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return resultJson;
        }
    }

    /// <summary>
    /// 加载结果时补齐 ResizeScale：sidecar → 按 PDF 页尺寸推断（与 PdfRenderService 压缩算法一致）。
    /// </summary>
    public static PreprocessInfo? Enrich(
        PreprocessInfo? existing,
        string jsonDir,
        int pageIndex0Based,
        double pdfPageWidth = 0,
        double pdfPageHeight = 0)
    {
        var pp = existing ?? new PreprocessInfo();
        bool needScale = pp.ResizeScale <= 0 || pp.ResizeScale >= 1.0
                         || pp.OriginalSize == null || pp.OriginalSize.Length < 2
                         || pp.ResizedSize == null || pp.ResizedSize.Length < 2;

        if (!needScale)
            return pp;

        var sidecar = TryLoadSidecar(jsonDir, pageIndex0Based);
        if (sidecar != null && sidecar.ResizeScale > 0 && sidecar.ResizeScale < 1.0
            && sidecar.OriginalSize is { Length: >= 2 } && sidecar.ResizedSize is { Length: >= 2 })
        {
            pp.ResizeScale = sidecar.ResizeScale;
            pp.OriginalSize = sidecar.OriginalSize;
            pp.ResizedSize = sidecar.ResizedSize;
            return pp;
        }

        if (pdfPageWidth > 1 && pdfPageHeight > 1)
        {
            int origW = (int)Math.Round(pdfPageWidth * OcrScale);
            int origH = (int)Math.Round(pdfPageHeight * OcrScale);
            int maxDim = Math.Max(origW, origH);
            if (maxDim > MaxImageDim)
            {
                double s = (double)MaxImageDim / maxDim;
                int sw = (int)(origW * s);
                int sh = (int)(origH * s);

                // 若 OCR 结果图尺寸已知，校验推断（允许方向旋转导致宽高对调）
                int ow = pp.OrientedWidth, oh = pp.OrientedHeight;
                if (ow > 0 && oh > 0 && !SizeMatches(ow, oh, sw, sh))
                {
                    // 不强制；仍按渲染算法写入（与上传图一致）
                }

                pp.ResizeScale = s;
                pp.OriginalSize = new[] { origW, origH };
                pp.ResizedSize = new[] { sw, sh };
            }
            else
            {
                pp.ResizeScale = 1.0;
                pp.OriginalSize ??= new[] { origW, origH };
                pp.ResizedSize ??= new[] { origW, origH };
            }
        }

        return pp;
    }

    private static bool SizeMatches(int a, int b, int x, int y, int tol = 4)
        => (Math.Abs(a - x) <= tol && Math.Abs(b - y) <= tol)
           || (Math.Abs(a - y) <= tol && Math.Abs(b - x) <= tol);

    private static int[]? ReadIntArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<int>();
        foreach (var x in el.EnumerateArray())
            list.Add((int)x.GetDouble());
        return list.Count >= 2 ? list.ToArray() : null;
    }
}
