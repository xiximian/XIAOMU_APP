using ReactiveUI;

namespace Xiaomuocr.Core.ViewModels;

/// <summary>PDF 页码导航项（左侧列表）</summary>
public class PdfPageNavItem : ReactiveObject
{
    /// <summary>0-based 页索引</summary>
    public int PageIndex { get; init; }

    /// <summary>1-based 展示页码</summary>
    public int PageNumber { get; init; }

    private bool _hasOcr;
    /// <summary>是否已有 OCR 结果（存在 page_N_ocr_result.json）</summary>
    public bool HasOcr
    {
        get => _hasOcr;
        set => this.RaiseAndSetIfChanged(ref _hasOcr, value);
    }

    private bool _isCurrent;
    /// <summary>是否为当前阅读页</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
    }
}
