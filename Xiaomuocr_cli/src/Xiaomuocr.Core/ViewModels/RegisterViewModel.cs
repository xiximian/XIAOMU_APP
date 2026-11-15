using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class RegisterViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private readonly IApiService _api;
    private readonly ITokenStorage _tokenStorage;

    private string _phone = "";
    public string Phone
    {
        get => _phone;
        set => this.RaiseAndSetIfChanged(ref _phone, value);
    }

    private string _verifyCode = "";
    public string VerifyCode
    {
        get => _verifyCode;
        set => this.RaiseAndSetIfChanged(ref _verifyCode, value);
    }

    private string _password = "";
    public string Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    private string _confirmPassword = "";
    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => this.RaiseAndSetIfChanged(ref _confirmPassword, value);
    }

    private int _countdownSeconds;
    public int CountdownSeconds
    {
        get => _countdownSeconds;
        set => this.RaiseAndSetIfChanged(ref _countdownSeconds, value);
    }

    private bool _isSendingCode;
    public bool IsSendingCode
    {
        get => _isSendingCode;
        set => this.RaiseAndSetIfChanged(ref _isSendingCode, value);
    }

    public string SendCodeButtonText => CountdownSeconds > 0 ? $"{CountdownSeconds}s" : "获取验证码";
    public bool CanSendCode => CountdownSeconds == 0 && !IsSendingCode && !string.IsNullOrWhiteSpace(Phone);

    public Action? OnRegisterSucceeded { get; set; }
    public Action? OnBackToLogin { get; set; }

    public ReactiveCommand<Unit, Unit> RegisterCommand { get; }
    public ReactiveCommand<Unit, Unit> SendCodeCommand { get; }
    public ReactiveCommand<Unit, Unit> BackToLoginCommand { get; }

    private IDisposable? _countdownSubscription;

    public RegisterViewModel(IAuthService auth, IApiService api, ITokenStorage tokenStorage)
    {
        _auth = auth;
        _api = api;
        _tokenStorage = tokenStorage;

        var canRegister = this.WhenAnyValue(
            x => x.Phone, x => x.VerifyCode,
            x => x.Password, x => x.ConfirmPassword, x => x.IsBusy,
            (p, vc, pw, cp, busy) =>
                !busy
                && !string.IsNullOrWhiteSpace(p) && p.Length == 11
                && !string.IsNullOrWhiteSpace(vc) && vc.Length >= 4
                && pw.Length >= 6 && pw == cp);

        RegisterCommand = ReactiveCommand.CreateFromTask(RegisterAsync, canRegister);

        var canSendCode = this.WhenAnyValue(
            x => x.Phone, x => x.CountdownSeconds, x => x.IsSendingCode,
            (p, cd, sending) =>
                !string.IsNullOrWhiteSpace(p) && p.Length == 11 && cd == 0 && !sending);

        SendCodeCommand = ReactiveCommand.CreateFromTask(SendCodeAsync, canSendCode);

        BackToLoginCommand = ReactiveCommand.Create(() => OnBackToLogin?.Invoke());

        // 监听倒计时变化，更新按钮文本和可用状态
        this.WhenAnyValue(x => x.CountdownSeconds)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(SendCodeButtonText));
                this.RaisePropertyChanged(nameof(CanSendCode));
            });
    }

    private async Task SendCodeAsync()
    {
        IsSendingCode = true;
        ClearError();
        try
        {
            await _auth.SendSmsCodeAsync(Phone, "register");
            StartCountdown(60);
            StatusMessage = "验证码已发送";
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"发送失败: {ex.Message}";
        }
        finally
        {
            IsSendingCode = false;
        }
    }

    private void StartCountdown(int seconds)
    {
        CountdownSeconds = seconds;
        _countdownSubscription?.Dispose();
        _countdownSubscription = Observable
            .Interval(TimeSpan.FromSeconds(1), RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (CountdownSeconds > 0)
                {
                    CountdownSeconds--;
                }
                else
                {
                    _countdownSubscription?.Dispose();
                    _countdownSubscription = null;
                }
            });
    }

    private async Task RegisterAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            var resp = await _auth.RegisterAsync(Phone, VerifyCode, Password);
            _api.SetAccessToken(resp.AccessToken);
            _api.SetRefreshToken(resp.RefreshToken);
            await _tokenStorage.SaveTokensAsync(resp.AccessToken, resp.RefreshToken, resp.ExpiresAt);
            OnRegisterSucceeded?.Invoke();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"连接失败: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}