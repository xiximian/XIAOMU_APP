using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Text;
using System.Text.RegularExpressions;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Views.Views;

public partial class NoteDialog : Window
{
    private readonly INoteService _noteService;
    private readonly string _jsonDir;
    private readonly string _documentName;
    private bool _modified;
    private bool _isReading = true; // default: reading mode
    private int _selStart;
    private int _selLength;
    private List<OutlineEntry> _outlineEntries = new();
    private static readonly string _debugLogPath = System.IO.Path.Combine(
        System.AppContext.BaseDirectory, "note_debug.log");

    private record OutlineEntry(int LineIndex, int Level, string DisplayText);

    private IDisposable? _editSub;
    private CancellationTokenSource? _previewDebounce;

    private static void LogDebug(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { System.IO.File.AppendAllText(_debugLogPath, line + "\n"); } catch { }
    }

    public NoteDialog() : this(null!, "", "") { /* design-time */ }

    public NoteDialog(INoteService noteService, string jsonDir, string documentName)
    {
        InitializeComponent();
        _noteService = noteService;
        _jsonDir = jsonDir;
        _documentName = documentName;
        Title = $"笔记 - {documentName}";

        // Toolbar
        BoldBtn.Click += (_, _) => { LogDebug("[NOTE-BTN] Bold clicked"); ApplyInlineMarker("**"); };
        HighlightBtn.Click += (_, _) => { LogDebug("[NOTE-BTN] Highlight clicked"); ApplyInlineMarker("=="); };
        H1Btn.Click += (_, _) => ApplyHeadingMarker(1);
        H2Btn.Click += (_, _) => ApplyHeadingMarker(2);
        H3Btn.Click += (_, _) => ApplyHeadingMarker(3);
        SaveBtn.Click += (_, _) => SaveNotes(silent: false);
        ExportBtn.Click += (_, _) => ExportNotes();
        ToggleModeBtn.Click += (_, _) => ToggleMode();

        // Outline selection → jump to heading
        OutlineList.SelectionChanged += OnOutlineSelected;

        // Editor text change → live preview + outline
        _editSub = NoteEditor.GetObservable(TextBox.TextProperty)
            .Subscribe(_ =>
            {
                _modified = true;
#pragma warning disable CS4014
                DebouncedRenderPreview();
                RefreshOutline();
#pragma warning restore CS4014
            });

        // Cache selection on mouse release (survives focus loss when clicking toolbar buttons)
        NoteEditor.PointerReleased += (_, _) => CacheSelection();

        // Track selection property changes for debugging
        NoteEditor.GetObservable(TextBox.SelectionStartProperty).Subscribe(v =>
            LogDebug($"[NOTE-SELCHG] SelectionStart={v} SelectionEnd={NoteEditor.SelectionEnd} focused={NoteEditor.IsFocused}"));
        NoteEditor.GetObservable(TextBox.SelectionEndProperty).Subscribe(v =>
            LogDebug($"[NOTE-SELCHG] SelectionEnd={v} SelectionStart={NoteEditor.SelectionStart} focused={NoteEditor.IsFocused}"));

        // Keyboard
        KeyDown += OnWindowKeyDown;

        // Close
        Closing += OnClosing;

        _ = LoadNotesAsync();
    }

    // ===================================================================
    // LOAD / SAVE
    // ===================================================================

    private async Task LoadNotesAsync()
    {
        try
        {
            var text = await _noteService.LoadAsync(_jsonDir);
            NoteEditor.Text = text ?? "";
            _modified = false;
            RenderPreview();
            RefreshOutline();
            SetReadingMode(true);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"加载笔记失败: {ex.Message}";
        }
    }

