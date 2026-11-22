using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Views.Views;

public partial class SearchDialog : Window
{
    private const int MaxAdvancedRows = 8;

    private readonly ISearchService _searchService;
    private readonly ISearchIndexService? _indexService;
    private readonly string _jsonDir;
    private readonly int _totalPages;
    private readonly string _documentName;
    private readonly string? _documentId;
    private readonly ObservableCollection<SearchResult> _results = new();
    private readonly Action<int, int>? _onJumpRequested;
    private readonly List<AdvancedRow> _advancedRows = new();
    private readonly CancellationTokenSource _indexCts = new();

    private string _lastQueryDesc = "";
    private List<SearchResult> _lastResults = new();
    private bool _indexBuilding;

    private sealed class AdvancedRow
    {
        public StackPanel Panel { get; init; } = null!;
        public ComboBox? OpCombo { get; init; }
        public TextBox KeywordBox { get; init; } = null!;
        public Button? RemoveBtn { get; init; }
    }

    public SearchDialog() : this(null!, "", 0, null) { /* design-time */ }

    public SearchDialog(
        ISearchService searchService,
        string jsonDir,
        int totalPages,
        Action<int, int>? onJumpRequested = null,
        string? documentName = null,
        string? documentId = null,
        ISearchIndexService? indexService = null)
    {
        InitializeComponent();
        _searchService = searchService;
        _indexService = indexService;
        _jsonDir = jsonDir;
        _totalPages = totalPages;
        _documentName = string.IsNullOrWhiteSpace(documentName) ? "未命名文献" : documentName.Trim();
        _documentId = string.IsNullOrWhiteSpace(documentId) ? null : documentId.Trim();
        _onJumpRequested = onJumpRequested;

        ResultsList.ItemsSource = _results;
        ResetStats();
        InitAdvancedRows();

        SearchBtn.Click += OnBasicSearch;
        RefineBtn.Click += OnRefine;
        AdvancedSearchBtn.Click += OnAdvancedSearch;
        ExprSearchBtn.Click += OnExpressionSearch;
        AddClauseBtn.Click += (_, _) => AddAdvancedRow();
        ExportBtn.Click += OnExport;
        JumpBtn.Click += OnJump;
        CloseBtn.Click += (_, _) => Close();

        KeywordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnBasicSearch(null!, null!);
        };
        RefineBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnRefine(null!, null!);
        };
        ResultsList.DoubleTapped += (_, _) => OnJump(null!, null!);

        Closed += (_, _) =>
        {
            try { _indexCts.Cancel(); } catch { /* ignore */ }
            _indexCts.Dispose();
        };

        Opened += async (_, _) => await EnsureIndexIfNeededAsync();
    }

    private async Task EnsureIndexIfNeededAsync()
    {
        if (_indexService == null || string.IsNullOrEmpty(_documentId))
            return;
        if (!_indexService.ShouldUseIndex(_totalPages))
            return;

        if (_indexService.IsIndexReady(_documentId, _jsonDir, _totalPages))
        {
            StatusLabel.Text = "大文献检索索引已就绪";
            return;
        }

        _indexBuilding = true;
        SetSearchButtonsEnabled(false);
        StatusLabel.Text = "全文超过 1000 页，正在建立检索索引…";

        var progress = new Progress<(int done, int total, string message)>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusLabel.Text = p.message;
            });
        });

        try
        {
            await _indexService.EnsureIndexAsync(
                _documentId, _jsonDir, _totalPages, progress, _indexCts.Token);
            StatusLabel.Text = "检索索引已就绪，可以搜索";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "索引建立已取消";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"索引建立失败，将使用慢速扫描: {ex.Message}";
        }
        finally
        {
            _indexBuilding = false;
            SetSearchButtonsEnabled(true);
        }
    }

    private void SetSearchButtonsEnabled(bool enabled)
    {
        SearchBtn.IsEnabled = enabled;
        AdvancedSearchBtn.IsEnabled = enabled;
        ExprSearchBtn.IsEnabled = enabled;
        if (!enabled)
        {
            RefineBtn.IsEnabled = false;
            RefineBox.IsEnabled = false;
        }
        else
        {
            UpdateRefineEnabled();
        }
    }

    private void InitAdvancedRows()
    {
        AdvancedRowsPanel.Children.Clear();
        _advancedRows.Clear();
        AddAdvancedRow();
        AddAdvancedRow();
    }

    private void AddAdvancedRow()
    {
        if (_advancedRows.Count >= MaxAdvancedRows)
        {
            StatusLabel.Text = $"最多 {MaxAdvancedRows} 个条件";
            return;
        }

        var isFirst = _advancedRows.Count == 0;
        ComboBox? opCombo = null;
        if (!isFirst)
        {
            opCombo = new ComboBox
            {
                Width = 72,
                FontSize = 12,
                SelectedIndex = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            opCombo.Items.Add("AND");
            opCombo.Items.Add("OR");
            opCombo.Items.Add("NOT");
            opCombo.SelectedIndex = 0;
        }

        var kwBox = new TextBox
        {
            Width = 260,
            FontSize = 13,
            Watermark = "关键词",
            VerticalAlignment = VerticalAlignment.Center,
        };

        Button? removeBtn = null;
        if (!isFirst)
        {
            removeBtn = new Button
            {
                Content = "删除",
                Padding = new Avalonia.Thickness(8, 4),
                FontSize = 12,
                Background = Brush.Parse("#E8E0D4"),
                CornerRadius = new Avalonia.CornerRadius(4),
            };
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        if (isFirst)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "包含:",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 72,
            });
        }
        else if (opCombo != null)
        {
            panel.Children.Add(opCombo);
        }

        panel.Children.Add(kwBox);
        if (removeBtn != null)
            panel.Children.Add(removeBtn);

        var row = new AdvancedRow
        {
            Panel = panel,
            OpCombo = opCombo,
            KeywordBox = kwBox,
            RemoveBtn = removeBtn,
        };

        if (removeBtn != null)
            removeBtn.Click += (_, _) => RemoveAdvancedRow(row);

        _advancedRows.Add(row);
        AdvancedRowsPanel.Children.Add(panel);
        AddClauseBtn.IsEnabled = _advancedRows.Count < MaxAdvancedRows;
    }

    private void RemoveAdvancedRow(AdvancedRow row)
    {
        if (_advancedRows.Count <= 1) return;
        if (_advancedRows.IndexOf(row) == 0) return;

        AdvancedRowsPanel.Children.Remove(row.Panel);
        _advancedRows.Remove(row);
        AddClauseBtn.IsEnabled = _advancedRows.Count < MaxAdvancedRows;
    }

    private SearchQuery BuildAdvancedQuery()
    {
        var q = new SearchQuery();
        for (var i = 0; i < _advancedRows.Count; i++)
        {
            var row = _advancedRows[i];
            var kw = row.KeywordBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(kw)) continue;

            var op = SearchBoolOp.And;
            if (i > 0 && row.OpCombo != null)
            {
                op = row.OpCombo.SelectedIndex switch
                {
                    1 => SearchBoolOp.Or,
                    2 => SearchBoolOp.Not,
                    _ => SearchBoolOp.And,
                };
            }

            q.Clauses.Add(new SearchClause { Keyword = kw, Op = op });
        }
        return q;
    }

    private bool GuardIndexReady()
    {
        if (_indexBuilding)
        {
            StatusLabel.Text = "正在建立检索索引，请稍候…";
            return false;
        }
        return true;
    }

    private async void OnBasicSearch(object? sender, RoutedEventArgs? e)
    {
        if (!GuardIndexReady()) return;
        var keyword = KeywordBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            StatusLabel.Text = "请输入关键词";
            return;
        }

        await RunSearchAsync(
            () => _searchService.SearchAsync(_jsonDir, _totalPages, keyword, _documentId),
            keyword,
            SearchBtn);
    }

    private async void OnRefine(object? sender, RoutedEventArgs? e)
    {
        if (!GuardIndexReady()) return;
        var keyword = RefineBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            StatusLabel.Text = "请输入再次检索关键词";
            return;
        }

        if (_lastResults.Count == 0)
        {
            StatusLabel.Text = "请先搜索";
            return;
        }

        RefineBtn.IsEnabled = false;
        RefineBtn.Content = "检索中...";
        ExportBtn.IsEnabled = false;
        StatusLabel.Text = "";

        try
        {
            var docId = _documentId;
            var pages = _totalPages;
            var results = await Task.Run(() =>
                _searchService.RefineResults(_lastResults, _jsonDir, keyword, docId, pages));

            ApplyResults(results, $"{_lastQueryDesc} → 再次「{keyword}」");
            StatusLabel.Text = _results.Count > 0 ? "双击或点「跳转」定位到文本块" : "再次检索无匹配";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"再次检索失败: {ex.Message}";
            ResetStats();
        }
        finally
        {
            RefineBtn.Content = "再次检索";
            UpdateRefineEnabled();
        }
    }

    private async void OnAdvancedSearch(object? sender, RoutedEventArgs? e)
    {
        if (!GuardIndexReady()) return;
        var query = BuildAdvancedQuery();
        if (query.Clauses.Count == 0)
        {
            StatusLabel.Text = "请至少填写一个关键词";
            return;
        }

        await RunSearchAsync(
            () => _searchService.SearchAsync(_jsonDir, _totalPages, query, _documentId),
            query.Describe(),
            AdvancedSearchBtn);
    }

    private async void OnExpressionSearch(object? sender, RoutedEventArgs? e)
    {
        if (!GuardIndexReady()) return;
        var expr = ExpressionBox.Text?.Trim();
        if (!BooleanQueryParser.TryParse(expr, out var root, out var error) || root == null)
        {
            StatusLabel.Text = error ?? "检索式无效";
            return;
        }

        await RunSearchAsync(
            () => _searchService.SearchAsync(_jsonDir, _totalPages, root, _documentId),
            expr!,
            ExprSearchBtn);
    }

    private async Task RunSearchAsync(Func<Task<List<SearchResult>>> search, string queryDesc, Button btn)
    {
        var prevContent = btn.Content;
        btn.IsEnabled = false;
        btn.Content = "搜索中...";
        ExportBtn.IsEnabled = false;
        StatusLabel.Text = "";
        ResetStats();

        try
        {
            var results = await search();
            ApplyResults(results, queryDesc);
            StatusLabel.Text = _results.Count > 0 ? "双击或点「跳转」定位到文本块" : "未找到匹配";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"搜索失败: {ex.Message}";
            ResetStats();
            _lastResults = new List<SearchResult>();
            UpdateRefineEnabled();
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = prevContent;
        }
    }

    private void ApplyResults(List<SearchResult> results, string queryDesc)
    {
        _results.Clear();
        foreach (var r in results)
            _results.Add(r);

        _lastResults = results.ToList();
        _lastQueryDesc = queryDesc;
        UpdateStats(queryDesc);
        ExportBtn.IsEnabled = _results.Count > 0;
        UpdateRefineEnabled();
    }

    private void UpdateRefineEnabled()
    {
        var ok = !_indexBuilding && _lastResults.Count > 0;
        RefineBox.IsEnabled = ok;
        RefineBtn.IsEnabled = ok;
    }

    private void UpdateStats(string queryDesc)
    {
        var total = _results.Count;
        var hits = _results.Sum(r => Math.Max(1, r.MatchCount));
        var pages = _results.Select(r => r.PageNumber).Distinct().Count();

        StatsTotalLabel.Text = $"结果: {total} 条";
        StatsHitLabel.Text = $"命中: {hits} 次";
        StatsPageLabel.Text = $"页数: {pages} 页";
        StatsKeywordLabel.Text = $"查询: {queryDesc}（简繁互通）";
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
        var safeQuery = string.Join("_", _lastQueryDesc.Split(Path.GetInvalidFileNameChars()));
        if (safeQuery.Length > 40) safeQuery = safeQuery[..40];

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出搜索结果",
            SuggestedFileName = $"{safeName}_搜索_{safeQuery}.txt",
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
        sb.AppendLine($"查询: {_lastQueryDesc}（简繁互通）");
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
