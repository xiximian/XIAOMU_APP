using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QRCoder;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Views.Views;

public partial class WxLoginWindow : Window
{
    private readonly IAuthService _auth;
    private readonly CancellationTokenSource _pollCts = new();
    private string _sessionId = "";
    private bool _loginSuccess;
    private TokenResponse? _tokens;

    public bool LoginSuccess => _loginSuccess;
    public TokenResponse? Tokens => _tokens;

    public WxLoginWindow() : this(null!)
    {
    }

    public WxLoginWindow(IAuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        CloseBtn.Click += OnCloseClick;
        Closing += OnClosing;
    }

    /// <summary>拉取二维码并开始轮询。</summary>
    public async Task StartAsync()
    {
        try
        {
            StatusText.Text = "正在获取登录二维码...";
            var start = await _auth.StartWxQrLoginAsync();
            _sessionId = start.SessionId;
            SetQrCode(start.QrContent);
            StatusText.Text = "请使用微信扫描二维码";
            _ = PollAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取二维码失败: {ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x44, 0x3C));
            PollingText.Text = "";
        }
    }

    private void SetQrCode(string qrContent)
    {
        if (string.IsNullOrEmpty(qrContent)) return;
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrCodeData);
            var qrCodeImage = qrCode.GetGraphic(8, [0, 0, 0], [255, 255, 255], true);
            using var stream = new MemoryStream(qrCodeImage);
            QrCodeImage.Source = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"二维码生成失败: {ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x44, 0x3C));
        }
    }

    private async Task PollAsync()
    {
        var token = _pollCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token);
                try
                {
                    var result = await _auth.PollWxQrLoginAsync(_sessionId);
                    if (result.Status == "confirmed" && !string.IsNullOrEmpty(result.AccessToken))
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _loginSuccess = true;
                            _tokens = new TokenResponse
                            {
                                AccessToken = result.AccessToken!,
                                RefreshToken = result.RefreshToken ?? "",
                                ExpiresAt = result.ExpiresAt ?? DateTime.UtcNow.AddMinutes(30),
                            };
                            StatusText.Text = "登录成功！";
                            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0x59));
                            PollingText.Text = "";
                        });
                        _pollCts.Cancel();
                        await Dispatcher.UIThread.InvokeAsync(Close);
                        return;
                    }

                    if (result.Status is "expired" or "failed")
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            StatusText.Text = result.Detail ?? "登录失败或已过期，请关闭后重试";
                            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x44, 0x3C));
                            PollingText.Text = "";
                        });
                        _pollCts.Cancel();
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        PollingText.Text = result.Status == "pending"
                            ? "正在等待扫码确认..."
                            : (result.Detail ?? "正在等待扫码确认...");
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        PollingText.Text = "轮询异常，重试中…";
                    });
                    _ = ex;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _pollCts.Cancel();
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _pollCts.Cancel();
    }
}
