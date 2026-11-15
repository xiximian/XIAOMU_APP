using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// OCR 结果解析服务：从 JSON 文件中提取 bbox 坐标和文本内容，
/// 应用 preprocess 元数据进行坐标转换。
/// 兼容两种结果：
/// 1) 版面模式 parsing_res_list（block_bbox + block_content）
/// 2) 拓片/spotting 模式 spotting_res（rec_polys + rec_texts，按行）
/// </summary>
public interface IOcrResultService
{
    /// <param name="pdfPageWidth">PDF 页宽（点），用于缺失 _preprocess 时按压缩算法推断</param>
    /// <param name="pdfPageHeight">PDF 页高（点）</param>
    Task<OcrPageResult?> LoadPageResultAsync(
        string jsonDir, int pageIndex,
        double pdfPageWidth = 0, double pdfPageHeight = 0);

    /// <summary>
    /// 将编辑后的文本写回 page_N_ocr_result.json（按加载时的 SourceKind/SourceIndex）。
    /// </summary>
    Task SavePageTextsAsync(string jsonDir, int pageIndex, IReadOnlyList<OcrTextEdit> edits);
}

/// <summary>单页 OCR 解析结果</summary>
public class OcrPageResult
{
    public List<OcrTextBlock> Blocks { get; init; } = new();
    public PreprocessInfo? Preprocess { get; init; }
}

/// <summary>OCR 文本来源（决定写回路径）</summary>
public enum OcrTextSourceKind
{
    Layout = 0,
    Spotting = 1,
}

/// <summary>单个 OCR 文本块</summary>
public class OcrTextBlock
{
    /// <summary>原始 bbox (在 OCR 图片空间中的坐标)</summary>
    public double[]? Bbox { get; init; }

    /// <summary>识别文字</summary>
    public string Text { get; set; } = "";

    /// <summary>文本块标签 (如 paragraph_title / spotting_line)</summary>
    public string? Label { get; init; }

    /// <summary>排序用的 block_order</summary>
    public int Order { get; init; }

    /// <summary>写回来源类型</summary>
    public OcrTextSourceKind SourceKind { get; init; } = OcrTextSourceKind.Layout;

    /// <summary>
    /// 写回索引：Layout → parsing_res_list 原始下标；Spotting → rec_texts 下标。
    /// </summary>
    public int SourceIndex { get; init; } = -1;
}

/// <summary>待写回的一条文本编辑</summary>
public sealed class OcrTextEdit
{
    public OcrTextSourceKind SourceKind { get; init; }
    public int SourceIndex { get; init; }
    public string Text { get; init; } = "";
}

