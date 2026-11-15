using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class ResetPasswordViewModel : ViewModelBase
{
    private readonly IAuthService _auth;

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

    private string _newPassword = "";
    public string NewPassword
    {
        get => _newPassword;
        set => this.RaiseAndSetIfChanged(ref _newPassword, value);
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

    public Action? OnResetSucceeded { get; set; }
    public Action? OnBackToLogin { get; set; }

    public ReactiveCommand<Unit, Unit> ResetPasswordCommand { get; }
    public ReactiveCommand<Unit, Unit> SendCodeCommand { get; }
    public ReactiveCommand<Unit, Unit> BackToLoginCommand { get; }

    private IDisposable? _countdownSubscription;

    public ResetPasswordViewModel(IAuthService auth)
    {
        _auth = auth;

        var canReset = this.WhenAnyValue(
            x => x.Phone, x => x.VerifyCode, x => x.NewPassword, x => x.ConfirmPassword, x => x.IsBusy,
            (p, vc, pw, cp, busy) =>
                !busy
                && !string.IsNullOrWhiteSpace(p) && p.Length == 11
                && !string.IsNullOrWhiteSpace(vc) && vc.Length >= 4
                && pw.Length >= 6 && pw == cp);

        ResetPasswordCommand = ReactiveCommand.CreateFromTask(ResetPasswordAsync, canReset);

        var canSendCode = this.WhenAnyValue(
            x => x.Phone, x => x.CountdownSeconds, x => x.IsSendingCode,
            (p, cd, sending) =>
                !string.IsNullOrWhiteSpace(p) && p.Length == 11 && cd == 0 && !sending);

        SendCodeCommand = ReactiveCommand.CreateFromTask(SendCodeAsync, canSendCode);

        BackToLoginCommand = ReactiveCommand.Create(() => OnBackToLogin?.Invoke());

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
            await _auth.SendSmsCodeAsync(Phone, "reset_password");
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

    private async Task ResetPasswordAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            await _auth.ResetPasswordAsync(Phone, VerifyCode, NewPassword);
            StatusMessage = "密码重置成功，请登录";
            OnResetSucceeded?.Invoke();
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