namespace Xiaomuocr.Core.Services;

/// <summary>大文献全文检索索引：每文献独立 SQLite，块级原文 + 简体。</summary>
public interface ISearchIndexService
{
    /// <summary>超过该页数时打开检索窗自动建索引（不含等于）。</summary>
    int PageThreshold { get; }

    bool ShouldUseIndex(int totalPages);

    /// <summary>索引是否已就绪且与当前 OCR 指纹一致。</summary>
    bool IsIndexReady(string documentId, string jsonDir, int totalPages);

    /// <summary>
    /// 确保索引可用：已就绪则跳过；否则扫描 OCR JSON 重建。
    /// progress: (已处理页数, 总页数, 状态文案)
    /// </summary>
    Task EnsureIndexAsync(
        string documentId,
        string jsonDir,
        int totalPages,
        IProgress<(int done, int total, string message)>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>枚举索引中的文本块；索引不可用时返回 null。</summary>
    IReadOnlyList<IndexedTextBlock>? TryLoadBlocks(string documentId, string jsonDir, int totalPages);
}

public sealed class IndexedTextBlock
{
    public int PageIndex { get; init; }
    public int BlockIndex { get; init; }
    public string Text { get; init; } = "";
}
