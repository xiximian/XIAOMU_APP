using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Views.Views;

public partial class SearchDialog : Window
{
    private readonly ISearchService _searchService;
    private readonly string _jsonDir;
    private readonly int _totalPages;
    private readonly string _documentName;
    private readonly ObservableCollection<SearchResult> _results = new();
    private readonly Action<int, int>? _onJumpRequested;
    private string _lastKeyword = "";

    public SearchDialog() : this(null!, "", 0, null) { /* design-time */ }

    public SearchDialog(ISearchService searchService, string jsonDir, int totalPages,
        Action<int, int>? onJumpRequested = null, string? documentName = null)
    {
        InitializeComponent();
        _searchService = searchService;
        _jsonDir = jsonDir;
        _totalPages = totalPages;
        _documentName = string.IsNullOrWhiteSpace(documentName) ? "未命名文献" : documentName.Trim();
        _onJumpRequested = onJumpRequested;

        ResultsList.ItemsSource = _results;
        ResetStats();

        SearchBtn.Click += OnSearch;
        ExportBtn.Click += OnExport;
        JumpBtn.Click += OnJump;
        CloseBtn.Click += (_, _) => Close();
        KeywordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnSearch(null!, null!);
        };
        ResultsList.DoubleTapped += (_, _) => OnJump(null!, null!);
    }

    private async void OnSearch(object? sender, RoutedEventArgs? e)
    {
        var keyword = KeywordBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword)) return;

        SearchBtn.IsEnabled = false;
        SearchBtn.Content = "搜索中...";
        ExportBtn.IsEnabled = false;
        StatusLabel.Text = "";
        ResetStats();

        try
        {
            var results = await _searchService.SearchAsync(_jsonDir, _totalPages, keyword);
            _results.Clear();
            foreach (var r in results)
                _results.Add(r);

            _lastKeyword = keyword;
            UpdateStats(keyword);
            ExportBtn.IsEnabled = _results.Count > 0;
            StatusLabel.Text = _results.Count > 0 ? "双击或点「跳转」定位到文本块" : "未找到匹配";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"搜索失败: {ex.Message}";
            ResetStats();
        }
        finally
        {
            SearchBtn.IsEnabled = true;
            SearchBtn.Content = "搜索";
        }
    }

    private void UpdateStats(string keyword)
    {
        var total = _results.Count;
        var hits = _results.Sum(r => Math.Max(1, r.MatchCount));
        var pages = _results.Select(r => r.PageNumber).Distinct().Count();

        StatsTotalLabel.Text = $"结果: {total} 条";
        StatsHitLabel.Text = $"命中: {hits} 次";
        StatsPageLabel.Text = $"页数: {pages} 页";
        StatsKeywordLabel.Text = $"关键词: 「{keyword}」(简繁互通)";
    }

    private void ResetStats()
    {
        StatsTotalLabel.Text = "结果: —";
        StatsHitLabel.Text = "命中: —";
        StatsPageLabel.Text = "页数: —";
        StatsKeywordLabel.Text = "";
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            StatusLabel.Text = "没有可导出的结果";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var safeName = string.Join("_", _documentName.Split(Path.GetInvalidFileNameChars()));
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出搜索结果",
            SuggestedFileName = $"{safeName}_搜索_{_lastKeyword}.txt",
            DefaultExtension = ".txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("CSV 文件") { Patterns = new[] { "*.csv" } },
            },
        });

        if (file == null) return;

        try
        {
            var path = file.Path.LocalPath;
            var isCsv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            var content = isCsv ? BuildCsvExport() : BuildTxtExport();
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusLabel.Text = $"已导出: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"导出失败: {ex.Message}";
        }
    }

    private string BuildTxtExport()
    {
        var total = _results.Count;
        var hits = _results.Sum(r => Math.Max(1, r.MatchCount));
        var pages = _results.Select(r => r.PageNumber).Distinct().Count();
        var sb = new StringBuilder();
        sb.AppendLine("小木史料阅读器 — 全文搜索结果");
        sb.AppendLine($"文献: {_documentName}");
        sb.AppendLine($"关键词: {_lastKeyword}（简繁互通）");
        sb.AppendLine($"统计: {total} 条结果，命中 {hits} 次，涉及 {pages} 页");
        sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 48));

        foreach (var r in _results)
        {
            sb.AppendLine(
                $"页码【{r.PageNumber}】块[{r.BlockId}] 命中{r.MatchCount}次：{r.Snippet}");
        }

        return sb.ToString();
    }

    private string BuildCsvExport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("页码,块号,命中次数,匹配文字,上下文");
        foreach (var r in _results)
        {
            sb.Append(r.PageNumber).Append(',')
              .Append(r.BlockId).Append(',')
              .Append(r.MatchCount).Append(',')
              .Append(CsvEscape(r.MatchedText)).Append(',')
              .Append(CsvEscape(r.Snippet))
              .AppendLine();
        }
        return sb.ToString();
    }

    private static string CsvEscape(string? value)
    {
        var s = value ?? "";
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private void OnJump(object? sender, RoutedEventArgs? e)
    {
        var selected = ResultsList.SelectedItem as SearchResult;
        if (selected == null) return;

        _onJumpRequested?.Invoke(selected.PageIndex, selected.BlockIndex);
    }
}
