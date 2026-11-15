using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IVersionService
{
    /// <summary>platform 为空时使用当前 OS（win / mac / mac-x64 / linux）。</summary>
    Task<VersionCheckResult> CheckUpdateAsync(string? platform = null);
}

public class VersionService : IVersionService
{
    private readonly IApiService _api;

    public VersionService(IApiService api)
    {
        _api = api;
    }

    public async Task<VersionCheckResult> CheckUpdateAsync(string? platform = null)
    {
        return await _api.PostAsync<VersionCheckResult>(Constants.Endpoints.VersionCheck,
            new VersionCheckRequest
            {
                CurrentVersion = Constants.AppVersion,
                Platform = platform ?? AppPlatform.Id,
            });
    }
}
