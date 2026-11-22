using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public class SearchService : ISearchService
{
    private readonly ISearchIndexService? _indexService;

    public SearchService(ISearchIndexService? indexService = null)
    {
        _indexService = indexService;
    }

    public Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, string keyword, string? documentId = null)
    {
        var kw = (keyword ?? "").Trim();
        if (string.IsNullOrEmpty(kw))
            return Task.FromResult(new List<SearchResult>());
        return SearchAsync(jsonDir, totalPages, new SearchTermNode(kw), documentId);
    }

    public Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, SearchQuery query, string? documentId = null)
    {
        if (query == null || query.Clauses.Count == 0)
            return Task.FromResult(new List<SearchResult>());
        return SearchAsync(jsonDir, totalPages, query.ToNode(), documentId);
    }

    public async Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, SearchNode query, string? documentId = null)
    {
        if (string.IsNullOrEmpty(jsonDir) || query == null)
            return new List<SearchResult>();

        IReadOnlyList<IndexedTextBlock>? indexed = null;
        if (!string.IsNullOrWhiteSpace(documentId)
            && _indexService != null
            && _indexService.ShouldUseIndex(totalPages))
        {
            indexed = _indexService.TryLoadBlocks(documentId, jsonDir, totalPages);
        }

        if (indexed != null)
            return SearchBlocks(indexed, query);

        return await SearchFromJsonFilesAsync(jsonDir, totalPages, query).ConfigureAwait(false);
    }

    public List<SearchResult> RefineResults(
        IEnumerable<SearchResult> source,
        string jsonDir,
        string keyword,
        string? documentId = null,
        int totalPages = 0)
    {
        var list = new List<SearchResult>();
        var kw = (keyword ?? "").Trim();
        if (string.IsNullOrEmpty(kw) || source == null)
            return list;

        var variants = BuildKeywordVariants(kw);
        var kwSimplified = ChineseTextConverter.ToSimplified(kw);
        const int contextChars = 15;

        Dictionary<(int page, int block), string>? indexMap = null;
        var pageCache = new Dictionary<int, List<string>>();
        if (!string.IsNullOrWhiteSpace(documentId) && _indexService != null && totalPages > 0)
        {
            var blocks = _indexService.TryLoadBlocks(documentId, jsonDir, totalPages);
            if (blocks != null)
            {
                indexMap = blocks.ToDictionary(
                    b => (b.PageIndex, b.BlockIndex),
                    b => b.Text);
            }
        }

        foreach (var r in source)
        {
            string text;
            if (indexMap != null && indexMap.TryGetValue((r.PageIndex, r.BlockIndex), out var indexedText))
            {
                text = indexedText;
            }
            else
            {
                if (!pageCache.TryGetValue(r.PageIndex, out var pageBlocks))
                {
                    var jsonPath = Path.Combine(jsonDir ?? "", $"page_{r.PageIndex + 1}_ocr_result.json");
                    pageBlocks = File.Exists(jsonPath)
                        ? OcrJsonTextLoader.LoadPageTexts(jsonPath)
                        : new List<string>();
                    pageCache[r.PageIndex] = pageBlocks;
                }

                if (r.BlockIndex < 0 || r.BlockIndex >= pageBlocks.Count) continue;
                text = pageBlocks[r.BlockIndex] ?? "";
            }

            if (!TryFindMatches(text, variants, kwSimplified, out var firstPos, out var matchLen, out var matchCount))
                continue;

            var start = Math.Max(0, firstPos - contextChars);
            var end = Math.Min(text.Length, firstPos + Math.Max(matchLen, 1) + contextChars);
            var snippet = text[start..end];
            if (start > 0) snippet = "…" + snippet;
            if (end < text.Length) snippet += "…";

            list.Add(new SearchResult
            {
                PageIndex = r.PageIndex,
                BlockIndex = r.BlockIndex,
                PageNumber = r.PageNumber,
                BlockId = r.BlockId,
                Snippet = snippet,
                MatchCount = matchCount,
                MatchedText = matchLen > 0 && firstPos >= 0 && firstPos + matchLen <= text.Length
                    ? text.Substring(firstPos, matchLen)
                    : kw,
            });
        }

        return list;
    }

    private static List<SearchResult> SearchBlocks(IReadOnlyList<IndexedTextBlock> blocks, SearchNode query)
    {
        var results = new List<SearchResult>();
        var positiveKws = query.PositiveKeywords()
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var variantCache = new Dictionary<string, (List<string> Variants, string Simplified)>(StringComparer.Ordinal);
        foreach (var kw in positiveKws)
            variantCache[kw] = (BuildKeywordVariants(kw), ChineseTextConverter.ToSimplified(kw));

        const int contextChars = 15;

        foreach (var block in blocks)
        {
            var text = block.Text ?? "";
            if (string.IsNullOrEmpty(text)) continue;

            bool TermMatches(string keyword)
            {
                var k = (keyword ?? "").Trim();
                if (string.IsNullOrEmpty(k)) return false;
                if (!variantCache.TryGetValue(k, out var cached))
                {
                    cached = (BuildKeywordVariants(k), ChineseTextConverter.ToSimplified(k));
                    variantCache[k] = cached;
                }
                return TryFindMatches(text, cached.Variants, cached.Simplified, out _, out _, out _);
            }

            if (!query.Evaluate(TermMatches))
                continue;

            var firstPos = -1;
            var matchLen = 0;
            var matchCount = 1;
            var matchedText = "";
            foreach (var kw in positiveKws)
            {
                var cached = variantCache[kw];
                if (TryFindMatches(text, cached.Variants, cached.Simplified, out var pos, out var len, out var cnt))
                {
                    firstPos = pos;
                    matchLen = len;
                    matchCount = Math.Max(1, cnt);
                    matchedText = len > 0 && pos >= 0 && pos + len <= text.Length
                        ? text.Substring(pos, len)
                        : kw;
                    break;
                }
            }

            string snippet;
            if (firstPos >= 0)
            {
                var start = Math.Max(0, firstPos - contextChars);
                var end = Math.Min(text.Length, firstPos + Math.Max(matchLen, 1) + contextChars);
                snippet = text[start..end];
                if (start > 0) snippet = "…" + snippet;
                if (end < text.Length) snippet += "…";
            }
            else
            {
                var take = Math.Min(text.Length, contextChars * 2 + 8);
                snippet = take > 0 ? text[..take] + (text.Length > take ? "…" : "") : "";
                matchedText = "";
                matchCount = 1;
            }

            results.Add(new SearchResult
            {
                PageIndex = block.PageIndex,
                BlockIndex = block.BlockIndex,
                PageNumber = block.PageIndex + 1,
                BlockId = block.BlockIndex + 1,
                Snippet = snippet,
                MatchCount = matchCount,
                MatchedText = matchedText,
            });
        }

        return results;
    }

    private static async Task<List<SearchResult>> SearchFromJsonFilesAsync(
        string jsonDir, int totalPages, SearchNode query)
    {
        var results = new List<SearchResult>();
        var positiveKws = query.PositiveKeywords()
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var variantCache = new Dictionary<string, (List<string> Variants, string Simplified)>(StringComparer.Ordinal);
        foreach (var kw in positiveKws)
            variantCache[kw] = (BuildKeywordVariants(kw), ChineseTextConverter.ToSimplified(kw));

        const int contextChars = 15;

        for (int pageIdx = 0; pageIdx < totalPages; pageIdx++)
        {
            var jsonPath = Path.Combine(jsonDir, $"page_{pageIdx + 1}_ocr_result.json");
            if (!File.Exists(jsonPath)) continue;

            var textBlocks = await OcrJsonTextLoader.LoadPageTextsAsync(jsonPath).ConfigureAwait(false);
            for (int i = 0; i < textBlocks.Count; i++)
            {
                var text = textBlocks[i] ?? "";
                if (string.IsNullOrEmpty(text)) continue;

                bool TermMatches(string keyword)
                {
                    var k = (keyword ?? "").Trim();
                    if (string.IsNullOrEmpty(k)) return false;
                    if (!variantCache.TryGetValue(k, out var cached))
                    {
                        cached = (BuildKeywordVariants(k), ChineseTextConverter.ToSimplified(k));
                        variantCache[k] = cached;
                    }
                    return TryFindMatches(text, cached.Variants, cached.Simplified, out _, out _, out _);
                }

                if (!query.Evaluate(TermMatches))
                    continue;

                var firstPos = -1;
                var matchLen = 0;
                var matchCount = 1;
                var matchedText = "";
                foreach (var kw in positiveKws)
                {
                    var cached = variantCache[kw];
                    if (TryFindMatches(text, cached.Variants, cached.Simplified, out var pos, out var len, out var cnt))
                    {
                        firstPos = pos;
                        matchLen = len;
                        matchCount = Math.Max(1, cnt);
                        matchedText = len > 0 && pos >= 0 && pos + len <= text.Length
                            ? text.Substring(pos, len)
                            : kw;
                        break;
                    }
                }

                string snippet;
                if (firstPos >= 0)
                {
                    var start = Math.Max(0, firstPos - contextChars);
                    var end = Math.Min(text.Length, firstPos + Math.Max(matchLen, 1) + contextChars);
                    snippet = text[start..end];
                    if (start > 0) snippet = "…" + snippet;
                    if (end < text.Length) snippet += "…";
                }
                else
                {
                    var take = Math.Min(text.Length, contextChars * 2 + 8);
                    snippet = take > 0 ? text[..take] + (text.Length > take ? "…" : "") : "";
                    matchedText = "";
                    matchCount = 1;
                }

                results.Add(new SearchResult
                {
                    PageIndex = pageIdx,
                    BlockIndex = i,
                    PageNumber = pageIdx + 1,
                    BlockId = i + 1,
                    Snippet = snippet,
                    MatchCount = matchCount,
                    MatchedText = matchedText,
                });
            }
        }

        return results;
    }

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

        if (string.IsNullOrEmpty(keywordSimplified))
            return false;

        var textSimp = ChineseTextConverter.ToSimplified(text);
        int p2 = 0;
        while ((p2 = textSimp.IndexOf(keywordSimplified, p2, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            matchCount++;
            if (firstPos < 0)
            {
                firstPos = textSimp.Length == text.Length
                    ? p2
                    : Math.Min(p2, Math.Max(0, text.Length - keywordSimplified.Length));
                matchLen = Math.Min(keywordSimplified.Length, text.Length - firstPos);
            }
            p2 += keywordSimplified.Length;
        }

        return matchCount > 0;
    }
}
