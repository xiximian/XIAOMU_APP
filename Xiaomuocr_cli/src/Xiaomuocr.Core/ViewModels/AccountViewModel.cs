using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class AccountViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private readonly IBalanceService _balance;
    private readonly IPricingService _pricing;

    /// <summary>暴露 BalanceService 供 View 层使用（如支付窗口轮询）</summary>
    public IBalanceService BalanceService => _balance;

    private string _username = "";
    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    private string _email = "";
    public string Email
    {
        get => _email;
        set => this.RaiseAndSetIfChanged(ref _email, value);
    }

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    private string _profileMessage = "";
    public string ProfileMessage
    {
        get => _profileMessage;
        set => this.RaiseAndSetIfChanged(ref _profileMessage, value);
    }

    private decimal _balanceAmount;
    public decimal BalanceAmount
    {
        get => _balanceAmount;
        set => this.RaiseAndSetIfChanged(ref _balanceAmount, value);
    }

    private decimal _frozenAmount;
    public decimal FrozenAmount
    {
        get => _frozenAmount;
        set => this.RaiseAndSetIfChanged(ref _frozenAmount, value);
    }

    private decimal _available;
    public decimal Available
    {
        get => _available;
        set => this.RaiseAndSetIfChanged(ref _available, value);
    }

    private int _packageRemaining;
    public int PackageRemaining
    {
        get => _packageRemaining;
        set
        {
            this.RaiseAndSetIfChanged(ref _packageRemaining, value);
            this.RaisePropertyChanged(nameof(HasOwnedPackages));
            this.RaisePropertyChanged(nameof(PackageRemainingDisplay));
        }
    }

    public string PackageRemainingDisplay =>
        PackageRemaining > 0 ? $"套餐可用 {PackageRemaining} 页" : "暂无套餐额度";

    private decimal _paygUnitPrice;
    public decimal PaygUnitPrice
    {
        get => _paygUnitPrice;
        set
        {
            this.RaiseAndSetIfChanged(ref _paygUnitPrice, value);
            this.RaisePropertyChanged(nameof(PaygPriceDisplay));
        }
    }

    public string PaygPriceDisplay =>
        PaygUnitPrice > 0 ? $"按量计费 ¥{PaygUnitPrice:F2}/页（套餐耗尽后自动切换）" : "";

    private bool _isRechargeVisible;
    public bool IsRechargeVisible
    {
        get => _isRechargeVisible;
        set => this.RaiseAndSetIfChanged(ref _isRechargeVisible, value);
    }

    /// <summary>充值金额文本（用 string 绑定，避免清空输入框时 decimal 转换红框报错）。</summary>
    private string _rechargeAmountText = "10";
    public string RechargeAmountText
    {
        get => _rechargeAmountText;
        set => this.RaiseAndSetIfChanged(ref _rechargeAmountText, value);
    }

    private static bool TryParseRechargeAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return decimal.TryParse(
                   text.Trim(),
                   System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out amount)
               || decimal.TryParse(text.Trim(), out amount);
    }

    private string _rechargeMessage = "";
    public string RechargeMessage
    {
        get => _rechargeMessage;
        set => this.RaiseAndSetIfChanged(ref _rechargeMessage, value);
    }

    private string _purchaseMessage = "";
    public string PurchaseMessage
    {
        get => _purchaseMessage;
        set
        {
            this.RaiseAndSetIfChanged(ref _purchaseMessage, value);
            this.RaisePropertyChanged(nameof(HasPurchaseMessage));
        }
    }

    public bool HasPurchaseMessage => !string.IsNullOrWhiteSpace(PurchaseMessage);

    private bool _purchaseSucceeded;
    public bool PurchaseSucceeded
    {
        get => _purchaseSucceeded;
        set => this.RaiseAndSetIfChanged(ref _purchaseSucceeded, value);
    }

    public ObservableCollection<UserPackageInfo> OwnedPackages { get; } = new();
    public ObservableCollection<PricingPlan> SalePlans { get; } = new();
    public ObservableCollection<ConsumptionRecord> Records { get; } = new();

    private bool _isOwnedPackagesEmpty = true;
    public bool IsOwnedPackagesEmpty
    {
        get => _isOwnedPackagesEmpty;
        set => this.RaiseAndSetIfChanged(ref _isOwnedPackagesEmpty, value);
    }

    public bool HasOwnedPackages => !IsOwnedPackagesEmpty;

    private bool _isSalePlansEmpty = true;
    public bool IsSalePlansEmpty
    {
        get => _isSalePlansEmpty;
        set => this.RaiseAndSetIfChanged(ref _isSalePlansEmpty, value);
    }

    private bool _isRecordsEmpty = true;
    public bool IsRecordsEmpty
    {
        get => _isRecordsEmpty;
        set => this.RaiseAndSetIfChanged(ref _isRecordsEmpty, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRechargeCommand { get; }
    public ReactiveCommand<Unit, Unit> WechatRechargeCommand { get; }
    public ReactiveCommand<Unit, Unit> AlipayRechargeCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveDisplayNameCommand { get; }

    /// <summary>当需要展示二维码支付窗口时触发，参数为 RechargeResult</summary>
    public event Action<RechargeResult>? ShowPaymentQrCode;
    public ReactiveCommand<Unit, Unit> LoadMoreRecordsCommand { get; }

    private int _currentPage = 1;
    private int _totalPages = 1;

    private bool _hasMoreRecords;
    public bool HasMoreRecords
    {
        get => _hasMoreRecords;
        set => this.RaiseAndSetIfChanged(ref _hasMoreRecords, value);
    }

    private bool _isInitialized;

    public AccountViewModel(IAuthService auth, IBalanceService balance, IPricingService pricing)
    {
        _auth = auth;
        _balance = balance;
        _pricing = pricing;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshOrLoadAsync);
        ToggleRechargeCommand = ReactiveCommand.Create(() =>
        {
            IsRechargeVisible = !IsRechargeVisible;
            RechargeMessage = "";
        });

        var canRecharge = this.WhenAnyValue(
            x => x.RechargeAmountText,
            text => TryParseRechargeAmount(text, out var amt) && amt > 0 && amt <= 10000);
        WechatRechargeCommand = ReactiveCommand.CreateFromTask(
            () => RechargeAsync("wxpay"), canRecharge);
        AlipayRechargeCommand = ReactiveCommand.CreateFromTask(
            () => RechargeAsync("alipay"), canRecharge);
        LoadMoreRecordsCommand = ReactiveCommand.CreateFromTask(LoadMoreRecordsAsync);
        SaveDisplayNameCommand = ReactiveCommand.CreateFromTask(SaveDisplayNameAsync);

        _ = LoadDataAsync();
    }

    private async Task SaveDisplayNameAsync()
    {
        ProfileMessage = "";
        try
        {
            var user = await _auth.UpdateProfileAsync(DisplayName);
            DisplayName = user.DisplayName ?? "";
            ProfileMessage = "显示名称已保存";
        }
        catch (ApiException ex)
        {
            ProfileMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ProfileMessage = $"保存失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 软刷新入口：已初始化则后台刷新（保留现有数据），否则全量加载。
    /// </summary>
    public Task RefreshOrLoadAsync()
    {
        return _isInitialized ? SoftRefreshAsync() : LoadDataAsync();
    }

    /// <summary>
    /// 软刷新：保留现有数据，后台拉取最新数据后静默更新UI。
    /// </summary>
    private async Task SoftRefreshAsync()
    {
        try
        {
            var user = await _auth.GetCurrentUserAsync();
            Username = user.Username;
            Email = user.Email;
            DisplayName = user.DisplayName ?? "";

            try
            {
                var assets = await _pricing.GetAssetsAsync();
                ApplyAssets(assets);
            }
            catch
            {
                var balance = await _balance.GetBalanceAsync();
                BalanceAmount = balance.Amount;
                FrozenAmount = balance.FrozenAmount;
                Available = balance.Available;
            }

            try
            {
                var config = await _pricing.GetConfigAsync();
                PaygUnitPrice = config.PaygUnitPrice;
                SalePlans.Clear();
                foreach (var p in config.Plans.Where(x => x.IsPackage && x.IsActive)
                             .OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                {
                    SalePlans.Add(p);
                }
                IsSalePlansEmpty = SalePlans.Count == 0;
            }
            catch { /* keep existing sale plans */ }

            _currentPage = 1;
            Records.Clear();
            var result = await _balance.GetRecordsAsync(1, 20);
            _totalPages = result.TotalPages;
            foreach (var r in result.Items) Records.Add(r);
            IsRecordsEmpty = Records.Count == 0;
            HasMoreRecords = _currentPage < _totalPages;
        }
        catch { /* soft refresh fails silently, keep existing data */ }
    }

    public async Task LoadDataAsync()
    {
        IsBusy = true;
        ClearError();
        StatusMessage = "正在加载账户数据...";
        try
        {
            var user = await _auth.GetCurrentUserAsync();
            Username = user.Username;
            Email = user.Email;
            DisplayName = user.DisplayName ?? "";

            // 资产（余额 + 套餐）优先；失败则回退余额接口
            try
            {
                var assets = await _pricing.GetAssetsAsync();
                ApplyAssets(assets);
            }
            catch
            {
                var balance = await _balance.GetBalanceAsync();
                BalanceAmount = balance.Amount;
                FrozenAmount = balance.FrozenAmount;
                Available = balance.Available;
            }

            // 定价配置（上架套餐）
            try
            {
                var config = await _pricing.GetConfigAsync();
                PaygUnitPrice = config.PaygUnitPrice;
                SalePlans.Clear();
                foreach (var p in config.Plans.Where(x => x.IsPackage && x.IsActive)
                             .OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                {
                    SalePlans.Add(p);
                }
                IsSalePlansEmpty = SalePlans.Count == 0;
            }
            catch (Exception ex)
            {
                StatusMessage = $"定价配置加载失败: {ex.Message}";
            }

            _currentPage = 1;
            Records.Clear();
            var result = await _balance.GetRecordsAsync(1, 20);
            _totalPages = result.TotalPages;
            foreach (var r in result.Items) Records.Add(r);
            IsRecordsEmpty = Records.Count == 0;
            HasMoreRecords = _currentPage < _totalPages;
            StatusMessage =
                $"加载完成: 余额={Available:F2}, 套餐={PackageRemaining}页, 在售套餐={SalePlans.Count}";
            _isInitialized = true;
        }
        catch (ApiException ex)
        {
            ErrorMessage = $"API错误: {ex.Message}";
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败: {ex.GetType().Name}: {ex.Message}";
            StatusMessage = null;
        }
        finally { IsBusy = false; }
    }

    private void ApplyAssets(UserAssets assets)
    {
        BalanceAmount = assets.Amount;
        FrozenAmount = assets.FrozenAmount;
        Available = assets.AvailableBalance;
        PackageRemaining = assets.PackageRemaining;
        if (assets.PaygUnitPrice > 0)
            PaygUnitPrice = assets.PaygUnitPrice;

        OwnedPackages.Clear();
        foreach (var pkg in assets.Packages.Where(p => p.Status == "active"))
            OwnedPackages.Add(pkg);
        IsOwnedPackagesEmpty = OwnedPackages.Count == 0;
    }

    /// <summary>购买资源包（由 View 确认后调用）。</summary>
    public async Task PurchasePackageAsync(PricingPlan plan)
    {
        if (plan == null || !plan.IsPackage) return;

        IsBusy = true;
        PurchaseMessage = "";
        PurchaseSucceeded = false;
        ClearError();
        try
        {
            var result = await _pricing.PurchaseAsync(plan.Id);
            PurchaseSucceeded = true;
            PurchaseMessage = result.Detail;
            StatusMessage = $"购买成功: {result.Detail}";
            await LoadDataAsync();
        }
        catch (ApiException ex)
        {
            PurchaseSucceeded = false;
            PurchaseMessage = $"购买失败: {ex.Message}";
            ErrorMessage = PurchaseMessage;
        }
        catch (Exception ex)
        {
            PurchaseSucceeded = false;
            PurchaseMessage = $"购买失败: {ex.Message}";
            ErrorMessage = PurchaseMessage;
        }
        finally { IsBusy = false; }
    }

    private async Task RechargeAsync(string paymentMethod)
    {
        if (!TryParseRechargeAmount(RechargeAmountText, out var amount) || amount <= 0 || amount > 10000)
        {
            RechargeMessage = "请输入有效金额（0.01～10000）";
            return;
        }

        IsBusy = true;
        RechargeMessage = "";
        try
        {
            var channelLabel = paymentMethod == "wxpay" ? "微信" : "支付宝";
            RechargeMessage = $"正在发起{channelLabel}充值...";
            var result = await _balance.RechargeAsync(amount, paymentMethod);

            if (result.IsQrPayMode)
            {
                RechargeMessage = "正在生成支付二维码...";
                ShowPaymentQrCode?.Invoke(result);
            }
            else
            {
                // Mock 模式：直接充值成功
                BalanceAmount = result.Amount;
                FrozenAmount = result.FrozenAmount;
                Available = result.Available;
                RechargeMessage = $"充值成功！金额: {amount:F2} 元";
                IsRechargeVisible = false;
                await LoadDataAsync();
            }
        }
        catch (ApiException ex)
        {
            RechargeMessage = $"充值失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            RechargeMessage = $"充值失败: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task LoadMoreRecordsAsync()
    {
        if (_currentPage >= _totalPages) return;
        _currentPage++;
        try
        {
            var result = await _balance.GetRecordsAsync(_currentPage, 20);
            _totalPages = result.TotalPages;
            foreach (var r in result.Items) Records.Add(r);
            HasMoreRecords = _currentPage < _totalPages;
        }
        catch { _currentPage--; }
    }
}