public class OcrResultService : IOcrResultService
{
    public Task<OcrPageResult?> LoadPageResultAsync(
        string jsonDir, int pageIndex,
        double pdfPageWidth = 0, double pdfPageHeight = 0)
    {
        var jsonPath = GetJsonPath(jsonDir, pageIndex);
        if (!File.Exists(jsonPath))
            return Task.FromResult<OcrPageResult?>(null);

        try
        {
            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);

            PreprocessInfo? preprocess = null;
            if (doc.RootElement.TryGetProperty("_preprocess", out var pp))
            {
                preprocess = new PreprocessInfo
                {
                    ResizeScale = pp.TryGetProperty("resize_scale", out var rs) ? rs.GetDouble() : 1.0,
                    OriginalSize = pp.TryGetProperty("original_size", out var os)
                        ? os.EnumerateArray().Select(x => (int)x.GetDouble()).ToArray() : null,
                    ResizedSize = pp.TryGetProperty("resized_size", out var rsz)
                        ? rsz.EnumerateArray().Select(x => (int)x.GetDouble()).ToArray() : null,
                };
            }

            var blocks = new List<OcrTextBlock>();
            // spotting 与版面并存时必须优先 spotting_res（按行）；parsing_res_list 多为整页汇总块
            if (OcrResultJson.TryGetLayoutParsingResults(doc.RootElement, out var layouts))
            {
                foreach (var pageResult in layouts.EnumerateArray())
                {
                    if (!pageResult.TryGetProperty("prunedResult", out var pruned))
                        continue;

                    preprocess ??= new PreprocessInfo();
                    if (pruned.TryGetProperty("width", out var ow) && ow.ValueKind == JsonValueKind.Number)
                        preprocess.OrientedWidth = ow.GetInt32();
                    if (pruned.TryGetProperty("height", out var oh) && oh.ValueKind == JsonValueKind.Number)
                        preprocess.OrientedHeight = oh.GetInt32();
                    if (pruned.TryGetProperty("doc_preprocessor_res", out var dpp)
                        && dpp.TryGetProperty("angle", out var ang)
                        && ang.ValueKind == JsonValueKind.Number)
                        preprocess.OrientationAngle = ang.GetInt32();

                    // 优先：拓片 spotting 按行结果（与 parsing_res_list 并存时不可走汇总块）
                    var spottingBlocks = TryParseSpottingRes(pruned);
                    if (spottingBlocks.Count > 0)
                    {
                        blocks.AddRange(spottingBlocks);
                        continue;
                    }

                    // 回退：版面 parsing_res_list
                    blocks.AddRange(ParseParsingResList(pruned));
                }
            }

            // 批量结果常缺 _preprocess：从 sidecar / PDF 尺寸补齐压缩比例
            preprocess = OcrPreprocessStore.Enrich(
                preprocess, jsonDir, pageIndex, pdfPageWidth, pdfPageHeight);

            // 若 JSON 里没有 _preprocess 但已推断出压缩，回写一次（修复旧结果）
            if (preprocess != null
                && preprocess.ResizeScale > 0 && preprocess.ResizeScale < 1.0
                && !doc.RootElement.TryGetProperty("_preprocess", out _))
            {
                try
                {
                    var patched = OcrPreprocessStore.InjectIntoResultJson(json, preprocess);
                    if (!ReferenceEquals(patched, json) && patched != json)
                        File.WriteAllText(jsonPath, patched);
                }
                catch { /* 回写失败不影响本次显示 */ }
            }

            return Task.FromResult<OcrPageResult?>(new OcrPageResult
            {
                Blocks = blocks,
                Preprocess = preprocess,
            });
        }
        catch (Exception)
        {
            return Task.FromResult<OcrPageResult?>(null);
        }
    }

    public Task SavePageTextsAsync(string jsonDir, int pageIndex, IReadOnlyList<OcrTextEdit> edits)
    {
        if (edits == null || edits.Count == 0)
            return Task.CompletedTask;

        var jsonPath = GetJsonPath(jsonDir, pageIndex);
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("OCR 结果文件不存在", jsonPath);

        var json = File.ReadAllText(jsonPath);
        var root = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("OCR JSON 解析失败");

        var layouts = OcrResultJson.GetLayoutParsingResults(root);
        if (layouts == null || layouts.Count == 0)
            throw new InvalidOperationException("JSON 缺少 layoutParsingResults");

        // 按加载逻辑：找到第一个可用的 prunedResult 写入
        JsonObject? pruned = null;
        foreach (var pageNode in layouts)
        {
            if (pageNode?["prunedResult"] is JsonObject p)
            {
                pruned = p;
                break;
            }
        }
        if (pruned == null)
            throw new InvalidOperationException("JSON 缺少 prunedResult");

        bool hasSpottingEdits = edits.Any(e => e.SourceKind == OcrTextSourceKind.Spotting);
        bool hasLayoutEdits = edits.Any(e => e.SourceKind == OcrTextSourceKind.Layout);

        if (hasSpottingEdits)
            ApplySpottingEdits(pruned, edits.Where(e => e.SourceKind == OcrTextSourceKind.Spotting).ToList());

        if (hasLayoutEdits)
            ApplyLayoutEdits(pruned, edits.Where(e => e.SourceKind == OcrTextSourceKind.Layout).ToList());

        // spotting 编辑后同步整页汇总块（若存在）
        if (hasSpottingEdits)
            SyncSpottingSummaryBlock(pruned);

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, root.ToJsonString(options));
        return Task.CompletedTask;
    }

    private static string GetJsonPath(string jsonDir, int pageIndex)
        => System.IO.Path.Combine(jsonDir, $"page_{pageIndex + 1}_ocr_result.json");

    private static void ApplySpottingEdits(JsonObject pruned, List<OcrTextEdit> edits)
    {
        if (pruned["spotting_res"] is not JsonObject spotting)
            throw new InvalidOperationException("JSON 缺少 spotting_res，无法写回拓片文本");

        if (spotting["rec_texts"] is not JsonArray texts)
            throw new InvalidOperationException("JSON 缺少 spotting_res.rec_texts");

        foreach (var edit in edits)
        {
            if (edit.SourceIndex < 0 || edit.SourceIndex >= texts.Count)
                continue;
            texts[edit.SourceIndex] = edit.Text ?? "";
        }
    }

    private static void ApplyLayoutEdits(JsonObject pruned, List<OcrTextEdit> edits)
    {
        if (pruned["parsing_res_list"] is not JsonArray items)
            throw new InvalidOperationException("JSON 缺少 parsing_res_list，无法写回版面文本");

        foreach (var edit in edits)
        {
            if (edit.SourceIndex < 0 || edit.SourceIndex >= items.Count)
                continue;
            if (items[edit.SourceIndex] is not JsonObject item)
                continue;

            if (item.ContainsKey("block_content"))
                item["block_content"] = edit.Text ?? "";
            else if (item.ContainsKey("text"))
                item["text"] = edit.Text ?? "";
            else if (item.ContainsKey("content"))
                item["content"] = edit.Text ?? "";
            else
                item["block_content"] = edit.Text ?? "";
        }
    }

    /// <summary>将 spotting 行文本拼回 parsing_res_list 中 label=spotting 的汇总块（若有）</summary>
    private static void SyncSpottingSummaryBlock(JsonObject pruned)
    {
        if (pruned["spotting_res"] is not JsonObject spotting
            || spotting["rec_texts"] is not JsonArray texts
            || pruned["parsing_res_list"] is not JsonArray items)
            return;

        var joined = string.Join("\n", texts.Select(t =>
            t is JsonValue jv ? (jv.TryGetValue<string>(out var s) ? s : jv.ToString())
            : (t?.ToString() ?? "")));
        foreach (var node in items)
        {
            if (node is not JsonObject item) continue;
            var label = item["block_label"]?.GetValue<string>();
            if (!string.Equals(label, "spotting", StringComparison.OrdinalIgnoreCase))
                continue;
            if (item.ContainsKey("block_content"))
                item["block_content"] = joined;
            else if (item.ContainsKey("text"))
                item["text"] = joined;
            else
                item["block_content"] = joined;
        }
    }

    /// <summary>
    /// spotting 模式：spotting_res.rec_texts[i] + rec_polys[i]（四点多边形 → AABB）
    /// </summary>
    private static List<OcrTextBlock> TryParseSpottingRes(JsonElement pruned)
    {
        var blocks = new List<OcrTextBlock>();
        if (!pruned.TryGetProperty("spotting_res", out var spotting)
            || spotting.ValueKind != JsonValueKind.Object)
            return blocks;

        if (!spotting.TryGetProperty("rec_texts", out var texts)
            || !spotting.TryGetProperty("rec_polys", out var polys)
            || texts.ValueKind != JsonValueKind.Array
            || polys.ValueKind != JsonValueKind.Array)
            return blocks;

        var textList = texts.EnumerateArray().ToList();
        var polyList = polys.EnumerateArray().ToList();
        int n = Math.Min(textList.Count, polyList.Count);
        if (n == 0) return blocks;

        for (int i = 0; i < n; i++)
        {
            var text = textList[i].ValueKind == JsonValueKind.String
                ? textList[i].GetString() ?? ""
                : textList[i].ToString();

            var bbox = PolygonToBbox(polyList[i]);
            if (bbox == null) continue;

            blocks.Add(new OcrTextBlock
            {
                Bbox = bbox,
                Text = text,
                Label = "spotting_line",
                Order = i + 1,
                SourceKind = OcrTextSourceKind.Spotting,
                SourceIndex = i,
            });
        }

        return blocks;
    }

    /// <summary>版面模式：parsing_res_list 的 block_bbox + block_content</summary>
    private static List<OcrTextBlock> ParseParsingResList(JsonElement pruned)
    {
        var blocks = new List<OcrTextBlock>();
        if (!pruned.TryGetProperty("parsing_res_list", out var parsingList)
            || parsingList.ValueKind != JsonValueKind.Array)
            return blocks;

        var items = parsingList.EnumerateArray()
            .Select((item, index) =>
            {
                int order = 999;
                if (item.TryGetProperty("block_order", out var bo)
                    && bo.ValueKind == JsonValueKind.Number)
                    order = bo.GetInt32();

                double[]? bbox = null;
                if (item.TryGetProperty("block_bbox", out var bb)
                    && bb.ValueKind == JsonValueKind.Array)
                {
                    var arr = bb.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    if (arr.Length == 4) bbox = arr;
                }
                if (bbox == null && item.TryGetProperty("block_polygon_points", out var poly))
                    bbox = PolygonToBbox(poly);

                string text = "";
                if (item.TryGetProperty("block_content", out var bc)
                    && bc.ValueKind == JsonValueKind.String)
                    text = bc.GetString() ?? "";
                else if (item.TryGetProperty("text", out var t)
                    && t.ValueKind == JsonValueKind.String)
                    text = t.GetString() ?? "";
                else if (item.TryGetProperty("content", out var c)
                    && c.ValueKind == JsonValueKind.String)
                    text = c.GetString() ?? "";

                string? label = null;
                if (item.TryGetProperty("block_label", out var bl)
                    && bl.ValueKind == JsonValueKind.String)
                    label = bl.GetString();

                return new { Index = index, Order = order, Bbox = bbox, Text = text, Label = label };
            })
            .OrderBy(x => x.Order)
            .ToList();

        foreach (var item in items)
        {
            if (item.Bbox == null || item.Bbox.Length != 4) continue;
            blocks.Add(new OcrTextBlock
            {
                Bbox = item.Bbox,
                Text = item.Text ?? "",
                Label = item.Label,
                Order = item.Order,
                SourceKind = OcrTextSourceKind.Layout,
                SourceIndex = item.Index,
            });
        }

        return blocks;
    }

    /// <summary>
    /// 多边形 [[x,y],...] 或扁平坐标 → AABB [x1,y1,x2,y2]
    /// </summary>
    private static double[]? PolygonToBbox(JsonElement poly)
    {
        if (poly.ValueKind != JsonValueKind.Array) return null;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        bool any = false;

        foreach (var pt in poly.EnumerateArray())
        {
            double x, y;
            if (pt.ValueKind == JsonValueKind.Array)
            {
                var coords = pt.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                if (coords.Length < 2) continue;
                x = coords[0];
                y = coords[1];
            }
            else if (pt.ValueKind == JsonValueKind.Number)
            {
                continue;
            }
            else continue;

            any = true;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        if (!any)
        {
            var flat = poly.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.Number)
                .Select(v => v.GetDouble())
                .ToList();
            if (flat.Count >= 4 && flat.Count % 2 == 0)
            {
                for (int i = 0; i < flat.Count; i += 2)
                {
                    double x = flat[i], y = flat[i + 1];
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
                any = true;
            }
        }

        if (!any || minX > maxX || minY > maxY) return null;
        return new[] { minX, minY, maxX, maxY };
    }
}
