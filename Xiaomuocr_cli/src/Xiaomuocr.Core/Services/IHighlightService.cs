using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>高亮标记持久化服务：读写文档目录下的 highlights.json</summary>
public interface IHighlightService
{
    /// <summary>从 {jsonDir}/highlights.json 加载全部高亮。</summary>
    Task<Dictionary<int, List<HighlightEntry>>> LoadAsync(string jsonDir);

    /// <summary>将全部高亮写回 {jsonDir}/highlights.json。</summary>
    Task SaveAsync(string jsonDir, Dictionary<int, List<HighlightEntry>> data);

    /// <summary>添加一条高亮（自动去重）。</summary>
    void AddHighlight(Dictionary<int, List<HighlightEntry>> data, int pageIndex, string text, string color = "#FFEB3B");

    /// <summary>移除指定页的所有高亮。</summary>
    void RemovePageHighlights(Dictionary<int, List<HighlightEntry>> data, int pageIndex);
}
