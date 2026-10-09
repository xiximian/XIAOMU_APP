namespace Xiaomuocr.Core.Models;

/// <summary>单条高亮标记</summary>
public class HighlightEntry
{
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#FFEB3B"; // 默认暖黄
}

/// <summary>文本高亮分段：一段连续文字 + 可选背景色</summary>
public class HighlightSegment
{
    public string Text { get; set; } = "";
    public string? BgHex { get; set; } // null = 透明, "#FFEB3B" = 高亮黄
}

/// <summary>全文搜索结果</summary>
public class SearchResult
{
    public int PageIndex { get; set; }
    public int BlockIndex { get; set; }
    public int PageNumber { get; set; }       // 1-based display
    public int BlockId { get; set; }          // 1-based display
    public string Snippet { get; set; } = ""; // 上下文摘要（前后各15字）
    /// <summary>该文本块内命中次数（简繁变体去重按起始位置计）</summary>
    public int MatchCount { get; set; } = 1;
    /// <summary>实际命中的原文片段（便于导出）</summary>
    public string MatchedText { get; set; } = "";
}
