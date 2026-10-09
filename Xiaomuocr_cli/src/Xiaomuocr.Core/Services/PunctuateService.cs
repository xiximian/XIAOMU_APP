using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IPunctuateService
{
    Task<PunctuatePageResponse> PunctuatePageAsync(
        IEnumerable<(string Id, string Text)> blocks,
        bool? keepTraditional = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 句读专用长超时客户端（首次加载模型可能远超全局 HttpClient 的 180s）。
/// </summary>
public class PunctuateService : IPunctuateService
{
    /// <summary>首次拉模型/推理可能很慢，与主 API 的 180s 超时隔离。</summary>
    private static readonly HttpClient LongHttp = CreateLongHttp();

    private readonly IApiService _api;
    private readonly IAppLogService? _log;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PunctuateService(IApiService api, IAppLogService? log = null)
    {
        _api = api;
        _log = log;
    }

    private static HttpClient CreateLongHttp()
    {
        // 与历史实现一致：独立 HttpClient，不改进程内其它 HttpClient（登录等走 ApiService）
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add("X-Client-Version", Constants.AppVersion);
        http.DefaultRequestHeaders.Add("X-Api-Version", Constants.ApiVersion);
        return http;
    }

    public async Task<PunctuatePageResponse> PunctuatePageAsync(
        IEnumerable<(string Id, string Text)> blocks,
        bool? keepTraditional = null,
        CancellationToken cancellationToken = default)
    {
        var reqBody = new PunctuatePageRequest
        {
            KeepTraditional = keepTraditional,
            Blocks = blocks
                .Select(b => new PunctuateBlockRequest { Id = b.Id, Text = b.Text ?? "" })
                .ToList(),
        };

        var url = $"{_api.BaseUrl.TrimEnd('/')}{Constants.Endpoints.PunctuatePage}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var token = _api.GetAccessToken();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        request.Content = new StringContent(
            JsonSerializer.Serialize(reqBody, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        _log?.Info($"句读请求: blocks={reqBody.Blocks.Count}（首次加载模型可能需数分钟）");
        try
        {
            using var response = await LongHttp
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<PunctuatePageResponse>(json, _jsonOptions)
                       ?? new PunctuatePageResponse();
            }

            var message = $"HTTP {(int)response.StatusCode}";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("detail", out var detailEl)
                    && detailEl.ValueKind == JsonValueKind.String)
                {
                    message = detailEl.GetString() ?? message;
                }
            }
            catch
            {
                /* keep message */
            }

            throw new ApiException(message, (int)response.StatusCode, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new TimeoutException(
                "句读请求超时（首次下载/加载模型可能较慢，请确认服务端模型已缓存后重试）", ex);
        }
    }
}