    private async void SaveNotes(bool silent)
    {
        try
        {
            var text = NoteEditor.Text ?? "";
            await _noteService.SaveAsync(_jsonDir, text);
            _modified = false;
            StatusLabel.Text = silent ? "" : "笔记已保存 ✓";
            if (!silent)
            {
                await Task.Delay(2000);
                UpdateStatusLabel();
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"保存失败: {ex.Message}";
        }
    }

    // ===================================================================
    // MODE TOGGLE
    // ===================================================================

    private void ToggleMode()
    {
        _isReading = !_isReading;

        if (_isReading)
        {
            EditPanel.IsVisible = false;
            ReadPanel.IsVisible = true;
            ToggleModeBtn.Content = "编辑";
            ToggleModeBtn.Background = new SolidColorBrush(0xFF4A7C59);
            SetFormatButtonsEnabled(false);
            RenderPreview();
            UpdateStatusLabel();
        }
        else
        {
            ReadPanel.IsVisible = false;
            EditPanel.IsVisible = true;
            ToggleModeBtn.Content = "阅读";
            ToggleModeBtn.Background = new SolidColorBrush(0xFF9B722E);
            SetFormatButtonsEnabled(true);
            RenderPreview();
            NoteEditor.Focus();
            UpdateStatusLabel();
        }
    }

    private void SetReadingMode(bool reading)
    {
        _isReading = reading;
        EditPanel.IsVisible = !reading;
        ReadPanel.IsVisible = reading;
        ToggleModeBtn.Content = reading ? "编辑" : "阅读";
        ToggleModeBtn.Background = new SolidColorBrush(reading ? 0xFF4A7C59u : 0xFF9B722Eu);
        SetFormatButtonsEnabled(!reading);
        UpdateStatusLabel();
    }

    private void SetFormatButtonsEnabled(bool enabled)
    {
        BoldBtn.IsEnabled = enabled;
        HighlightBtn.IsEnabled = enabled;
        H1Btn.IsEnabled = enabled;
        H2Btn.IsEnabled = enabled;
        H3Btn.IsEnabled = enabled;
        var opacity = enabled ? 1.0 : 0.4;
        BoldBtn.Opacity = opacity;
        HighlightBtn.Opacity = opacity;
        H1Btn.Opacity = opacity;
        H2Btn.Opacity = opacity;
        H3Btn.Opacity = opacity;
    }

    private void UpdateStatusLabel()
    {
        StatusLabel.Text = _isReading
            ? "阅读模式  |  点击「编辑」开始编辑  |  Ctrl+S 保存"
            : "编辑模式 — 左侧写笔记，右侧看效果  |  Ctrl+S 保存  |  Ctrl+B 加粗  |  Ctrl+H 高亮  |  Ctrl+P 切换";
    }

    // ===================================================================
    // PREVIEW RENDER
    // ===================================================================

    private void RenderPreview()
    {
        try
        {
            var markdown = NoteEditor.Text ?? "";
            RenderMarkdownToTextBlock(ReadBlock, markdown);
            RenderMarkdownToTextBlock(EditPreviewBlock, markdown);
        }
        catch (Exception ex)
        {
            // Don't crash on bad Markdown
            LogDebug($"[NoteDialog] RenderPreview error: {ex.Message}");
        }
    }

    private async Task DebouncedRenderPreview()
    {
        _previewDebounce?.Cancel();
        var tcs = new CancellationTokenSource();
        _previewDebounce = tcs;
        try
        {
            await Task.Delay(400, tcs.Token);
            RenderPreview();
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>将 Markdown 文本解析渲染到 TextBlock.Inlines。</summary>
    private static void RenderMarkdownToTextBlock(TextBlock tb, string markdown)
    {
        tb.Inlines!.Clear();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            tb.Inlines.Add(new Run("（暂无内容）")
            {
                Foreground = new SolidColorBrush(0xFF8B8370),
                FontStyle = FontStyle.Italic,
            });
            return;
        }

        var lines = markdown.Split('\n');
        bool firstBlock = true;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Skip empty lines (they separate blocks via the firstBlock gap)
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Gap before each block
            if (!firstBlock)
                tb.Inlines.Add(new LineBreak());

            // Heading
            var hMatch = Regex.Match(trimmed, @"^(#{1,3})\s+(.+)");
            if (hMatch.Success)
            {
                var level = hMatch.Groups[1].Length;
                var titleText = hMatch.Groups[2].Value;
                double size = level switch { 1 => 20, 2 => 16, _ => 14 };
                uint fg = level switch { 1 => 0xFF1A1A1A, 2 => 0xFF333333, _ => 0xFF555555 };

                tb.Inlines.Add(new Run(titleText)
                {
                    FontSize = size,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(fg),
                });
                firstBlock = false;
                continue;
            }

            // Paragraph
            RenderInlineLine(tb, trimmed);
            firstBlock = false;
        }
    }

    /// <summary>渲染一行中的 **加粗** 和 ==高亮==。</summary>
    private static void RenderInlineLine(TextBlock tb, string text)
    {
        const string pattern = @"(\*\*(.+?)\*\*)|(==(.+?)==)";
        MatchCollection matches;
        try { matches = Regex.Matches(text, pattern); }
        catch (Exception)
        {
            tb.Inlines.Add(new Run(text));
            tb.Inlines.Add(new LineBreak());
            return;
        }
        int cursor = 0;

        foreach (Match m in matches)
        {
            if (m.Index > cursor)
                tb.Inlines.Add(new Run(text[cursor..m.Index]));

            if (m.Groups[1].Success) // **bold**
            {
                tb.Inlines.Add(new Run(m.Groups[2].Value) { FontWeight = FontWeight.Bold });
            }
            else if (m.Groups[3].Success) // ==highlight==
            {
                tb.Inlines.Add(new Run(m.Groups[4].Value)
                {
                    Background = new SolidColorBrush(0xFFFFEB3B),
                });
            }
            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
            tb.Inlines.Add(new Run(text[cursor..]));
    }

    // ===================================================================
    // OUTLINE
    // ===================================================================

    private void RefreshOutline()
    {
        _outlineEntries.Clear();
        OutlineList.ItemsSource = null;

        var text = NoteEditor.Text ?? "";
        var lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], @"^(#{1,3})\s+(.+)");
            if (!match.Success) continue;

            var level = match.Groups[1].Length;
            var title = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var indent = new string(' ', (level - 1) * 4);
            _outlineEntries.Add(new OutlineEntry(i, level, $"{indent}{title}"));
        }

