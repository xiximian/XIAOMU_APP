using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>全文导出服务：将单篇文献的 OCR 文本按选项导出。</summary>
public interface IFullTextExportService
{
    /// <summary>
    /// 导出全文。
    /// </summary>
    /// <param name="punctuateBodyAsync">
    /// 句读模式：对一页正文块文本批量句读，返回与输入等长的结果列表；非句读模式可传 null。
    /// </param>
    /// <param name="progress">进度回调 (currentPage1Based, totalInRange)。</param>
    Task ExportAsync(
        string jsonDir,
        int totalPages,
        string documentName,
        string outputPath,
        FullTextExportOptions options,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>>? punctuateBodyAsync = null,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken cancellationToken = default);
}
