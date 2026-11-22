using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>全文搜索服务：遍历 OCR JSON 或大文献索引查找关键词。</summary>
public interface ISearchService
{
    Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, string keyword, string? documentId = null);

    Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, SearchNode query, string? documentId = null);

    Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, SearchQuery query, string? documentId = null);

    /// <summary>
    /// 在已有结果上按关键词再次过滤（简繁匹配，不重扫全书）。
    /// </summary>
    List<SearchResult> RefineResults(
        IEnumerable<SearchResult> source,
        string jsonDir,
        string keyword,
        string? documentId = null,
        int totalPages = 0);
}
