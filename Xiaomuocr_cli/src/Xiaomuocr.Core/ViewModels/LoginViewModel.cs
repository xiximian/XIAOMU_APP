using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class LoginViewModel : ViewModelBase
{
    private readonly IAuthService _auth;
    private readonly IApiService _api;
    private readonly ITokenStorage _tokenStorage;

    private string _loginId = "";
    public string LoginId
    {
        get => _loginId;
        set => this.RaiseAndSetIfChanged(ref _loginId, value);
    }

    private string _password = "";
    public string Password
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    /// <summary>Callback when login succeeds. MainWindow sets this to switch pages.</summary>
    public Action? OnLoginSucceeded { get; set; }
    public Action? OnGoToRegister { get; set; }
    public Action? OnGoToResetPassword { get; set; }
    public Action? OnOfflineReading { get; set; }
    /// <summary>打开微信扫码登录窗口（由 MainWindow 实现）。</summary>
    public Func<Task>? OnWxLoginRequested { get; set; }

    public ReactiveCommand<Unit, Unit> LoginCommand { get; }
    public ReactiveCommand<Unit, Unit> GoToRegisterCommand { get; }
    public ReactiveCommand<Unit, Unit> GoToResetPasswordCommand { get; }
    public ReactiveCommand<Unit, Unit> OfflineReadingCommand { get; }
    public ReactiveCommand<Unit, Unit> WxLoginCommand { get; }

    private bool _isOfflineEntryVisible = true;
    public bool IsOfflineEntryVisible
    {
        get => _isOfflineEntryVisible;
        set => this.RaiseAndSetIfChanged(ref _isOfflineEntryVisible, value);
    }

    private bool _rememberAccount = true;
    public bool RememberAccount
    {
        get => _rememberAccount;
        set => this.RaiseAndSetIfChanged(ref _rememberAccount, value);
    }

    private string _serverConnectionLabel = "";
    /// <summary>登录页左下角连接提示（不含服务器地址）。</summary>
    public string ServerConnectionLabel
    {
        get => _serverConnectionLabel;
        private set => this.RaiseAndSetIfChanged(ref _serverConnectionLabel, value);
    }

    public bool HasServerConnection => !string.IsNullOrWhiteSpace(ServerConnectionLabel);

    public LoginViewModel(IAuthService auth, IApiService api, ITokenStorage tokenStorage)
    {
        _auth = auth;
        _api = api;
        _tokenStorage = tokenStorage;

        if (!string.IsNullOrWhiteSpace(_api.BaseUrl))
        {
            ServerConnectionLabel = "已连接服务器";
            this.RaisePropertyChanged(nameof(HasServerConnection));
        }

        var canLogin = this.WhenAnyValue(
            x => x.LoginId, x => x.Password, x => x.IsBusy,
            (id, pw, busy) => !busy && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(pw));

        LoginCommand = ReactiveCommand.CreateFromTask(LoginAsync, canLogin);
        GoToRegisterCommand = ReactiveCommand.Create(() => OnGoToRegister?.Invoke());
        GoToResetPasswordCommand = ReactiveCommand.Create(() => OnGoToResetPassword?.Invoke());
        OfflineReadingCommand = ReactiveCommand.Create(() => OnOfflineReading?.Invoke());
        var canWxLogin = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        WxLoginCommand = ReactiveCommand.CreateFromTask(WxLoginAsync, canWxLogin);

        // 异步加载记住的账号
        _ = LoadRememberedLoginAsync();
    }

    private async Task WxLoginAsync()
    {
        if (OnWxLoginRequested == null) return;
        IsBusy = true;
        ClearError();
        try
        {
            await OnWxLoginRequested();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"微信登录失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRememberedLoginAsync()
    {
        var saved = await _tokenStorage.LoadRememberedLoginAsync();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            LoginId = saved;
            RememberAccount = true;
        }
    }

    private async Task LoginAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            var resp = await _auth.LoginAsync(LoginId, Password);
            _api.SetAccessToken(resp.AccessToken);
            _api.SetRefreshToken(resp.RefreshToken);
            await _tokenStorage.SaveTokensAsync(resp.AccessToken, resp.RefreshToken, resp.ExpiresAt);

            // 记住账号：保存或清除登录 ID
            if (RememberAccount)
                await _tokenStorage.SaveRememberedLoginAsync(LoginId);
            else
                await _tokenStorage.SaveRememberedLoginAsync("");

            OnLoginSucceeded?.Invoke();
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
