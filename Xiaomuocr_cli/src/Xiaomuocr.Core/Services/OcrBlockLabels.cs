namespace Xiaomuocr.Core.Services;

/// <summary>
/// OCR 版面 block_label 分类。正文用黑名单：未知标签按正文处理，避免漏句读。
/// </summary>
public static class OcrBlockLabels
{
    /// <summary>明确非正文（标题/页眉页脚/图表表格等），句读与导出「句读后简体」时跳过。</summary>
    private static readonly HashSet<string> NonBodyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "paragraph_title",
        "doc_title",
        "title",
        "header",
        "footer",
        "footnote",
        "page_number",
        "number",
        "aside_text",
        "table",
        "table_caption",
        "figure",
        "figure_caption",
        "chart",
        "seal",
        "formula",
        "algorithm",
        "reference",
        "contents",
    };

    public static bool IsBodyLabel(string? label)
    {
        var l = (label ?? "").Trim();
        if (l.Length == 0)
            return true;
        return !NonBodyLabels.Contains(l);
    }

    public static bool IsTitleLabel(string? label)
        => string.Equals(label, "paragraph_title", StringComparison.OrdinalIgnoreCase)
           || string.Equals(label, "doc_title", StringComparison.OrdinalIgnoreCase)
           || string.Equals(label, "title", StringComparison.OrdinalIgnoreCase);
}
