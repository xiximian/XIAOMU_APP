using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace Xiaomuocr.Views.Views;

/// <summary>
/// 全屏半透明蒙层：拖拽框选截图区域。
/// 确认返回屏幕像素矩形；取消 / Esc 返回 null。
/// </summary>
public class ScreenshotOverlayWindow : Window
{
    private readonly Canvas _canvas;
    private readonly Rectangle _selection;
    private Point? _start;
    private Rect _current;
    private PixelRect? _result;
    private readonly double _scale;
    private readonly int _originX;
    private readonly int _originY;

    private ScreenshotOverlayWindow(PixelRect virtualBounds, double scale)
    {
        _originX = virtualBounds.X;
        _originY = virtualBounds.Y;
        _scale = scale <= 0 ? 1.0 : scale;

        var dipW = virtualBounds.Width / _scale;
        var dipH = virtualBounds.Height / _scale;

        Title = "截图选区";
        Width = dipW;
        Height = dipH;
        Position = new PixelPoint(virtualBounds.X, virtualBounds.Y);
        WindowState = WindowState.Normal;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _canvas = new Canvas
        {
            Width = dipW,
            Height = dipH,
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
            Cursor = new Cursor(StandardCursorType.Cross),
            Focusable = true,
        };

        _selection = new Rectangle
        {
            Stroke = Brush.Parse("#4A7C59"),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(50, 74, 124, 89)),
            IsVisible = false,
        };

        var hint = new TextBlock
        {
            Text = "拖拽选择区域 · Esc 取消 · 松开鼠标确认",
            FontSize = 16,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Padding = new Thickness(12, 8),
        };
        Canvas.SetLeft(hint, 24);
        Canvas.SetTop(hint, 24);

        _canvas.Children.Add(_selection);
        _canvas.Children.Add(hint);
        Content = _canvas;

        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        KeyDown += OnKeyDown;
        Opened += (_, _) => _canvas.Focus();
    }

    public static async Task<PixelRect?> PickRegionAsync()
    {
        var (bounds, scale) = ResolveVirtualDesktop();
        var win = new ScreenshotOverlayWindow(bounds, scale);
        var tcs = new TaskCompletionSource<PixelRect?>();
        win.Closed += (_, _) => tcs.TrySetResult(win._result);
        win.Show();
        win.Activate();
        return await tcs.Task;
    }

    private static (PixelRect bounds, double scale) ResolveVirtualDesktop()
    {
        Screen[]? screens = null;
        double scale = 1.0;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
            if (host?.Screens?.All is { Count: > 0 } list)
            {
                screens = list.ToArray();
                scale = host.Screens.Primary?.Scaling ?? host.DesktopScaling;
            }
        }

        if (screens == null || screens.Length == 0)
            return (new PixelRect(0, 0, 1920, 1080), 1.0);

        var left = screens.Min(s => s.Bounds.X);
        var top = screens.Min(s => s.Bounds.Y);
        var right = screens.Max(s => s.Bounds.X + s.Bounds.Width);
        var bottom = screens.Max(s => s.Bounds.Y + s.Bounds.Height);
        return (new PixelRect(left, top, right - left, bottom - top), scale > 0 ? scale : 1.0);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _result = null;
            Close();
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _current = new Rect(_start.Value, new Size(0, 0));
        _selection.IsVisible = true;
        UpdateSelectionVisual();
        e.Pointer.Capture(_canvas);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_start == null) return;
        var p = e.GetPosition(_canvas);
        _current = NormalizeRect(_start.Value, p);
        UpdateSelectionVisual();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_start == null) return;
        e.Pointer.Capture(null);
        var p = e.GetPosition(_canvas);
        _current = NormalizeRect(_start.Value, p);
        _start = null;

        if (_current.Width < 4 || _current.Height < 4)
        {
            _result = null;
            Close();
            return;
        }

        var px = _originX + (int)Math.Round(_current.X * _scale);
        var py = _originY + (int)Math.Round(_current.Y * _scale);
        var pw = Math.Max(1, (int)Math.Round(_current.Width * _scale));
        var ph = Math.Max(1, (int)Math.Round(_current.Height * _scale));
        _result = new PixelRect(px, py, pw, ph);
        Close();
    }

    private void UpdateSelectionVisual()
    {
        Canvas.SetLeft(_selection, _current.X);
        Canvas.SetTop(_selection, _current.Y);
        _selection.Width = Math.Max(0, _current.Width);
        _selection.Height = Math.Max(0, _current.Height);
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
