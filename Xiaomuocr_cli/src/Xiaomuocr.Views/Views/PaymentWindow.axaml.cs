using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QRCoder;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Views.Views;

public partial class PaymentWindow : Window
{
    private readonly IBalanceService _balanceService;
    private readonly string _outTradeNo;
    private readonly decimal _amount;
    private readonly CancellationTokenSource _pollCts = new();
    private bool _paymentSuccess;

    /// <summary>支付是否成功</summary>
    public bool PaymentSuccess => _paymentSuccess;

    public PaymentWindow() : this(null!, "", 0)
    {
        // 设计器用
    }

    public PaymentWindow(
        IBalanceService balanceService,
        string outTradeNo,
        decimal amount,
        string? paymentType = null)
    {
        InitializeComponent();
        _balanceService = balanceService;
        _outTradeNo = outTradeNo;
        _amount = amount;

        AmountText.Text = $"¥{amount:F2}";
        OrderNoText.Text = $"订单号: {outTradeNo}";
        StatusText.Text = ResolveScanHint(paymentType);

        RefreshBtn.Click += OnRefreshClick;
        CloseBtn.Click += OnCloseClick;

        Closing += OnClosing;
    }

    private static string ResolveScanHint(string? paymentType)
    {
        var t = (paymentType ?? "").Trim().ToLowerInvariant();
        if (t is "wxpay" or "wechat" or "weixin" or "yungouos")
            return "请使用微信扫描二维码完成支付";
        return "请使用支付宝扫描二维码完成支付";
    }

    /// <summary>设置二维码内容并生成图片</summary>
    public void SetQrCode(string qrContent)
    {
        if (string.IsNullOrEmpty(qrContent)) return;

        try
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrCodeData);
            var qrCodeImage = qrCode.GetGraphic(8, [0, 0, 0], [255, 255, 255], true);

            using var stream = new MemoryStream(qrCodeImage);
            var bitmap = new Bitmap(stream);
            QrCodeImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"二维码生成失败: {ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x44, 0x3C));
        }
    }

    /// <summary>启动轮询查单</summary>
    public void StartPolling()
    {
        _ = PollPaymentStatus();
    }

    private async Task PollPaymentStatus()
    {
        var token = _pollCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(3000, token);

                try
                {
                    var result = await _balanceService.SyncPaymentStatusAsync(_outTradeNo);
                    if (result.Status == "paid")
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _paymentSuccess = true;
                            StatusText.Text = "支付成功！";
                            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0x59));
                            PollingText.Text = "";
                            PollingText.IsVisible = false;
                            RefreshBtn.Content = "完成";
                            RefreshBtn.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0x59));
                        });
                        _pollCts.Cancel();
                        return;
                    }
                }
                catch
                {
                    // 查单失败，继续轮询
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PollingText.Text = "正在等待支付结果...";
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_paymentSuccess)
        {
            Close();
            return;
        }

        PollingText.Text = "正在查询支付结果...";
        try
        {
            var result = await _balanceService.SyncPaymentStatusAsync(_outTradeNo);
            if (result.Status == "paid")
            {
                _paymentSuccess = true;
                StatusText.Text = "支付成功！";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0x59));
                PollingText.Text = "";
                PollingText.IsVisible = false;
                RefreshBtn.Content = "完成";
                RefreshBtn.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0x59));
                _pollCts.Cancel();
            }
            else
            {
                PollingText.Text = "尚未支付，请扫码后重试";
            }
        }
        catch (Exception ex)
        {
            PollingText.Text = $"查询失败: {ex.Message}";
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