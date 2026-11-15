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

    /// <summary>点击笔记内 xm-block 链接时跳转 PDF 文本块（pageIndex/blockIndex 均为 0-based）。</summary>
    public Action<int, int>? OnNavigateToBlock { get; set; }

    /// <summary>整段剪贴板：页码【N】标签[M]："文字"（页码用【】，标签用 ASCII []）。</summary>
    private static readonly Regex CopySelectedClipRegex = new(
        @"^页码【(\d+)】标签\[(\d+)\]：[""\u201c\uFF02]([\s\S]*?)[""\u201d\uFF02]\s*$",
        RegexOptions.Compiled);

    /// <summary>正文中嵌入的「复制选中」片段（不依赖 Ctrl+V 拦截）。</summary>
    private static readonly Regex CopySelectedEmbedRegex = new(
        @"页码【(\d+)】标签\[(\d+)\]：[""\u201c\uFF02]([\s\S]*?)[""\u201d\uFF02]",
        RegexOptions.Compiled);

    private static readonly Regex InlineMarkupRegex = new(
        @"(\[([^\]]+)\]\(xm-block:(\d+):(\d+)\))|(\*\*(.+?)\*\*)|(==(.+?)==)",
        RegexOptions.Compiled);

    private record OutlineEntry(int LineIndex, int Level, string DisplayText);

    private IDisposable? _editSub;
    private CancellationTokenSource? _previewDebounce;
    private bool _convertingPaste;

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

        // Editor text change → 自动把「复制选中」格式转为跳转链接 + 预览/目录
        _editSub = NoteEditor.GetObservable(TextBox.TextProperty)
            .Subscribe(_ =>
            {
                if (_convertingPaste) return;
                ConvertEmbeddedCopySelectedToLinks();
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

        // 隧道拦截 Ctrl+V，优先于 TextBox 默认粘贴
        NoteEditor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);

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
    private void RenderMarkdownToTextBlock(TextBlock tb, string markdown)
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

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool firstInline = true;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd('\r').TrimEnd();

            // 空行：保留换行间距
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                tb.Inlines.Add(new LineBreak());
                firstInline = false;
                continue;
            }

            if (!firstInline)
                tb.Inlines.Add(new LineBreak());

            // Heading
            var hMatch = Regex.Match(trimmed.TrimStart(), @"^(#{1,3})\s+(.+)");
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
                firstInline = false;
                continue;
            }

            // Paragraph（含加粗 / 高亮 / xm-block 链接）
            RenderInlineLine(tb, trimmed.TrimStart());
            firstInline = false;
        }
    }

    /// <summary>渲染一行中的 [链接](xm-block:P:B)、**加粗**、==高亮==。</summary>
    private void RenderInlineLine(TextBlock tb, string text)
    {
        MatchCollection matches;
        try { matches = InlineMarkupRegex.Matches(text); }
        catch (Exception)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }
        int cursor = 0;

        foreach (Match m in matches)
        {
            if (m.Index > cursor)
                tb.Inlines.Add(new Run(text[cursor..m.Index]));

            if (m.Groups[1].Success) // [text](xm-block:P:B)
            {
                var label = m.Groups[2].Value;
                if (!int.TryParse(m.Groups[3].Value, out var pageIndex))
                    pageIndex = -1;
                if (!int.TryParse(m.Groups[4].Value, out var blockIndex))
                    blockIndex = -1;
                tb.Inlines.Add(CreateBlockLinkInline(label, pageIndex, blockIndex));
            }
            else if (m.Groups[5].Success) // **bold**
            {
                tb.Inlines.Add(new Run(m.Groups[6].Value) { FontWeight = FontWeight.Bold });
            }
            else if (m.Groups[7].Success) // ==highlight==
            {
                tb.Inlines.Add(new Run(m.Groups[8].Value)
                {
                    Background = new SolidColorBrush(0xFFFFEB3B),
                });
            }
            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
            tb.Inlines.Add(new Run(text[cursor..]));
    }

    private InlineUIContainer CreateBlockLinkInline(string label, int pageIndex, int blockIndex)
    {
        var linkTb = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = new SolidColorBrush(0xFF2563EB),
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(linkTb, $"跳转到 第{pageIndex + 1}页 块[{blockIndex + 1}]");
        linkTb.PointerPressed += (_, e) =>
        {
            if (pageIndex < 0 || blockIndex < 0) return;
            OnNavigateToBlock?.Invoke(pageIndex, blockIndex);
            StatusLabel.Text = $"已跳转: 第{pageIndex + 1}页 块[{blockIndex + 1}]";
            e.Handled = true;
        };
        return new InlineUIContainer { Child = linkTb };
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

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        // Avalonia TextBox 常绕过 KeyDown 直接粘贴；此处尽力拦截，正文转换兜底
        var isPasteKey = e.Key == Key.V || e.Key == Key.Insert;
        var withCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var withShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (_isReading || !isPasteKey) return;
        if (e.Key == Key.V && !withCtrl) return;
        if (e.Key == Key.Insert && !withShift) return;

        LogDebug($"[NOTE-PASTE] key intercept key={e.Key} mods={e.KeyModifiers}");
        e.Handled = true;
        CacheSelection();
        if (await TryPasteCopySelectedAsLinkAsync())
            return;
        await PastePlainClipboardAsync();
    }

    /// <summary>
    /// 把正文里刚粘贴进来的「页码【N】标签[M]："…"」自动替换为 xm-block 链接。
    /// 不依赖 Ctrl+V 拦截（Avalonia 默认粘贴常绕过 KeyDown）。
    /// </summary>
    private void ConvertEmbeddedCopySelectedToLinks()
    {
        var text = NoteEditor.Text ?? "";
        if (text.Length == 0 || !text.Contains("页码【", StringComparison.Ordinal))
            return;
        if (!CopySelectedEmbedRegex.IsMatch(text))
            return;

        var caret = NoteEditor.CaretIndex;
        var sb = new StringBuilder(text.Length);
        var last = 0;
        var converted = 0;
        MatchCollection matches;
        try { matches = CopySelectedEmbedRegex.Matches(text); }
        catch { return; }

        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups[1].Value, out var pageNum) || pageNum < 1) continue;
            if (!int.TryParse(m.Groups[2].Value, out var blockId) || blockId < 1) continue;

            var quote = m.Groups[3].Value.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            if (string.IsNullOrEmpty(quote))
                quote = $"第{pageNum}页块[{blockId}]";
            var label = quote.Replace("]", "］");
            if (label.Length > 40)
                label = label[..40] + "…";

            var replacement = $"[{label}](xm-block:{pageNum - 1}:{blockId - 1})";
            sb.Append(text, last, m.Index - last);
            sb.Append(replacement);

            if (m.Index + m.Length <= caret)
                caret += replacement.Length - m.Length;
            else if (m.Index < caret)
                caret = sb.Length;

            last = m.Index + m.Length;
            converted++;
            LogDebug($"[NOTE-PASTE] auto-convert page={pageNum} block={blockId} -> {replacement}");
        }

        if (converted == 0) return;
        sb.Append(text, last, text.Length - last);
        var newText = sb.ToString();
        if (newText == text) return;

        _convertingPaste = true;
        try
        {
            NoteEditor.Text = newText;
            NoteEditor.CaretIndex = Math.Clamp(caret, 0, newText.Length);
            _selStart = NoteEditor.CaretIndex;
            _selLength = 0;
            StatusLabel.Text = converted == 1
                ? "已转换为跳转链接（预览中可点击）"
                : $"已转换 {converted} 条跳转链接";
        }
        finally
        {
            _convertingPaste = false;
        }
    }

    /// <summary>
    /// 若剪贴板为「页码【N】标签[M]："文字"」，插入 [文字](xm-block:P:B) 并返回 true。
    /// </summary>
    private async Task<bool> TryPasteCopySelectedAsLinkAsync()
    {
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip == null) return false;
            var raw = await clip.GetTextAsync();
            if (string.IsNullOrWhiteSpace(raw)) return false;
            LogDebug($"[NOTE-PASTE] clipboard len={raw.Length} head='{raw.Trim()[..Math.Min(40, raw.Trim().Length)]}'");

            var m = CopySelectedClipRegex.Match(raw.Trim());
            if (!m.Success)
            {
                // 剪贴板可能夹杂换行，再试嵌入式匹配
                m = CopySelectedEmbedRegex.Match(raw.Trim());
                if (!m.Success) return false;
            }

            if (!int.TryParse(m.Groups[1].Value, out var pageNum) || pageNum < 1)
                return false;
            if (!int.TryParse(m.Groups[2].Value, out var blockId) || blockId < 1)
                return false;

            var quote = m.Groups[3].Value.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            if (string.IsNullOrEmpty(quote))
                quote = $"第{pageNum}页块[{blockId}]";

            var label = quote.Replace("]", "］");
            if (label.Length > 40)
                label = label[..40] + "…";

            var pageIndex = pageNum - 1;
            var blockIndex = blockId - 1;
            var insert = $"[{label}](xm-block:{pageIndex}:{blockIndex})";
            InsertAtSelection(insert, skipAutoConvert: true);
            StatusLabel.Text = $"已插入跳转链接 → 第{pageNum}页 块[{blockId}]";
            LogDebug($"[NOTE-PASTE] link page={pageIndex} block={blockIndex} labelLen={label.Length}");
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"[NOTE-PASTE] fail: {ex.Message}");
            return false;
        }
    }

    private async Task PastePlainClipboardAsync()
    {
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip == null) return;
            var raw = await clip.GetTextAsync();
            if (raw == null) return;
            InsertAtSelection(raw);
            // 粘贴后 Text 订阅会触发 ConvertEmbeddedCopySelectedToLinks
        }
        catch (Exception ex)
        {
            LogDebug($"[NOTE-PASTE] plain fail: {ex.Message}");
        }
    }

    private void InsertAtSelection(string insert, bool skipAutoConvert = false)
    {
        var text = NoteEditor.Text ?? "";
        var start = Math.Clamp(Math.Min(NoteEditor.SelectionStart, NoteEditor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(NoteEditor.SelectionStart, NoteEditor.SelectionEnd), 0, text.Length);
        if (start == end && _selLength > 0)
        {
            start = Math.Clamp(_selStart, 0, text.Length);
            end = Math.Clamp(_selStart + _selLength, 0, text.Length);
        }
        _convertingPaste = true;
        try
        {
            NoteEditor.Text = text[..start] + insert + text[end..];
            NoteEditor.CaretIndex = start + insert.Length;
            _selStart = NoteEditor.CaretIndex;
            _selLength = 0;
        }
        finally
        {
            _convertingPaste = false;
        }
        // 纯文本粘贴后补跑一次转换（Text 订阅在 flag=true 时被跳过）
        if (!skipAutoConvert)
            ConvertEmbeddedCopySelectedToLinks();
        _modified = true;
        RenderPreview();
        RefreshOutline();
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
        foreach (var line in md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
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

            // Strip block links → keep label
            trimmed = Regex.Replace(trimmed, @"\[([^\]]+)\]\(xm-block:\d+:\d+\)", "$1");
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
        sb.AppendLine("a.xm-block{color:#2563EB;text-decoration:underline}");
        sb.AppendLine("</style></head><body>");

        foreach (var line in md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                sb.AppendLine("<br>");
                continue;
            }

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
        // 先按片段转义，再包标签，避免把 HTML 标标记再转义掉
        var sb = new StringBuilder();
        var matches = InlineMarkupRegex.Matches(text);
        int cursor = 0;
        foreach (Match m in matches)
        {
            if (m.Index > cursor)
                sb.Append(EscapeHtml(text[cursor..m.Index]));

            if (m.Groups[1].Success)
            {
                var label = EscapeHtml(m.Groups[2].Value);
                var href = $"xm-block:{m.Groups[3].Value}:{m.Groups[4].Value}";
                sb.Append($"<a class=\"xm-block\" href=\"{href}\">{label}</a>");
            }
            else if (m.Groups[5].Success)
            {
                sb.Append("<b>").Append(EscapeHtml(m.Groups[6].Value)).Append("</b>");
            }
            else if (m.Groups[7].Success)
            {
                sb.Append("<span class=\"hl\">").Append(EscapeHtml(m.Groups[8].Value)).Append("</span>");
            }
            cursor = m.Index + m.Length;
        }
        if (cursor < text.Length)
            sb.Append(EscapeHtml(text[cursor..]));
        return sb.ToString();
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