        OutlineList.ItemsSource = _outlineEntries.Select(o => o.DisplayText).ToList();
    }

    private void OnOutlineSelected(object? sender, SelectionChangedEventArgs e)
    {
        var idx = OutlineList.SelectedIndex;
        if (idx < 0 || idx >= _outlineEntries.Count) return;

        var entry = _outlineEntries[idx];
        GoToLine(entry.LineIndex);
    }

    private void GoToLine(int lineIndex)
    {
        // In reading mode, switch to edit first
        if (_isReading)
            ToggleMode();

        var text = NoteEditor.Text ?? "";
        var lines = text.Split('\n');
        var charOffset = 0;
        for (int i = 0; i < Math.Min(lineIndex, lines.Length); i++)
            charOffset += lines[i].Length + 1; // +1 for '\n'

        NoteEditor.CaretIndex = charOffset;
        NoteEditor.Focus();

        // Scroll to caret
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            NoteEditor.CaretIndex = charOffset;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // ===================================================================
    // MARKDOWN FORMATTING (toolbar)
    // ===================================================================

    /// <summary>Normalize selection: Avalonia may have Start > End (leftward drag).</summary>
    private static (int start, int length) NormalizeSelection(int s1, int s2)
    {
        var start = Math.Min(s1, s2);
        var end = Math.Max(s1, s2);
        return (start, end - start);
    }

    private void CacheSelection()
    {
        (_selStart, _selLength) = NormalizeSelection(NoteEditor.SelectionStart, NoteEditor.SelectionEnd);
        var text = NoteEditor.Text ?? "";
        var selText = _selLength > 0 && _selStart + _selLength <= text.Length
            ? text.Substring(_selStart, _selLength) : "";
        LogDebug($"[NOTE-SEL] CacheSelection: selStart={NoteEditor.SelectionStart} selEnd={NoteEditor.SelectionEnd} normalized=({_selStart},{_selLength}) selText='{selText}'");
    }

    private void ApplyInlineMarker(string marker)
    {
        var editor = NoteEditor;
        var text = editor.Text ?? "";

        // Use cached selection first, fall back to live (normalized) properties
        int start, length;
        if (_selLength > 0)
        {
            start = _selStart;
            length = _selLength;
        }
        else
        {
            (start, length) = NormalizeSelection(editor.SelectionStart, editor.SelectionEnd);
        }

        LogDebug($"[NOTE-FMT] marker={marker} live=({editor.SelectionStart},{editor.SelectionEnd}) final=({start},{length}) focused={editor.IsFocused}");

        if (length > 0 && start + length <= text.Length)
        {
            var selected = text.Substring(start, length);
            LogDebug($"[NOTE-FMT] wrapping selected text: '{selected}'");
            if (selected.StartsWith(marker) && selected.EndsWith(marker))
            {
                var inner = selected[marker.Length..^marker.Length];
                editor.Text = text[..start] + inner + text[(start + length)..];
                editor.SelectionStart = start;
                editor.SelectionEnd = start + inner.Length;
            }
            else
            {
                editor.Text = text[..start] + marker + selected + marker + text[(start + length)..];
                editor.SelectionStart = start;
                editor.SelectionEnd = start + length + marker.Length * 2;
            }
        }
        else
        {
            LogDebug($"[NOTE-FMT] NO SELECTION - inserting placeholder");
            var placeholder = marker == "==" ? "高亮文字" : "加粗文字";
            var insertion = $"{marker}{placeholder}{marker}";
            var caret = editor.CaretIndex;
            editor.Text = text.Insert(caret, insertion);
            editor.SelectionStart = caret + marker.Length;
            editor.SelectionEnd = caret + marker.Length + placeholder.Length;
        }

        _selLength = 0; // clear cache after use
        editor.Focus();
        _modified = true;
    }

    private void ApplyHeadingMarker(int level)
    {
        var editor = NoteEditor;
        var prefix = new string('#', level) + " ";
        var text = editor.Text ?? "";
        var caret = editor.CaretIndex;

        var lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd == -1) lineEnd = text.Length;
        var currentLine = text[lineStart..lineEnd];

        var match = Regex.Match(currentLine, @"^(#{1,3})\s+");
        if (match.Success && match.Groups[1].Length == level)
        {
            // Remove heading marker
            var newLine = currentLine[match.Length..];
            editor.Text = text[..lineStart] + newLine + text[lineEnd..];
            editor.CaretIndex = lineStart + newLine.Length;
        }
        else
        {
            var clean = Regex.Replace(currentLine, @"^(#{1,3})\s+", "");
            var newLine = prefix + clean;
            editor.Text = text[..lineStart] + newLine + text[lineEnd..];
            editor.CaretIndex = lineStart + newLine.Length;
        }

        editor.Focus();
        _modified = true;
    }

    // ===================================================================
    // EXPORT
    // ===================================================================

    private async void ExportNotes()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出笔记",
            SuggestedFileName = $"{_documentName}_笔记.html",
            DefaultExtension = ".html",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("HTML 文件") { Patterns = new[] { "*.html" } },
                new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } },
            },
        });

        if (file == null) return;

        try
        {
            var text = NoteEditor.Text ?? "";
            var isHtml = file.Path.LocalPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

            var content = isHtml ? MarkdownToHtml(text) : StripMarkdown(text);
            await File.WriteAllTextAsync(file.Path.LocalPath, content);
            StatusLabel.Text = $"已导出到: {System.IO.Path.GetFileName(file.Path.LocalPath)} ✓";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"导出失败: {ex.Message}";
        }
    }

    // ===================================================================
    // KEYBOARD
    // ===================================================================

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.S:
                    e.Handled = true;
                    SaveNotes(silent: true);
                    break;
                case Key.B:
                    e.Handled = true;
                    LogDebug($"[NOTE-KEY] Ctrl+B: selStart={NoteEditor.SelectionStart} selEnd={NoteEditor.SelectionEnd} cached=({_selStart},{_selLength}) focused={NoteEditor.IsFocused}");
                    ApplyInlineMarker("**");
                    break;
                case Key.H:
                    e.Handled = true;
                    LogDebug($"[NOTE-KEY] Ctrl+H: selStart={NoteEditor.SelectionStart} selEnd={NoteEditor.SelectionEnd} cached=({_selStart},{_selLength}) focused={NoteEditor.IsFocused}");
                    ApplyInlineMarker("==");
                    break;
                case Key.P:
                    e.Handled = true;
                    ToggleMode();
                    break;
            }
        }
    }

    // ===================================================================
    // CLOSE
    // ===================================================================

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_modified) return;

        e.Cancel = true;
        var result = await MessageBoxUtil.ShowAsync(
            this, "笔记有未保存的修改，是否保存后关闭？", "确认",
            MessageBoxUtil.Buttons.YesNoCancel);

        switch (result)
        {
            case MessageBoxUtil.Result.Yes:
                SaveNotes(silent: true);
                _modified = false;
                Close();
                break;
            case MessageBoxUtil.Result.No:
                _modified = false;
                Close();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _editSub?.Dispose();
        _previewDebounce?.Cancel();
        _previewDebounce?.Dispose();
        base.OnClosed(e);
    }

    // ===================================================================
    // MARKDOWN STRIPPING (TXT export)
    // ===================================================================

    /// <summary>去除所有 Markdown 标记，保留纯文本。</summary>
    private static string StripMarkdown(string md)
    {
        var sb = new StringBuilder();
        foreach (var line in md.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                sb.AppendLine();
                continue;
            }

            // Strip heading markers
            var hMatch = Regex.Match(trimmed, @"^(#{1,3})\s+(.+)");
            if (hMatch.Success)
                trimmed = hMatch.Groups[2].Value;

            // Strip bold **text**
            trimmed = Regex.Replace(trimmed, @"\*\*(.+?)\*\*", "$1");
            // Strip highlight ==text==
            trimmed = Regex.Replace(trimmed, @"==(.+?)==", "$1");

            sb.AppendLine(trimmed);
        }
        return sb.ToString().TrimEnd('\n', '\r');
    }

    // ===================================================================
    // MARKDOWN → HTML (export)
    // ===================================================================

    private string MarkdownToHtml(string md)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>笔记 - {EscapeHtml(_documentName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:\"Microsoft YaHei\",sans-serif;max-width:800px;margin:40px auto;line-height:1.8;color:#1a1a1a}");
        sb.AppendLine("h1{font-size:1.6em;border-bottom:2px solid #333;padding-bottom:6px;margin-top:24px}");
        sb.AppendLine("h2{font-size:1.3em;border-bottom:1px solid #999;padding-bottom:4px;margin-top:20px}");
        sb.AppendLine("h3{font-size:1.1em;margin-top:16px}");
        sb.AppendLine("p{margin:8px 0;text-indent:2em}");
        sb.AppendLine(".hl{background:#FFEB3B;padding:0 2px}");
        sb.AppendLine("</style></head><body>");

        foreach (var line in md.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var hm = Regex.Match(trimmed, @"^(#{1,3})\s+(.+)");
            if (hm.Success)
            {
                var lv = hm.Groups[1].Length;
                var tx = InlineToHtml(hm.Groups[2].Value);
                sb.AppendLine($"<h{lv}>{tx}</h{lv}>");
                continue;
            }

            sb.AppendLine($"<p>{InlineToHtml(trimmed)}</p>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string InlineToHtml(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
        text = Regex.Replace(text, @"==(.+?)==", "<span class=\"hl\">$1</span>");
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>简易消息框工具。</summary>
internal static class MessageBoxUtil
{
    public enum Buttons { OK, YesNoCancel }
    public enum Result { OK, Yes, No, Cancel }

    public static async Task<Result> ShowAsync(Window owner, string message, string title, Buttons buttons)
    {
        var tcs = new TaskCompletionSource<Result>();
        var dlg = new Window
        {
            Title = title, Width = 420, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false, ShowInTaskbar = false,
        };
        var grid = new Grid { RowDefinitions = new("*,Auto"), Margin = new Thickness(16) };
        grid.Children.Add(new TextBlock
        {
            Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        var btns = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(btns, 1);

        if (buttons == Buttons.YesNoCancel)
        {
            void B(string text, uint bg, Result r)
            {
                var btn = new Button { Content = text, Padding = new Thickness(12, 6),
                    Background = new SolidColorBrush(bg),
                    Foreground = bg == 0xFFFFFFFF ? Brushes.Black : Brushes.White,
                    CornerRadius = new CornerRadius(4) };
                btn.Click += (_, _) => { tcs.TrySetResult(r); dlg.Close(); };
                btns.Children.Add(btn);
            }
            B("保存并关闭", 0xFF059669, Result.Yes);
            B("不保存", 0xFFFFFFFF, Result.No);
            B("取消", 0xFFFFFFFF, Result.Cancel);
        }

        grid.Children.Add(btns);
        dlg.Content = grid;
        dlg.Closed += (_, _) => tcs.TrySetResult(Result.Cancel);
        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }
}
