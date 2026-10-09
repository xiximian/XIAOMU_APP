using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>全文搜索服务：遍历 OCR JSON 文件查找关键词。</summary>
public interface ISearchService
{
    /// <summary>
    /// 在指定文档目录下搜索关键词（简繁双向匹配）。
    /// </summary>
    /// <param name="jsonDir">OCR 结果目录</param>
    /// <param name="totalPages">文档总页数</param>
    /// <param name="keyword">搜索关键词</param>
    /// <returns>按页码排序的搜索结果列表</returns>
    Task<List<SearchResult>> SearchAsync(string jsonDir, int totalPages, string keyword);
}
