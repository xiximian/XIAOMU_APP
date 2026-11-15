namespace Xiaomuocr.Core.Models;

/// <summary>全文导出文件格式。</summary>
public enum FullTextExportFormat
{
    Txt = 0,
    Markdown = 1,
}

/// <summary>全文导出选项（由导出弹窗填写）。</summary>
public class FullTextExportOptions
{
    /// <summary>true=全部转简体；false=保留 OCR 原文脚本。</summary>
    public bool UseSimplified { get; set; }

    /// <summary>true=正文块先自动句逗（非正文不句逗）。</summary>
    public bool ApplyPunctuate { get; set; }

    public FullTextExportFormat Format { get; set; } = FullTextExportFormat.Txt;
    /// <summary>起始页（1-based，含）。</summary>
    public int PageFrom { get; set; } = 1;
    /// <summary>结束页（1-based，含）。</summary>
    public int PageTo { get; set; } = 1;

    public string FileExtension => Format == FullTextExportFormat.Markdown ? ".md" : ".txt";

    public string ContentModeFileSuffix => (ApplyPunctuate, UseSimplified) switch
    {
        (true, true) => "_句逗简体",
        (true, false) => "_句逗",
        (false, true) => "_简体",
        _ => "",
    };
}
