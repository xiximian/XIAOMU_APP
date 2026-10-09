using System.Text.Json;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public class SearchService : ISearchService
{
    public async Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, string keyword)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(jsonDir) || string.IsNullOrWhiteSpace(keyword))
            return results;

        var kw = keyword.Trim();
        var variants = BuildKeywordVariants(kw);
        var kwSimplified = ChineseTextConverter.ToSimplified(kw);
        const int contextChars = 15;

        for (int pageIdx = 0; pageIdx < totalPages; pageIdx++)
        {
            var jsonPath = Path.Combine(jsonDir, $"page_{pageIdx + 1}_ocr_result.json");
            if (!File.Exists(jsonPath)) continue;

            var textBlocks = await LoadPageTextsAsync(jsonPath);
            for (int i = 0; i < textBlocks.Count; i++)
            {
                var text = textBlocks[i] ?? "";
                if (string.IsNullOrEmpty(text)) continue;

                if (!TryFindMatches(text, variants, kwSimplified, out var firstPos, out var matchLen, out var matchCount))
                    continue;

                var start = Math.Max(0, firstPos - contextChars);
                var end = Math.Min(text.Length, firstPos + Math.Max(matchLen, 1) + contextChars);
                var snippet = text[start..end];
                if (start > 0) snippet = "…" + snippet;
                if (end < text.Length) snippet += "…";

                results.Add(new SearchResult
                {
                    PageIndex = pageIdx,
                    BlockIndex = i,
                    PageNumber = pageIdx + 1,
                    BlockId = i + 1,
                    Snippet = snippet,
                    MatchCount = matchCount,
                    MatchedText = matchLen > 0 && firstPos >= 0 && firstPos + matchLen <= text.Length
                        ? text.Substring(firstPos, matchLen)
                        : kw,
                });
            }
        }

        return results;
    }

    /// <summary>原词 + 简体 + 繁体，去重。</summary>
    internal static List<string> BuildKeywordVariants(string keyword)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? s)
        {
            if (!string.IsNullOrEmpty(s))
                set.Add(s);
        }

        Add(keyword);
        Add(ChineseTextConverter.ToSimplified(keyword));
        Add(ChineseTextConverter.ToTraditional(keyword));
        return set.ToList();
    }

    /// <summary>
    /// 简繁双向匹配：先在原文上试各变体，再在「双方都转简体」的规范化文本上匹配。
    /// </summary>
    internal static bool TryFindMatches(
        string text,
        List<string> variants,
        string keywordSimplified,
        out int firstPos,
        out int matchLen,
        out int matchCount)
    {
        firstPos = -1;
        matchLen = 0;
        matchCount = 0;

        // 1) 原文直接匹配各变体（位置精确，用于摘要）
        var hitStarts = new SortedSet<int>();
        foreach (var kw in variants)
        {
            if (kw.Length == 0) continue;
            int p = 0;
            while ((p = text.IndexOf(kw, p, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                hitStarts.Add(p);
                if (firstPos < 0 || p < firstPos)
                {
                    firstPos = p;
                    matchLen = kw.Length;
                }
                p += kw.Length;
            }
        }

        if (hitStarts.Count > 0)
        {
            matchCount = hitStarts.Count;
            return true;
        }

        // 2) 规范化到简体后再比（覆盖「简搜繁 / 繁搜简」且变体未直接命中的情况）
        if (string.IsNullOrEmpty(keywordSimplified))
            return false;

        var textSimp = ChineseTextConverter.ToSimplified(text);
        int p2 = 0;
        while ((p2 = textSimp.IndexOf(keywordSimplified, p2, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            matchCount++;
            if (firstPos < 0)
            {
                // 长度一致时可直接映射；否则回退到该位置附近
                firstPos = textSimp.Length == text.Length
                    ? p2
                    : Math.Min(p2, Math.Max(0, text.Length - keywordSimplified.Length));
                matchLen = Math.Min(keywordSimplified.Length, text.Length - firstPos);
            }
            p2 += keywordSimplified.Length;
        }

        return matchCount > 0;
    }

    private static async Task<List<string>> LoadPageTextsAsync(string jsonPath)
    {
        var texts = new List<string>();
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
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
        catch { /* 解析失败跳过该页 */ }

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
