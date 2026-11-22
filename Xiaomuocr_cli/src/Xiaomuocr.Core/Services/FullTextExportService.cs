using System.Text;
using System.Text.Json;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public class FullTextExportService : IFullTextExportService
{
    public async Task ExportAsync(
        string jsonDir,
        int totalPages,
        string documentName,
        string outputPath,
        FullTextExportOptions options,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>>? punctuateBodyAsync = null,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        int from = Math.Clamp(options.PageFrom, 1, Math.Max(1, totalPages));
        int to = Math.Clamp(options.PageTo, 1, Math.Max(1, totalPages));
        if (to < from) (from, to) = (to, from);

        var needPunctuate = options.ApplyPunctuate;
        if (needPunctuate && punctuateBodyAsync == null)
            throw new InvalidOperationException("句读导出需要提供句读回调");

        var sb = new StringBuilder();
        if (options.Format == FullTextExportFormat.Markdown)
        {
            sb.AppendLine($"# 《{documentName}》");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"《{documentName}》");
            sb.AppendLine();
        }

        int rangeCount = to - from + 1;
        int done = 0;

        for (int pageNum = from; pageNum <= to; pageNum++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jsonPath = Path.Combine(jsonDir, $"page_{pageNum}_ocr_result.json");
            done++;
            progress?.Report((done, rangeCount));

            if (!File.Exists(jsonPath))
                continue;

            var blocks = await LoadPageBlocksAsync(jsonPath).ConfigureAwait(false);
            if (blocks.Count == 0)
                continue;

            // 句读：仅正文块
            if (needPunctuate)
            {
                var bodyIdx = new List<int>();
                var bodyTexts = new List<string>();
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (IsBodyLabel(blocks[i].Label) && !string.IsNullOrWhiteSpace(blocks[i].Text))
                    {
                        bodyIdx.Add(i);
                        bodyTexts.Add(blocks[i].Text);
                    }
                }

                if (bodyTexts.Count > 0)
                {
                    var punctuated = await punctuateBodyAsync!(bodyTexts, cancellationToken)
                        .ConfigureAwait(false);
                    if (punctuated.Count != bodyTexts.Count)
                        throw new InvalidOperationException("句读返回块数与请求不一致");
                    for (int k = 0; k < bodyIdx.Count; k++)
                    {
                        var b = blocks[bodyIdx[k]];
                        blocks[bodyIdx[k]] = b with { Text = punctuated[k] ?? b.Text };
                    }
                }
            }

            // 繁简（与阅读器一致：句逗之后可选转简体）
            if (options.UseSimplified)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    var b = blocks[i];
                    blocks[i] = b with { Text = ChineseTextConverter.ToSimplified(b.Text ?? "") };
                }
            }

            AppendPage(sb, pageNum, blocks, options.Format);
        }

        // 去尾空行
        var text = sb.ToString().TrimEnd();
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputPath, text + Environment.NewLine, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AppendPage(
        StringBuilder sb,
        int pageNum,
        List<ExportBlock> blocks,
        FullTextExportFormat format)
    {
        if (format == FullTextExportFormat.Markdown)
        {
            sb.AppendLine($"## 第 {pageNum} 页");
            sb.AppendLine();
            foreach (var b in blocks)
            {
                var t = (b.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (IsTitleLabel(b.Label))
                    sb.AppendLine($"### {t}");
                else
                    sb.AppendLine(t);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine($"Page {pageNum} ------------------------------------------------------");
            var parts = new List<string>();
            foreach (var b in blocks)
            {
                var t = (b.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (IsTitleLabel(b.Label))
                    parts.Add($"### {t}");
                else
                    parts.Add(t);
            }
            sb.AppendLine(string.Join("\n\n", parts));
            sb.AppendLine();
        }
    }

    private static async Task<List<ExportBlock>> LoadPageBlocksAsync(string jsonPath)
    {
        var blocks = new List<ExportBlock>();
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            using var doc = JsonDocument.Parse(json);

            if (!OcrResultJson.TryGetLayoutParsingResults(doc.RootElement, out var layouts))
                return blocks;

            foreach (var pageResult in layouts.EnumerateArray())
            {
                if (!pageResult.TryGetProperty("prunedResult", out var pruned)) continue;

                if (pruned.TryGetProperty("spotting_res", out var spotting)
                    && spotting.TryGetProperty("rec_texts", out var recTexts)
                    && recTexts.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var t in recTexts.EnumerateArray())
                    {
                        var str = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : t.ToString();
                        if (!string.IsNullOrWhiteSpace(str))
                            blocks.Add(new ExportBlock(str.Trim(), "spotting_line", i));
                        i++;
                    }
                    continue;
                }

                if (pruned.TryGetProperty("parsing_res_list", out var parsingList)
                    && parsingList.ValueKind == JsonValueKind.Array)
                {
                    var items = parsingList.EnumerateArray()
                        .Select((item, index) => new ExportBlock(
                            GetBlockText(item).Trim(),
                            GetBlockLabel(item),
                            GetBlockOrder(item, index)))
                        .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                        .OrderBy(x => x.Order)
                        .ToList();
                    blocks.AddRange(items);
                }
            }
        }
        catch
        {
            /* 解析失败跳过该页 */
        }

        return blocks;
    }

    private static bool IsBodyLabel(string? label)
        => OcrBlockLabels.IsBodyLabel(label);

    private static bool IsTitleLabel(string? label)
        => OcrBlockLabels.IsTitleLabel(label);

    private static int GetBlockOrder(JsonElement item, int fallback)
    {
        if (item.TryGetProperty("block_order", out var bo) && bo.ValueKind == JsonValueKind.Number)
            return bo.GetInt32();
        return fallback;
    }

    private static string? GetBlockLabel(JsonElement item)
    {
        if (item.TryGetProperty("block_label", out var bl) && bl.ValueKind == JsonValueKind.String)
            return bl.GetString();
        return null;
    }

    private static string GetBlockText(JsonElement item)
    {
        if (item.TryGetProperty("block_content", out var bc) && bc.ValueKind == JsonValueKind.String)
            return bc.GetString() ?? "";
        if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() ?? "";
        if (item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? "";
        return "";
    }

    private readonly record struct ExportBlock(string Text, string? Label, int Order);
}
