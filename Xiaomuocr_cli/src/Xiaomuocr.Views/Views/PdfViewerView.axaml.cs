using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.ViewModels;

namespace Xiaomuocr.Views.Views;

public partial class PdfViewerView : UserControl
{
    private PdfViewerViewModel? _vm;
    private bool _syncing;

    public PdfViewerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[PdfView] DataContext changed: {DataContext?.GetType().Name}");
        if (DataContext is PdfViewerViewModel vm)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.FitToWidthRequested -= OnFitToWidthRequested;
                _vm.SaveFileDialogRequested -= OnSaveFileDialogRequested;
            }
            _vm = vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.TextBlockClicked += OnTextBlockClicked;
            _vm.FitToWidthRequested += OnFitToWidthRequested;
            _vm.SaveFileDialogRequested += OnSaveFileDialogRequested;

            _vm.OpenSearchDialogAsync = OpenSearchDialogImplAsync;
            _vm.OpenHighlightListDialogAsync = OpenHighlightListDialogImplAsync;
            _vm.OpenNoteDialogAsync = OpenNoteDialogImplAsync;
            _vm.OpenExportDialogAsync = OpenExportDialogImplAsync;
            _vm.ShowBatchOcrConfirmAsync = ShowBatchOcrConfirmImplAsync;

            System.Diagnostics.Debug.WriteLine($"[PdfView] Subscribed to VM, DisplayImage={_vm.DisplayImage?.Length} bytes");
            _ = Dispatcher.UIThread.InvokeAsync(async () => await RefreshDisplay());
        }
    }

    private void OnFitToWidthRequested()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm == null) return;
            double w = PdfScrollViewer.Bounds.Width;
            if (w < 10) w = Bounds.Width * 0.6;
            if (w < 10) w = 800;
            _vm.ProvideViewportWidthForFit(w);
        }, DispatcherPriority.Loaded);
    }

    private async void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncing || _vm == null) return;
        switch (e.PropertyName)
        {
            case nameof(PdfViewerViewModel.DisplayImage):
            case nameof(PdfViewerViewModel.Zoom):
            case nameof(PdfViewerViewModel.Rotation):
            case nameof(PdfViewerViewModel.CurrentPage):
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await RefreshDisplay();
                    ScrollToCurrentPage();
                });
                break;
            case nameof(PdfViewerViewModel.BboxVersion):
            case nameof(PdfViewerViewModel.SelectedBlockIndex):
                await Dispatcher.UIThread.InvokeAsync(RefreshBboxSelection);
                break;
        }
    }

    private Task RefreshDisplay()
    {
        if (_vm?.DisplayImage == null)
        {
            System.Diagnostics.Debug.WriteLine("[PdfViewer] DisplayImage is null, skipping render");
            return Task.CompletedTask;
        }

        try
        {
            System.Diagnostics.Debug.WriteLine($"[PdfViewer] RefreshDisplay: {_vm.DisplayImage.Length} bytes, {_vm.DisplayWidth}x{_vm.DisplayHeight}");
            if (PdfImage.Source is IDisposable old)
            {
                PdfImage.Source = null;
                old.Dispose();
            }

            using var ms = new System.IO.MemoryStream(_vm.DisplayImage);
            var bmp = new Bitmap(ms);
            System.Diagnostics.Debug.WriteLine($"[PdfViewer] Bitmap created: {bmp.Size.Width}x{bmp.Size.Height}");
            PdfImage.Source = bmp;
            // 始终按位图像素布局，异形页不会被首页尺寸裁切
            var pxW = Math.Max(1, bmp.PixelSize.Width);
            var pxH = Math.Max(1, bmp.PixelSize.Height);
            PdfImage.Width = pxW;
            PdfImage.Height = pxH;
            BboxCanvas.Width = pxW;
            BboxCanvas.Height = pxH;
            DrawBboxes();
            PdfScrollViewer.Offset = new Vector(0, 0);
            System.Diagnostics.Debug.WriteLine("[PdfViewer] RefreshDisplay done");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PdfViewer] Render error: {ex}");
            try { PdfImage.Source = null; } catch { }
        }
        return Task.CompletedTask;
    }

    private void DrawBboxes()
    {
        try
        {
            BboxCanvas.Children.Clear();
            if (_vm == null) return;

            for (int i = 0; i < _vm.Blocks.Count; i++)
            {
                var block = _vm.Blocks[i];
                var bb = block.DisplayBbox;
                if (bb.Length < 4) continue;

                double x = bb[0], y = bb[1];
                double w = bb[2] - bb[0];
                double h = bb[3] - bb[1];

                if (w <= 1 || h <= 1 || w > 20000 || h > 20000) continue;
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(w) || double.IsNaN(h)) continue;

                bool isSelected = _vm.SelectedBlockIndex == i;
                var border = new Border
                {
                    Width = w,
                    Height = h,
                    BorderBrush = isSelected ? Brushes.LimeGreen : Brushes.DodgerBlue,
                    BorderThickness = new Thickness(isSelected ? 3.0 : 1.0),
                    Background = isSelected
                        ? new SolidColorBrush(Color.FromArgb(40, 0, 255, 0))
                        : Brushes.Transparent,
                    Tag = i,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        FontSize = 9,
                        FontWeight = FontWeight.Bold,
                        Foreground = isSelected ? Brushes.Green : Brushes.DodgerBlue,
                    },
                };

                Canvas.SetLeft(border, x);
                Canvas.SetTop(border, y);
                border.PointerPressed += OnBboxClicked;
                BboxCanvas.Children.Add(border);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PdfViewer] DrawBboxes error: {ex.Message}");
        }
    }

    private void OnBboxClicked(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is int idx)
        {
            System.Diagnostics.Debug.WriteLine($"[PdfView] Bbox #{idx + 1} clicked, blocks={_vm?.Blocks.Count}");
            _vm?.OnTextClicked(idx);
        }
    }

    private void RefreshBboxSelection()
    {
        System.Diagnostics.Debug.WriteLine($"[PdfView] RefreshBboxSelection, selected={_vm?.SelectedBlockIndex}");
        DrawBboxes();
    }

    private void OnTextBlockClicked(int blockIndex)
    {
        System.Diagnostics.Debug.WriteLine($"[PdfView] OnTextBlockClicked: block={blockIndex}");
        // 延迟到布局完成后再滚动，确保跨页跳转时 ListBox 已渲染目标项
        Dispatcher.UIThread.Post(() => ScrollToBlock(blockIndex), DispatcherPriority.Loaded);
    }

    private void OnTextBlockEditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is int blockIdx && _vm != null)
        {
            var alreadyEditing = _vm.Blocks.Any(b => b.IsEditing);
            _vm.LogDebug($"[EDIT] GotFocus block=#{blockIdx}, alreadyEditing={alreadyEditing}");
            for (int i = 0; i < _vm.Blocks.Count; i++)
                _vm.Blocks[i].IsEditing = (i == blockIdx - 1);
            _vm.OnTextClicked(blockIdx - 1);
        }
    }

    /// <summary>TextBox 失焦 → 自动退出编辑模式。点别处即可退出，无需按 Escape。</summary>
    private void OnTextBlockEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is OcrTextBlockViewModel vm)
        {
            _vm?.LogDebug($"[EDIT] LostFocus block=#{vm.Index}, eSource={e.Source?.GetType().Name}");
            vm.IsEditing = false;
        }
    }

    private void OnTextBlockEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is TextBox tb && tb.Tag is int blockIdx
            && _vm != null && blockIdx > 0 && blockIdx <= _vm.Blocks.Count)
        {
            _vm.Blocks[blockIdx - 1].IsEditing = false;
            e.Handled = true;
        }
    }

    private void ScrollToBlock(int blockIndex)
    {
        try
        {
            if (_vm != null && blockIndex >= 0 && blockIndex < _vm.Blocks.Count)
                TextBlocksList.ScrollIntoView(_vm.Blocks[blockIndex]);
        }
        catch { }
    }

    private void ScrollToCurrentPage()
    {
        try
        {
            if (_vm == null || _vm.PageNavItems.Count == 0) return;
            if (_vm.CurrentPage >= 0 && _vm.CurrentPage < _vm.PageNavItems.Count)
                PageNavList.ScrollIntoView(_vm.PageNavItems[_vm.CurrentPage]);
        }
        catch { }
    }

    private async void OnPageNavTapped(object? sender, TappedEventArgs e)
    {
        if (_vm == null || sender is not Border border || border.Tag is not int pageIndex) return;
        e.Handled = true;
        await _vm.JumpToPageAsync(pageIndex);
    }

    // ======== 高亮 ========

    /// <summary>TextBox 鼠标松开 → 捕获选中文字到 ViewModel（精准，不串台）。</summary>
    private void OnTextBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is OcrTextBlockViewModel vm)
        {
            var sel = GetNormalizedSelectedText(tb);
            _vm?.LogDebug($"[SEL] PointerReleased block=#{vm.Index}, selLen={sel?.Length ?? 0}");
            vm.CurrentSelectedText = sel;
        }
    }

    /// <summary>
    /// 从编辑中的 TextBox 现场读取选区并写回 ViewModel。
    /// 高亮/复制按钮虽 Focusable=False，仍以现场读取为准，避免只依赖 PointerReleased。
    /// </summary>
    private void CaptureLiveSelectionFromEditingBox()
    {
        if (_vm == null) return;
        var editBlock = _vm.Blocks.FirstOrDefault(b => b.IsEditing);
        if (editBlock == null) return;

        var tb = this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t =>
                ReferenceEquals(t.DataContext, editBlock)
                || (t.Tag is int idx && idx == editBlock.Index));

        if (tb == null) return;

        var sel = GetNormalizedSelectedText(tb);
        editBlock.CurrentSelectedText = sel;
        _vm.LogDebug($"[SEL] LiveCapture block=#{editBlock.Index}, selLen={sel?.Length ?? 0}");
    }

    /// <summary>Avalonia TextBox 向左拖选时 SelectionStart/End 顺序不保证，需归一化。</summary>
    private static string? GetNormalizedSelectedText(TextBox tb)
    {
        var text = tb.Text ?? "";
        var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        var length = Math.Abs(tb.SelectionEnd - tb.SelectionStart);
        if (length > 0 && start >= 0 && start + length <= text.Length)
            return text.Substring(start, length);

        return string.IsNullOrEmpty(tb.SelectedText) ? null : tb.SelectedText;
    }

    private void OnHighlightClick(object? sender, RoutedEventArgs e)
    {
        CaptureLiveSelectionFromEditingBox();
        _vm?.LogDebug($"[HL] Highlight button clicked, editingBlocks={_vm.Blocks.Count(b => b.IsEditing)}");
        _vm?.AddHighlightCommand.Execute().Subscribe();
    }

    /// <summary>复制选中文字，附带页码和标签ID。格式：页码【X】标签[Y]："文字"</summary>
    private async void OnCopySelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        CaptureLiveSelectionFromEditingBox();

        // 找到当前编辑块，读取其选中的文字
        var editBlock = _vm.Blocks.FirstOrDefault(b => b.IsEditing);
        string? selectedText = null;
        int blockIdx = -1;

        if (editBlock != null)
        {
            selectedText = editBlock.CurrentSelectedText?.Trim();
            blockIdx = editBlock.Index;
        }

        // 编辑模式下无选中 → 静默忽略
        if (string.IsNullOrEmpty(selectedText))
        {
            _vm.LogDebug("[COPY] No selection in editing block, ignored");
            return;
        }

        var pageNum = _vm.CurrentPageDisplay;
        var label = $"[{blockIdx}]";
        var formatted = $"页码【{pageNum}】标签{label}：\"{selectedText}\"";

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(formatted);
            _vm.LogDebug($"[COPY] Copied: page={pageNum}, label={label}, textLen={selectedText.Length}");
            _vm.StatusMessage = $"已复制: {selectedText[..Math.Min(selectedText.Length, 30)]}";
        }
    }

    /// <summary>单击 TextBlock → 进入编辑模式（选中 + 退出其他块编辑）。</summary>
    private void OnHighlightTextBlockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not int blockIdx
            || _vm == null || blockIdx < 1 || blockIdx > _vm.Blocks.Count)
            return;

        _vm.LogDebug($"[PRESS-TB] block=#{blockIdx}");
        EnterEditMode(blockIdx);
        e.Handled = true;
    }

    /// <summary>后备：单击外层 Border（当点击落在 TextBlock 之外时触发）。</summary>
    private void OnBlockBorderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not int blockIdx
            || _vm == null || blockIdx < 1 || blockIdx > _vm.Blocks.Count)
            return;

        _vm.LogDebug($"[PRESS-BORDER] block=#{blockIdx}, eSource={e.Source?.GetType().Name}");
        EnterEditMode(blockIdx);
        e.Handled = true;
    }

    private void EnterEditMode(int blockIdx)
    {
        if (_vm == null) return;
        // 退出所有编辑模式
        for (int i = 0; i < _vm.Blocks.Count; i++)
            _vm.Blocks[i].IsEditing = false;
        // 进入当前块的编辑模式
        _vm.Blocks[blockIdx - 1].IsEditing = true;
        _vm.OnTextClicked(blockIdx - 1);
        _vm.LogDebug($"[ENTER-EDIT] block=#{blockIdx}");
    }

    // ======== 搜索/高亮列表/导出/笔记 ========

    private Window? _searchDlg;
    private Window? _highlightDlg;
    private Window? _noteDlg;

    private Task<(int pageIndex, int blockIndex)?> OpenSearchDialogImplAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null || _vm == null) return Task.FromResult<(int, int)?>(null);
        var searchSvc = _vm.SearchService;
        if (searchSvc == null) return Task.FromResult<(int, int)?>(null);
        if (_searchDlg != null) { _searchDlg.Activate(); return Task.FromResult<(int, int)?>(null); }

        var dlg = new SearchDialog(searchSvc, _vm.JsonDir, _vm.TotalPages,
            onJumpRequested: (pi, bi) => _ = _vm.NavigateToBlock(pi, bi),
            documentName: _vm.DocumentName);
        dlg.Closed += (_, _) => _searchDlg = null;
        _searchDlg = dlg;
        dlg.Show(owner);
        return Task.FromResult<(int, int)?>(null);
    }

    private Task<(int pageIndex, int blockIndex)?> OpenHighlightListDialogImplAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null || _vm == null) return Task.FromResult<(int, int)?>(null);
        if (_highlightDlg != null) { _highlightDlg.Activate(); return Task.FromResult<(int, int)?>(null); }

        var highlights = _vm.GetDocHighlights();
        var converted = highlights.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        var dlg = new HighlightListDialog(_vm.JsonDir, converted,
            onJumpRequested: (pi, bi) => _ = _vm.NavigateToBlock(pi, bi));
        dlg.Closed += (_, _) => _highlightDlg = null;
        _highlightDlg = dlg;
        dlg.Show(owner);
        return Task.FromResult<(int, int)?>(null);
    }

    private Task OpenNoteDialogImplAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null || _vm == null) return Task.CompletedTask;
        if (_noteDlg is { IsVisible: true }) { _noteDlg.Activate(); return Task.CompletedTask; }

        var noteSvc = _vm.NoteService;
        if (noteSvc == null) return Task.CompletedTask;

        var dlg = new NoteDialog(noteSvc, _vm.JsonDir, _vm.DocumentName);
        dlg.Closed += (_, _) => _noteDlg = null;
        _noteDlg = dlg;
        dlg.Show(owner);
        return Task.CompletedTask;
    }

    private async Task<FullTextExportOptions?> OpenExportDialogImplAsync(int totalPages)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return null;
        var canPunctuate = _vm is { IsOffline: false, PunctuateSvc: not null };
        var dlg = new ExportFullTextDialog(totalPages, canPunctuate);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    private async Task<string?> OnSaveFileDialogRequested(string documentName, string fileName, string defaultExtension)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var ext = string.IsNullOrWhiteSpace(defaultExtension) ? ".txt" : defaultExtension;
        if (!ext.StartsWith('.')) ext = "." + ext;
        var isMd = ext.Equals(".md", StringComparison.OrdinalIgnoreCase);

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出全文",
            SuggestedFileName = fileName,
            DefaultExtension = ext.TrimStart('.'),
            FileTypeChoices = isMd
                ? new[]
                {
                    new FilePickerFileType("Markdown") { Patterns = new[] { "*.md" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
                }
                : new[]
                {
                    new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } },
                },
        });
        return file?.Path.LocalPath;
    }

    // ---- 批量 OCR 费用确认对话框 ----

    private Task<bool> ShowBatchOcrConfirmImplAsync(string title, string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return Task.FromResult(false);

        return ShowConfirmAsync(owner, title, message,
            confirmText: "确认识别", confirmColor: "#4A7C59");
    }

    private static async Task<bool> ShowConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "开始识别",
        string confirmColor = "#9B722E")
    {
        var tcs = new TaskCompletionSource<bool>();

        var okBtn = new Button
        {
            Content = confirmText,
            Background = Brush.Parse(confirmColor),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 100,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };
        var cancelBtn = new Button
        {
            Content = "取消",
            Background = Brush.Parse("#D4E5D9"),
            Foreground = Brush.Parse("#3D4A3E"),
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };

        var dlg = new Window
        {
            Title = title,
            Width = 460,
            Height = 280,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new Grid
                {
                    RowDefinitions = RowDefinitions.Parse("*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 14,
                            Foreground = Brush.Parse("#2D3A2C"),
                            [Grid.RowProperty] = 0,
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 16, 0, 0),
                            [Grid.RowProperty] = 1,
                            Children = { cancelBtn, okBtn },
                        },
                    },
                },
            },
        };

        okBtn.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false);

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }
}
