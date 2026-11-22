using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Views.Views;

internal class HighlightRow
{
    public int PageNumber { get; set; }
    public string BlockLabel { get; set; } = "";
    public string HighlightText { get; set; } = "";
    public string Context { get; set; } = "";
    public int PageIndex { get; set; }
    public int BlockIndex { get; set; }
}

public partial class HighlightListDialog : Window
{
    private readonly string _jsonDir;
    private readonly Dictionary<int, List<HighlightEntry>> _docHighlights;
    private readonly ObservableCollection<HighlightRow> _rows = new();
    private readonly Action<int, int>? _onJumpRequested;

    public HighlightListDialog() : this("", new(), null) { /* design-time */ }

    public HighlightListDialog(string jsonDir, Dictionary<int, List<HighlightEntry>> docHighlights,
        Action<int, int>? onJumpRequested = null)
    {
        InitializeComponent();
        _jsonDir = jsonDir;
        _docHighlights = docHighlights;
        _onJumpRequested = onJumpRequested;

        HighlightsList.ItemsSource = _rows;

        JumpBtn.Click += OnJump;
        CloseBtn.Click += (_, _) => Close();
        HighlightsList.DoubleTapped += (_, _) => OnJump(null!, null!);

        _loadHighlights();
    }

    private void _loadHighlights()
    {
        const int contextChars = 10;

        foreach (var kv in _docHighlights.OrderBy(kv => kv.Key))
        {
            var pageIdx = kv.Key;
            var pageTexts = LoadPageTexts(pageIdx);

            foreach (var hl in kv.Value)
            {
                var hlText = hl.Text;
                var blockIdx = -1;
                var context = hlText;

                for (int i = 0; i < pageTexts.Count; i++)
                {
                    var text = pageTexts[i];
                    var pos = text.IndexOf(hlText, StringComparison.Ordinal);
                    if (pos != -1)
                    {
                        blockIdx = i;
                        var start = Math.Max(0, pos - contextChars);
                        var end = Math.Min(text.Length, pos + hlText.Length + contextChars);
                        context = text[start..end];
                        if (start > 0) context = "…" + context;
                        if (end < text.Length) context += "…";
                        break;
                    }
                }

                if (blockIdx == -1 && hlText.Length >= 2)
                {
                    for (int i = 0; i < pageTexts.Count; i++)
                    {
                        if (pageTexts[i].Contains(hlText[..2]))
                        {
                            blockIdx = i;
                            break;
                        }
                    }
                }

                var displayText = hlText.Length > 25 ? hlText[..25] + "…" : hlText;
                _rows.Add(new HighlightRow
                {
                    PageNumber = pageIdx + 1,
                    BlockLabel = blockIdx >= 0 ? $"[{blockIdx + 1}]" : "-",
                    HighlightText = displayText,
                    Context = context,
                    PageIndex = pageIdx,
                    BlockIndex = blockIdx,
                });
            }
        }

        HeaderLabel.Text = $"共 {_rows.Count} 条高亮标记，双击可跳转";
    }

    private List<string> LoadPageTexts(int pageIdx)
    {
        var texts = new List<string>();
        var jsonPath = System.IO.Path.Combine(_jsonDir, $"page_{pageIdx + 1}_ocr_result.json");
        if (!File.Exists(jsonPath)) return texts;

        try
        {
            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);

            if (!OcrResultJson.TryGetLayoutParsingResults(doc.RootElement, out var layouts))
                return texts;

            foreach (var pageResult in layouts.EnumerateArray())
            {
                if (!pageResult.TryGetProperty("prunedResult", out var pruned)) continue;

                // 拓片：优先按行 spotting_res，避免只用整页 parsing 汇总块
                if (pruned.TryGetProperty("spotting_res", out var spotting)
                    && spotting.TryGetProperty("rec_texts", out var recTexts)
                    && recTexts.ValueKind == JsonValueKind.Array
                    && recTexts.GetArrayLength() > 0)
                {
                    foreach (var t in recTexts.EnumerateArray())
                    {
                        var str = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : t.ToString();
                        if (!string.IsNullOrWhiteSpace(str))
                            texts.Add(str);
                    }
                    continue;
                }

                if (pruned.TryGetProperty("parsing_res_list", out var parsingList)
                    && parsingList.ValueKind == JsonValueKind.Array)
                {
                    var items = parsingList.EnumerateArray()
                        .Select((item, idx) => new { Order = GetBlockOrder(item), Text = GetBlockText(item) })
                        .OrderBy(x => x.Order)
                        .ToList();
                    texts.AddRange(items.Select(x => x.Text));
                }
            }
        }
        catch { }

        return texts;
    }

    private static int GetBlockOrder(JsonElement item)
    {
        if (item.TryGetProperty("block_order", out var bo) && bo.ValueKind == JsonValueKind.Number)
            return bo.GetInt32();
        return 999;
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

    private void OnJump(object? sender, RoutedEventArgs? e)
    {
        var selected = HighlightsList.SelectedItem as HighlightRow;
        if (selected == null || selected.BlockIndex < 0) return;

        _onJumpRequested?.Invoke(selected.PageIndex, selected.BlockIndex);
        // 双击不关闭对话框
    }
}
