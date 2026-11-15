using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IAuthService
{
    Task<TokenResponse> LoginAsync(string login, string password);
    Task<TokenResponse> RegisterAsync(string phone, string verifyCode, string password);
    Task SendSmsCodeAsync(string phone, string templateType);
    Task ResetPasswordAsync(string phone, string verifyCode, string newPassword);
    Task<TokenResponse> RefreshTokenAsync(string refreshToken);
    Task<UserInfo> GetCurrentUserAsync();
    Task<UserInfo> UpdateProfileAsync(string? displayName);
    Task<WxQrStartResponse> StartWxQrLoginAsync();
    Task<WxQrPollResponse> PollWxQrLoginAsync(string sessionId);
    void ApplyTokens(TokenResponse resp);
}

public class AuthService : IAuthService
{
    private readonly IApiService _api;
    private readonly ITokenStorage _tokenStorage;

    public AuthService(IApiService api, ITokenStorage tokenStorage)
    {
        _api = api;
        _tokenStorage = tokenStorage;
    }

    public async Task<TokenResponse> LoginAsync(string login, string password)
    {
        var resp = await _api.PostAsync<TokenResponse>(Constants.Endpoints.Login,
            new LoginRequest { Login = login, Password = password });
        UpdateTokens(resp);
        return resp;
    }

    public async Task<TokenResponse> RegisterAsync(string phone, string verifyCode, string password)
    {
        var resp = await _api.PostAsync<TokenResponse>(Constants.Endpoints.Register,
            new RegisterRequest
            {
                Phone = phone,
                VerifyCode = verifyCode,
                Password = password
            });
        UpdateTokens(resp);
        return resp;
    }

    public async Task SendSmsCodeAsync(string phone, string templateType)
    {
        await _api.PostAsync<object>(Constants.Endpoints.SendSmsCode,
            new SmsCodeRequest
            {
                Phone = phone,
                TemplateType = templateType
            });
    }

    public async Task ResetPasswordAsync(string phone, string verifyCode, string newPassword)
    {
        await _api.PostAsync<object>(Constants.Endpoints.ResetPassword,
            new ResetPasswordRequest
            {
                Phone = phone,
                VerifyCode = verifyCode,
                NewPassword = newPassword
            });
    }

    public async Task<TokenResponse> RefreshTokenAsync(string refreshToken)
    {
        var resp = await _api.PostAsync<TokenResponse>(Constants.Endpoints.Refresh,
            new RefreshRequest { RefreshToken = refreshToken });
        UpdateTokens(resp);
        return resp;
    }

    public async Task<UserInfo> GetCurrentUserAsync()
    {
        return await _api.GetAsync<UserInfo>(Constants.Endpoints.Me);
    }

    public Task<UserInfo> UpdateProfileAsync(string? displayName)
        => _api.PatchAsync<UserInfo>(
            Constants.Endpoints.UpdateProfile,
            new ProfileUpdateRequest { DisplayName = displayName });

    public Task<WxQrStartResponse> StartWxQrLoginAsync()
        => _api.PostAsync<WxQrStartResponse>(Constants.Endpoints.WxQrStart);

    public Task<WxQrPollResponse> PollWxQrLoginAsync(string sessionId)
        => _api.GetAsync<WxQrPollResponse>(Constants.Endpoints.WxQrPoll(sessionId));

    public void ApplyTokens(TokenResponse resp) => UpdateTokens(resp);

    private void UpdateTokens(TokenResponse resp)
    {
        _api.SetAccessToken(resp.AccessToken);
        _api.SetRefreshToken(resp.RefreshToken);
        _tokenStorage.SaveTokensAsync(resp.AccessToken, resp.RefreshToken, resp.ExpiresAt);
    }
}
