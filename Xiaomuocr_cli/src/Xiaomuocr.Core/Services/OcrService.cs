using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IOcrService
{
    Task<OcrTask> SubmitRecognizeAsync(string fileKey, int pageCount = 1,
        Dictionary<string, object>? options = null, Dictionary<string, object>? preprocess = null,
        string? documentName = null, int? pageNumber = null);
    Task<OcrTask> GetTaskAsync(string taskUuid);
    Task<OcrTaskListResult> GetTasksAsync(int page = 1, int pageSize = 20, string? status = null);
    Task<OcrSearchResult> SearchAsync(string keyword, int page = 1, int pageSize = 20);
}

public class OcrService : IOcrService
{
    private readonly IApiService _api;
    private readonly IAppLogService? _log;

    public OcrService(IApiService api, ISettingsService settings, IAppLogService? log = null)
    {
        _api = api;
        _ = settings; // 识别参数 UI 已隐藏，暂不读本地 OCR 预设
        _log = log;
    }

    public async Task<OcrTask> SubmitRecognizeAsync(string fileKey, int pageCount = 1,
        Dictionary<string, object>? options = null, Dictionary<string, object>? preprocess = null,
        string? documentName = null, int? pageNumber = null)
    {
        // 识别参数 UI 已隐藏：未显式传入时不带 options（避免旧预设/Paddle 参数误导模力方舟路径）
        Dictionary<string, object>? opts = options;
        string preset = options != null ? "explicit" : "none";
        var optsJson = opts == null
            ? "(null)"
            : System.Text.Json.JsonSerializer.Serialize(opts);
        _log?.Info(
            $"OCR 提交参数 preset={preset}, doc={documentName}, page={pageNumber}: {optsJson}");

        var req = new OcrRequest
        {
            FileKey = fileKey,
            PageCount = pageCount,
            Options = opts,
            Preprocess = preprocess,
            DocumentName = documentName,
            PageNumber = pageNumber,
        };
        return await _api.PostAsync<OcrTask>(Constants.Endpoints.OcrRecognize, req);
    }

    public async Task<OcrTask> GetTaskAsync(string taskUuid)
    {
        return await _api.GetAsync<OcrTask>($"{Constants.Endpoints.OcrTasks}/{taskUuid}");
    }

    public async Task<OcrTaskListResult> GetTasksAsync(int page = 1, int pageSize = 20, string? status = null)
    {
        var url = $"{Constants.Endpoints.OcrTasks}?page={page}&page_size={pageSize}";
        if (!string.IsNullOrEmpty(status))
            url += $"&status={status}";
        return await _api.GetAsync<OcrTaskListResult>(url);
    }

    public async Task<OcrSearchResult> SearchAsync(string keyword, int page = 1, int pageSize = 20)
    {
        return await _api.PostAsync<OcrSearchResult>(Constants.Endpoints.OcrSearch,
            new OcrSearchRequest { Keyword = keyword, Page = page, PageSize = pageSize });
    }
}
