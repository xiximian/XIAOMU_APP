using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Views.Views;

public partial class OcrProgressWindow : Window
{
    private bool _allowClose;
    private bool _hasError;
    private bool _wasCancelled;
    private CancellationTokenSource? _cts;

    public OcrProgressWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public OcrProgressWindow(string title, string documentName) : this()
    {
        Title = title;
        TitleText.Text = title;
        DocumentText.Text = string.IsNullOrWhiteSpace(documentName) ? "—" : documentName;
    }

    public void ApplyProgress(OcrProgress p)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyProgress(p));
            return;
        }

        if (p.TotalPages > 0)
        {
            ProgressBar.Maximum = Math.Max(1, p.TotalPages);
            ProgressBar.Value = Math.Clamp(p.CurrentPage, 0, p.TotalPages);
            PageCountText.Text = $"进度：{p.CurrentPage} / {p.TotalPages} 页";
        }

        StatusText.Text = string.IsNullOrWhiteSpace(p.Status) ? StatusText.Text : p.Status;
        if (p.IsCancelled)
        {
            _wasCancelled = true;
            StatusText.Foreground = Brush.Parse("#9B722E");
        }
        else if (p.IsError)
        {
            _hasError = true;
            StatusText.Foreground = Brush.Parse("#B8443C");
        }
        else if (!_hasError && !_wasCancelled)
        {
            StatusText.Foreground = Brush.Parse("#4A5344");
        }

        if (p.IsFinished)
        {
            if (p.IsCancelled)
                MarkFinished(p.Status, cancelled: true);
            else
                MarkFinished(_hasError ? "已结束（部分页失败，详见状态）" : null);
        }
    }

    public void MarkFinished(string? errorMessage = null, bool cancelled = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkFinished(errorMessage, cancelled));
            return;
        }

        if (cancelled)
            _wasCancelled = true;

        _allowClose = true;
        CancelButton.IsEnabled = false;
        CancelButton.IsVisible = false;
        CloseButton.IsEnabled = true;
        CloseButton.Content = cancelled ? "关闭" : "完成";
        CloseButton.Background = Brush.Parse(
            cancelled ? "#9B722E" :
            errorMessage == null && !_hasError ? "#4A7C59" : "#9B722E");
        CloseButton.Foreground = Brushes.White;

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            StatusText.Text = errorMessage;
            StatusText.Foreground = Brush.Parse(cancelled ? "#9B722E" : "#B8443C");
            if (!cancelled)
                _hasError = true;
        }
        else if (!_hasError && !_wasCancelled)
        {
            StatusText.Foreground = Brush.Parse("#2D5A3D");
            if (ProgressBar.Maximum > 0)
                ProgressBar.Value = ProgressBar.Maximum;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => RequestCancel();

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        // 运行中点窗口关闭 = 请求取消，先不关窗，等任务停下后再允许关闭
        e.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_cts == null || _cts.IsCancellationRequested || _allowClose)
            return;

        StatusText.Text = "正在取消，请稍候…（当前页轮询结束后停止）";
        StatusText.Foreground = Brush.Parse("#9B722E");
        CancelButton.IsEnabled = false;
        CancelButton.Content = "取消中…";
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* ignore */ }
    }

    /// <summary>
    /// 弹出进度窗并执行批量 OCR；支持取消。结束后启用「完成/关闭」，关闭窗口后返回。
    /// </summary>
    /// <returns>true 正常完成；false 用户取消。其它失败仍抛出。</returns>
    public static async Task<bool> RunAsync(
        Window owner,
        string title,
        string documentName,
        Func<IProgress<OcrProgress>, CancellationToken, Task> work)
    {
        var win = new OcrProgressWindow(title, documentName);
        using var cts = new CancellationTokenSource();
        win._cts = cts;

        var progress = new Progress<OcrProgress>(win.ApplyProgress);

        Exception? fault = null;
        bool cancelled = false;

        async Task ExecuteAsync()
        {
            try
            {
                await work(progress, cts.Token);
                if (!win._allowClose)
                    win.MarkFinished();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                if (!win._allowClose)
                    win.MarkFinished("已取消识别", cancelled: true);
            }
            catch (Exception ex)
            {
                fault = ex;
                win.MarkFinished($"识别失败: {ex.Message}");
            }
        }

        var running = ExecuteAsync();
        await win.ShowDialog(owner);
        await running;

        if (fault != null)
            throw fault;

        return !cancelled;
    }
}
