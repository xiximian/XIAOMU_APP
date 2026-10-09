using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IApiService
{
    Task<T> GetAsync<T>(string endpoint);
    Task<T> PostAsync<T>(string endpoint, object? body = null);
    Task<T> PostFormAsync<T>(string endpoint, MultipartFormDataContent content);
    Task<T> PutAsync<T>(string endpoint, object? body = null);
    Task<T> PatchAsync<T>(string endpoint, object? body = null);
    Task DeleteAsync(string endpoint);
    void SetAccessToken(string token);
    void SetRefreshToken(string token);
    string? GetAccessToken();
    string? GetRefreshToken();
    string BaseUrl { get; set; }
    /// <summary>查询拓片等能力是否可用；失败时默认可用（不挡用户）。</summary>
    Task<bool> CheckRubbingAvailableAsync();
}

public class ApiService : IApiService
{
    private readonly HttpClient _http;
    private readonly ITokenStorage _tokenStorage;
    private readonly IAppLogService? _log;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _accessToken;
    private string? _refreshToken;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _baseUrl = "";

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = value.TrimEnd('/');
    }

    public ApiService(HttpClient httpClient, ITokenStorage tokenStorage, IAppLogService? log = null)
    {
        _http = httpClient;
        _tokenStorage = tokenStorage;
        _log = log;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        // 所有请求自动附带客户端版本信息
        _http.DefaultRequestHeaders.Remove("X-Client-Version");
        _http.DefaultRequestHeaders.Add("X-Client-Version", Constants.AppVersion);
        _http.DefaultRequestHeaders.Add("X-Api-Version", Constants.ApiVersion);
    }

    public void SetAccessToken(string token) => _accessToken = token;
    public void SetRefreshToken(string token) => _refreshToken = token;
    public string? GetAccessToken() => _accessToken;
    public string? GetRefreshToken() => _refreshToken;

    // ================ HTTP Methods ================

    public async Task<T> GetAsync<T>(string endpoint)
    {
        return await SendWithRetryAsync<T>(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{endpoint}");
            return req;
        });
    }

    public async Task<bool> CheckRubbingAvailableAsync()
    {
        // 拓片=特定参数集合；按钮可用性取决于服务端是否有 supports_params 的通道
        try
        {
            var result = await GetAsync<JsonElement>(Constants.Endpoints.OcrCapabilities);
            if (result.TryGetProperty("params_available", out var paramsAvail))
                return paramsAvail.ValueKind != JsonValueKind.False && paramsAvail.GetBoolean();
            // 兼容旧响应
            if (result.TryGetProperty("features", out var features)
                && features.TryGetProperty("rubbing", out var rubbing))
                return rubbing.ValueKind != JsonValueKind.False && rubbing.GetBoolean();
            if (result.TryGetProperty("rubbing_available", out var legacy))
                return legacy.ValueKind != JsonValueKind.False && legacy.GetBoolean();
            return true;
        }
        catch
        {
            return true;
        }
    }

    public async Task<T> PostAsync<T>(string endpoint, object? body = null)
    {
        return await SendWithRetryAsync<T>(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{endpoint}");
            if (body != null)
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body, _jsonOptions),
                    Encoding.UTF8, "application/json");
            return req;
        });
    }

    public async Task<T> PostFormAsync<T>(string endpoint, MultipartFormDataContent content)
    {
        return await SendWithRetryAsync<T>(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{endpoint}")
            {
                Content = content
            };
            return req;
        });
    }

    public async Task<T> PutAsync<T>(string endpoint, object? body = null)
    {
        return await SendWithRetryAsync<T>(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}{endpoint}");
            if (body != null)
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body, _jsonOptions),
                    Encoding.UTF8, "application/json");
            return req;
        });
    }

    public async Task<T> PatchAsync<T>(string endpoint, object? body = null)
    {
        return await SendWithRetryAsync<T>(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, $"{_baseUrl}{endpoint}");
            if (body != null)
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body, _jsonOptions),
                    Encoding.UTF8, "application/json");
            return req;
        });
    }

    public async Task DeleteAsync(string endpoint)
    {
        await SendWithRetryAsync<object?>(() =>
            new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}{endpoint}"));
    }

    // ================ Core pipeline ================

    private async Task<T> SendWithRetryAsync<T>(Func<HttpRequestMessage> requestFactory)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var request = requestFactory();
            AttachAuthHeader(request);

            // 安全日志：只记录方法和路径，不记录完整URL（可能含token参数）和header（含Authorization）
            var safePath = request.RequestUri?.AbsolutePath ?? request.RequestUri?.ToString() ?? "unknown";
            _log?.Debug($"API: {request.Method} {safePath} attempt={attempt + 1}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            HttpResponseMessage? response = null;
            try
            {
                try
                {
                    response = await _http.SendAsync(request);
                }
                catch (TaskCanceledException ex)
                {
                    // HttpClient.Timeout：多半是复用了半开连接，后端收不到包。只加一次短重试。
                    sw.Stop();
                    if (attempt < 1)
                    {
                        _log?.Warn($"API: {request.Method} {safePath} 超时 attempt={attempt + 1} elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}，将换连接重试");
                        await Task.Delay(TimeSpan.FromMilliseconds(300));
                        continue;
                    }
                    _log?.Error($"API: {request.Method} {safePath} 超时 elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    sw.Stop();
                    if (attempt < maxRetries)
                    {
                        _log?.Warn($"API: {request.Method} {safePath} 网络异常 attempt={attempt + 1} elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}，将重试");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                        continue;
                    }
                    _log?.Error($"API: {request.Method} {safePath} 网络异常（已达最大重试） elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
                    throw;
                }

                // 401 → try token refresh
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _log?.Warn($"API: {request.Method} {safePath} 401未授权，尝试刷新token");
                    var refreshed = await TryRefreshToken();
                    if (refreshed)
                    {
                        response.Dispose();
                        using var retryReq = requestFactory();
                        AttachAuthHeader(retryReq);
                        response = await _http.SendAsync(retryReq);
                    }
                }

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    sw.Stop();
                    _log?.Debug($"API: {request.Method} {safePath} → {(int)response.StatusCode} elapsed={sw.ElapsedMilliseconds}ms len={json.Length}");
                    if (typeof(T) == typeof(object))
                        return default!;
                    return JsonSerializer.Deserialize<T>(json, _jsonOptions)!;
                }

                // 5xx / 429 → retry
                var code = (int)response.StatusCode;
                if ((code >= 500 || code == 429) && attempt < maxRetries)
                {
                    sw.Stop();
                    _log?.Warn($"API: {request.Method} {safePath} → {code} attempt={attempt + 1} elapsed={sw.ElapsedMilliseconds}ms，将重试");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }

                // Deserialize error（422 的 detail 常为数组，不能当 string）
                var errorJson = await response.Content.ReadAsStringAsync();
                var message = $"HTTP {(int)response.StatusCode}";
                try
                {
                    using var doc = JsonDocument.Parse(errorJson);
                    if (doc.RootElement.TryGetProperty("detail", out var detailEl))
                    {
                        if (detailEl.ValueKind == JsonValueKind.String)
                            message = detailEl.GetString() ?? message;
                        else if (detailEl.ValueKind == JsonValueKind.Array)
                        {
                            var parts = new List<string>();
                            foreach (var item in detailEl.EnumerateArray())
                            {
                                var loc = item.TryGetProperty("loc", out var locEl)
                                    ? string.Join(".", locEl.EnumerateArray().Select(x => x.ToString()))
                                    : "";
                                var msg = item.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : item.ToString();
                                parts.Add(string.IsNullOrEmpty(loc) ? (msg ?? "") : $"{loc}: {msg}");
                            }
                            if (parts.Count > 0)
                                message = string.Join("; ", parts);
                        }
                        else
                            message = detailEl.ToString();
                    }
                }
                catch
                {
                    ApiError? error = null;
                    try { error = JsonSerializer.Deserialize<ApiError>(errorJson, _jsonOptions); }
                    catch { /* ignore */ }
                    if (!string.IsNullOrEmpty(error?.Detail))
                        message = error.Detail;
                }

                sw.Stop();
                _log?.Error($"API: {request.Method} {safePath} → {(int)response.StatusCode} elapsed={sw.ElapsedMilliseconds}ms err={message}");
                throw new ApiException(message, (int)response.StatusCode, (int)response.StatusCode);
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new ApiException("请求失败，已达到最大重试次数", 0, 0);
    }

    private void AttachAuthHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private async Task<bool> TryRefreshToken()
    {
        if (string.IsNullOrEmpty(_refreshToken))
            return false;

        await _refreshLock.WaitAsync();
        try
        {
            var refreshReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{Constants.Endpoints.Refresh}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new RefreshRequest { RefreshToken = _refreshToken }, _jsonOptions),
                    Encoding.UTF8, "application/json")
            };

            using var response = await _http.SendAsync(refreshReq);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync();
            var tokenResp = JsonSerializer.Deserialize<TokenResponse>(json, _jsonOptions);
            if (tokenResp == null) return false;

            _accessToken = tokenResp.AccessToken;
            _refreshToken = tokenResp.RefreshToken;
            await _tokenStorage.SaveTokensAsync(
                tokenResp.AccessToken, tokenResp.RefreshToken, tokenResp.ExpiresAt);
            return true;
        }
        catch { return false; }
        finally { _refreshLock.Release(); }
    }
}

public class ApiException : Exception
{
    public int StatusCode { get; }
    public int ErrorCode { get; }

    public ApiException(string message, int statusCode, int errorCode) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
