using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.ViewModels;

namespace Xiaomuocr.Views.Views;

public partial class AccountView : UserControl
{
    private AccountViewModel? _currentVm;

    public AccountView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // 取消旧订阅
        if (_currentVm != null)
        {
            _currentVm.ShowPaymentQrCode -= OnShowPaymentQrCode;
        }

        _currentVm = DataContext as AccountViewModel;

        // 订阅新事件
        if (_currentVm != null)
        {
            _currentVm.ShowPaymentQrCode += OnShowPaymentQrCode;
        }
    }

    private async void OnShowPaymentQrCode(RechargeResult result)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null || _currentVm == null) return;

        var balanceService = _currentVm.BalanceService;
        var paymentWin = new PaymentWindow(
            balanceService,
            result.OutTradeNo,
            result.Amount,
            result.PaymentType);

        // 生成二维码：优先用 qrcode 字段，否则用 payurl
        var qrContent = !string.IsNullOrEmpty(result.QrCode) ? result.QrCode : result.PayUrlApi;
        paymentWin.SetQrCode(qrContent);
        paymentWin.StartPolling();

        await paymentWin.ShowDialog(owner);

        // 窗口关闭后检查支付结果
        if (paymentWin.PaymentSuccess)
        {
            _currentVm.RechargeMessage = $"充值成功！金额: {result.Amount:F2} 元";
            _currentVm.IsRechargeVisible = false;
            await _currentVm.LoadDataAsync();
        }
        else
        {
            _currentVm.RechargeMessage = "支付未完成，可稍后在消费记录中查看";
        }
    }

    private async void OnPurchaseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PricingPlan plan) return;
        if (DataContext is not AccountViewModel vm) return;
        if (vm.IsBusy) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            var ok = await ShowConfirmAsync(
                owner,
                "确认购买套餐",
                $"确定购买「{plan.Name}」吗？\n\n" +
                $"包含：{plan.PagesDisplay}\n" +
                $"价格：{plan.PriceDisplay}（{plan.UnitPriceDisplay}）\n" +
                $"{plan.ValidityDisplay}\n\n" +
                $"将从账户余额扣除 ¥{plan.TotalPrice:F2}。\n" +
                $"当前可用余额：¥{vm.Available:F2}",
                confirmText: "确认购买",
                confirmColor: "#9B722E");
            if (!ok) return;
        }

        await vm.PurchasePackageAsync(plan);
    }

    private static async Task<bool> ShowConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "确认",
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
