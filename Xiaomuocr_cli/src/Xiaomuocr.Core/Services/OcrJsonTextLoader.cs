using System.Text.Json;

namespace Xiaomuocr.Core.Services;

/// <summary>从单页 OCR JSON 提取按阅读顺序排列的文本块（检索 / 建索引共用）。</summary>
internal static class OcrJsonTextLoader
{
    public static async Task<List<string>> LoadPageTextsAsync(string jsonPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            return ParsePageTexts(json);
        }
        catch
        {
            return new List<string>();
        }
    }

    public static List<string> LoadPageTexts(string jsonPath)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            return ParsePageTexts(json);
        }
        catch
        {
            return new List<string>();
        }
    }

    public static List<string> ParsePageTexts(string json)
    {
        var texts = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!OcrResultJson.TryGetLayoutParsingResults(doc.RootElement, out var layouts))
                return texts;

            foreach (var pageResult in layouts.EnumerateArray())
            {
                if (!pageResult.TryGetProperty("prunedResult", out var pruned)) continue;

                if (pruned.TryGetProperty("spotting_res", out var spotting)
                    && spotting.TryGetProperty("rec_texts", out var recTexts)
                    && recTexts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in recTexts.EnumerateArray())
                    {
                        var str = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : t.ToString();
                        texts.Add(str);
                    }
                    continue;
                }

                if (pruned.TryGetProperty("parsing_res_list", out var parsingList)
                    && parsingList.ValueKind == JsonValueKind.Array)
                {
                    var items = parsingList.EnumerateArray()
                        .Select((item, idx) => new { Order = GetBlockOrder(item), Index = idx, Text = GetBlockText(item) })
                        .OrderBy(x => x.Order)
                        .ToList();
                    texts.AddRange(items.Select(x => x.Text));
                }
            }
        }
        catch { /* 解析失败跳过 */ }

        return texts;
    }

    private static int GetBlockOrder(JsonElement item)
    {
        if (item.TryGetProperty("block_order", out var bo) && bo.ValueKind == JsonValueKind.Number)
            return bo.GetInt32();
        return 999;
    }

    private static string GetBlockText(JsonElement item)
    {
        if (item.TryGetProperty("block_content", out var bc) && bc.ValueKind == JsonValueKind.String)
            return bc.GetString() ?? "";
        if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() ?? "";
        if (item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? "";
        return "";
    }
}
