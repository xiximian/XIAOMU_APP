using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// OCR 扣费确认文案：与后端「先套餐、不够再扣余额」一致。
/// </summary>
public static class OcrBillingConfirm
{
    public const string DefaultTitle = "批量 OCR 确认";
    public const string LibraryTitle = "确认全文识别";

    public static string BuildMessage(int pageCount, UserAssets assets, string? documentName = null)
    {
        var pkgRemaining = Math.Max(0, assets.PackageRemaining);
        var pkgUse = Math.Min(pageCount, pkgRemaining);
        var balPages = pageCount - pkgUse;
        var unit = assets.PaygUnitPrice;
        var balCost = balPages * unit;
        var available = assets.AvailableBalance;

        var header = string.IsNullOrEmpty(documentName)
            ? $"本次识别共 {pageCount} 页"
            : $"确定对「{documentName}」执行全文识别吗？\n\n将处理尚未识别的页面（共 {pageCount} 页）";

        string billing;
        if (pkgUse > 0 && balPages == 0)
        {
            billing = $"将消耗套餐 {pageCount} 次（当前剩余 {pkgRemaining} 次）";
        }
        else if (pkgUse == 0)
        {
            billing = unit > 0
                ? $"预计从余额扣除 ¥{balCost:F2}（可用 ¥{available:F2}，单价 ¥{unit:F2}/页）"
                : $"预计从余额扣除（可用 ¥{available:F2}）";
        }
        else
        {
            billing = $"将消耗套餐 {pkgUse} 次（当前剩余 {pkgRemaining} 次）；\n" +
                      $"不足 {balPages} 页预计再从余额扣除 ¥{balCost:F2}（可用 ¥{available:F2}）";
        }

        return $"{header}\n\n{billing}\n\n确认开始识别？";
    }

    public static string BuildFetchFailedMessage(int pageCount = 0, string? documentName = null)
    {
        string header;
        if (!string.IsNullOrEmpty(documentName))
        {
            header = pageCount > 0
                ? $"确定对「{documentName}」执行全文识别吗？\n\n将处理尚未识别的页面（共 {pageCount} 页）"
                : $"确定对「{documentName}」执行全文识别吗？";
        }
        else
        {
            header = pageCount > 0 ? $"本次识别共 {pageCount} 页" : "即将开始批量识别";
        }

        return $"{header}\n\n无法获取额度信息，是否仍继续？\n（识别时仍会按套餐优先、不足扣余额进行计费）";
    }

    /// <summary>兼容旧调用名。</summary>
    public static string AssetsUnavailableMessage(int pageCount = 0, string? documentName = null)
        => BuildFetchFailedMessage(pageCount, documentName);

    /// <summary>统计尚未有 OCR 结果的页数。</summary>
    public static int CountPendingPages(string? outputDir, int totalPages)
    {
        if (totalPages <= 0) return 0;
        if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            return totalPages;

        int pending = 0;
        for (int i = 0; i < totalPages; i++)
        {
            var jsonPath = Path.Combine(outputDir, $"page_{i + 1}_ocr_result.json");
            if (!File.Exists(jsonPath))
                pending++;
        }
        return pending;
    }
}
