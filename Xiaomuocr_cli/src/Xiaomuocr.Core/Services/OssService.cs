using Xiaomuocr.Core;

namespace Xiaomuocr.Core.Services;

public interface IOssService
{
    Task<Models.UploadToken> GetUploadTokenAsync(string fileName, long fileSize, string contentType);
    Task<Models.BatchTokenResponse> GetUploadTokensBatchAsync(List<Models.BatchTokenFileItem> files);
    Task<Models.UploadResult> UploadFileAsync(string filePath, string token, string uploadUrl, string? key = null);
}

public class OssService : IOssService
{
    private readonly IApiService _api;
    private readonly IAppLogService? _log;

    /// <summary>OSS 直传/中继专用长寿命 HttpClient（勿每次 new）。</summary>
    private static readonly HttpClient OssHttp = CreateOssHttp();

    private static HttpClient CreateOssHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        return http;
    }

    public OssService(IApiService api, IAppLogService? log = null)
    {
        _api = api;
        _log = log;
    }

    public async Task<Models.UploadToken> GetUploadTokenAsync(string fileName, long fileSize, string contentType)
    {
        _log?.Info($"OSS: 获取上传凭证 file={fileName} size={fileSize} type={contentType}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(fileName), "file_name");
            content.Add(new StringContent(fileSize.ToString()), "file_size");
            content.Add(new StringContent(contentType), "content_type");
            var result = await _api.PostFormAsync<Models.UploadToken>(Constants.Endpoints.OssToken, content);
            sw.Stop();
            _log?.Info($"OSS: 获取凭证成功 key={result.Key} expires={result.ExpiresIn}s elapsed={sw.ElapsedMilliseconds}ms");
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log?.Error($"OSS: 获取凭证失败 file={fileName} elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
            throw;
        }
    }

    /// <summary>与后端 BatchTokenRequest.max_length 对齐；超出则客户端自动分片。</summary>
    private const int OssTokensBatchMax = 500;

    public async Task<Models.BatchTokenResponse> GetUploadTokensBatchAsync(List<Models.BatchTokenFileItem> files)
    {
        _log?.Info($"OSS: 批量获取上传凭证 count={files.Count}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (files.Count <= OssTokensBatchMax)
            {
                var req = new Models.BatchTokenRequest { Files = files };
                var result = await _api.PostAsync<Models.BatchTokenResponse>(Constants.Endpoints.OssTokensBatch, req);
                sw.Stop();
                _log?.Info($"OSS: 批量获取凭证成功 total={result.Total} elapsed={sw.ElapsedMilliseconds}ms");
                return result;
            }

            // 大批次分片请求后合并（兼容旧后端 500 上限；新后端 2000）
            var allTokens = new List<Models.BatchTokenItem>(files.Count);
            var chunkCount = (files.Count + OssTokensBatchMax - 1) / OssTokensBatchMax;
            for (var i = 0; i < files.Count; i += OssTokensBatchMax)
            {
                var chunk = files.GetRange(i, Math.Min(OssTokensBatchMax, files.Count - i));
                var chunkIdx = i / OssTokensBatchMax + 1;
                _log?.Info($"OSS: 分片获取凭证 {chunkIdx}/{chunkCount} size={chunk.Count}");
                var chunkReq = new Models.BatchTokenRequest { Files = chunk };
                var chunkResult = await _api.PostAsync<Models.BatchTokenResponse>(
                    Constants.Endpoints.OssTokensBatch, chunkReq);
                allTokens.AddRange(chunkResult.Tokens);
            }

            sw.Stop();
            _log?.Info($"OSS: 批量获取凭证成功(分片) total={allTokens.Count} chunks={chunkCount} elapsed={sw.ElapsedMilliseconds}ms");
            return new Models.BatchTokenResponse { Tokens = allTokens, Total = allTokens.Count };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log?.Error($"OSS: 批量获取凭证失败 count={files.Count} elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
            throw;
        }
    }

    public async Task<Models.UploadResult> UploadFileAsync(string filePath, string token, string uploadUrl, string? key = null)
    {
        var fileName = Path.GetFileName(filePath);
        var resolvedUrl = ResolveUploadUrl(uploadUrl, key);
        _log?.Info($"OSS: 开始上传 file={fileName} key={key} url={SanitizeUrlForLog(resolvedUrl)}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var content = new MultipartFormDataContent();

            var fileContent = new ByteArrayContent(fileBytes);
            var ext = Path.GetExtension(filePath);
            if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            else if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "file", Path.GetFileName(filePath));
            content.Add(new StringContent(token), "token");

            // 七牛云模式需要传 key；local/jdcloud 中继同样带上预生成 key
            if (!string.IsNullOrEmpty(key))
                content.Add(new StringContent(key), "key");

            // 去掉末尾斜杠，防止七牛云返回 400
            var cleanUrl = resolvedUrl.TrimEnd('/');

            var response = await OssHttp.PostAsync(cleanUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                // 截断错误体，防止 OSS 返回的 URL/Token 等敏感信息通过异常消息泄漏到日志
                var safeBody = errorBody.Length > 200 ? errorBody[..200] + "..." : errorBody;
                throw new InvalidOperationException(
                    $"上传失败 (HTTP {(int)response.StatusCode}): {safeBody}");
            }

            // 后端返回: {"key":"...","file_name":"...","file_size":N}；七牛直传可能只有 key
            var json = await response.Content.ReadAsStringAsync();
            var options = new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };
            var result = System.Text.Json.JsonSerializer.Deserialize<Models.UploadResult>(json, options)
                         ?? new Models.UploadResult();
            if (string.IsNullOrEmpty(result.Key))
                result.Key = key ?? "";
            if (string.IsNullOrEmpty(result.Key))
                throw new InvalidOperationException("上传成功但未返回 file_key");
            sw.Stop();
            _log?.Info($"OSS: 上传完成 file={fileName} key={result.Key} size={fileBytes.Length} elapsed={sw.ElapsedMilliseconds}ms");
            return result;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            sw.Stop();
            _log?.Error($"OSS: 上传异常 file={fileName} elapsed={sw.ElapsedMilliseconds}ms err={ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 后端中继上传（local / jdcloud）必须打到当前 API 同一台机器。
    /// 凭证里的 upload_url 常来自服务端 PUBLIC_URL；本地调试若仍指向生产，
    /// 会出现「上传成功但 OCR 报本地图片不存在」。
    /// 七牛直传域名保持原样。
    /// </summary>
    private string ResolveUploadUrl(string uploadUrl, string? key)
    {
        var apiBase = (_api.BaseUrl ?? "").TrimEnd('/');
        var relayFallback = string.IsNullOrEmpty(apiBase)
            ? ""
            : apiBase + Constants.Endpoints.OssUpload;

        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            if (!string.IsNullOrEmpty(relayFallback) &&
                (key?.StartsWith("local/", StringComparison.OrdinalIgnoreCase) == true
                 || key?.StartsWith("xiaomuocrapp_pic/", StringComparison.OrdinalIgnoreCase) == true))
                return relayFallback;
            return uploadUrl ?? "";
        }

        if (uploadUrl.StartsWith('/'))
        {
            return string.IsNullOrEmpty(apiBase) ? uploadUrl : apiBase + uploadUrl;
        }

        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var remote))
            return uploadUrl;

        var isBackendRelay = remote.AbsolutePath.Contains("/oss/upload", StringComparison.OrdinalIgnoreCase);
        if (!isBackendRelay)
            return uploadUrl; // 七牛等直传

        if (string.IsNullOrEmpty(apiBase) || !Uri.TryCreate(apiBase, UriKind.Absolute, out var apiUri))
            return uploadUrl;

        if (string.Equals(remote.Scheme, apiUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(remote.Authority, apiUri.Authority, StringComparison.OrdinalIgnoreCase))
            return uploadUrl;

        _log?.Warn(
            $"OSS: upload_url 主机({remote.Authority})与 API({apiUri.Authority})不一致，" +
            "改走当前 API 中继上传，避免 file_key 落到另一台机器");
        return relayFallback;
    }

    private static string SanitizeUrlForLog(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try
        {
            var u = new Uri(url);
            return $"{u.Scheme}://{u.Authority}{u.AbsolutePath}";
        }
        catch
        {
            return url.Length > 80 ? url[..80] : url;
        }
    }
}
