using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 统一导航 PaddleOCR 结果 JSON 的两种外壳：
/// 1) 官方任务回包：{ "result": { "layoutParsingResults": [...] } }
/// 2) resultUrl / 手工导出：{ "layoutParsingResults": [...] }（见 临时日志/拓片.json）
/// </summary>
public static class OcrResultJson
{
    public static bool TryGetLayoutParsingResults(JsonElement root, out JsonElement layouts)
    {
        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("layoutParsingResults", out layouts)
            && layouts.ValueKind == JsonValueKind.Array)
            return true;

        if (root.TryGetProperty("layoutParsingResults", out layouts)
            && layouts.ValueKind == JsonValueKind.Array)
            return true;

        layouts = default;
        return false;
    }

    public static JsonArray? GetLayoutParsingResults(JsonNode root)
    {
        if (root["result"]?["layoutParsingResults"] is JsonArray nested)
            return nested;
        if (root["layoutParsingResults"] is JsonArray top)
            return top;
        return null;
    }
}
