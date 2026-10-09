using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class OcrViewModel : ViewModelBase
{
    private readonly IOssService _oss;
    private readonly IOcrService _ocr;
    private readonly IBatchOcrService _batchOcr;
    private readonly IBalanceService _balance;

    private string _selectedFilePath = "";
    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFilePath, value);
            this.RaisePropertyChanged(nameof(FileName));
        }
    }

    public string FileName => string.IsNullOrEmpty(SelectedFilePath)
        ? "未选择文件" : Path.GetFileName(SelectedFilePath);

    private int _pageCount = 1;
    public int PageCount
    {
        get => _pageCount;
        set => this.RaiseAndSetIfChanged(ref _pageCount, value);
    }

    private decimal _cost;
    public decimal Cost
    {
        get => _cost;
        set => this.RaiseAndSetIfChanged(ref _cost, value);
    }

    private string _taskStatus = "idle"; // idle | uploading | processing | done | failed
    public string TaskStatus
    {
        get => _taskStatus;
        set => this.RaiseAndSetIfChanged(ref _taskStatus, value);
    }

    private string _resultText = "";
    public string ResultText
    {
        get => _resultText;
        set => this.RaiseAndSetIfChanged(ref _resultText, value);
    }

    private string? _currentTaskUuid;
    private CancellationTokenSource? _pollCts;

    public ReactiveCommand<Unit, Unit> SelectFileCommand { get; }
    public ReactiveCommand<Unit, Unit> SubmitOcrCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyResultCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveResultCommand { get; }

    public OcrViewModel(IOssService oss, IOcrService ocr, IBalanceService balance, IBatchOcrService batchOcr)
    {
        _oss = oss;
        _ocr = ocr;
        _batchOcr = batchOcr;
        _balance = balance;

        var canSubmit = this.WhenAnyValue(x => x.SelectedFilePath, x => x.TaskStatus,
            (path, status) => !string.IsNullOrEmpty(path) && (status == "idle" || status == "done" || status == "failed"));

        var canCancel = this.WhenAnyValue(x => x.TaskStatus,
            s => s == "uploading" || s == "processing");

        var hasResult = this.WhenAnyValue(x => x.ResultText, t => !string.IsNullOrEmpty(t));

        SelectFileCommand = ReactiveCommand.CreateFromTask(SelectFileAsync);
        SubmitOcrCommand = ReactiveCommand.CreateFromTask(SubmitOcrAsync, canSubmit);
        CancelCommand = ReactiveCommand.Create(CancelPolling, canCancel);
        CopyResultCommand = ReactiveCommand.CreateFromTask(CopyResultAsync, hasResult);
        SaveResultCommand = ReactiveCommand.CreateFromTask(SaveResultAsync, hasResult);

        this.WhenAnyValue(x => x.PageCount).Subscribe(p => Cost = p * 1.0m);
        Cost = 1.0m;
    }

    private async Task SelectFileAsync()
    {
        await Task.CompletedTask;
    }

    public async Task SubmitOcrAsync()
    {
        if (string.IsNullOrEmpty(SelectedFilePath)) return;

        IsBusy = true;
        ClearError();
        ResultText = "";
        Cost = PageCount * 1.0m;

        try
        {
            TaskStatus = "uploading";
            StatusMessage = "正在获取上传凭证...";
            var fileInfo = new FileInfo(SelectedFilePath);
            var mimeType = GetMimeType(SelectedFilePath);
            var tokenResp = await _oss.GetUploadTokenAsync(fileInfo.Name, fileInfo.Length, mimeType);

            StatusMessage = "正在上传文件...";
            var uploadResult = await _oss.UploadFileAsync(SelectedFilePath, tokenResp.Token, tokenResp.UploadUrl, tokenResp.Key);
            var fileKey = !string.IsNullOrEmpty(uploadResult.Key) ? uploadResult.Key : tokenResp.Key;
            if (string.IsNullOrEmpty(fileKey))
                throw new InvalidOperationException("上传成功但未得到 file_key");

            StatusMessage = "正在提交识别任务...";
            var response = await _batchOcr.SubmitBatchAsync(
                new List<string> { fileKey },
                fileInfo.Name,
                pageNumbers: new List<int> { 1 });
            _currentTaskUuid = response.BatchUuid;

            TaskStatus = "processing";
            await PollBatchAsync(response.BatchUuid);
        }
        catch (ApiException ex)
        {
            TaskStatus = "failed";
            StatusMessage = $"API错误: {ex.Message}";
            ErrorMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            TaskStatus = "failed";
            StatusMessage = $"上传失败: {ex.Message}";
            ErrorMessage = $"上传失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            TaskStatus = "failed";
            StatusMessage = $"失败: {ex.Message}";
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task PollBatchAsync(string batchUuid)
    {
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        var tmpDir = Path.Combine(Path.GetTempPath(), "xiaomuocr_" + batchUuid[..Math.Min(8, batchUuid.Length)]);
        Directory.CreateDirectory(tmpDir);

        try
        {
            await _batchOcr.StartPollingAsync(batchUuid, 1, tmpDir, async status =>
            {
                StatusMessage = status.Status is "completed" or "partial_failed"
                    ? "识别完成！"
                    : $"识别中... ({status.CompletedPages}/{status.TotalPages})";
                if (status.NewlyCompleted.Count > 0 && !string.IsNullOrEmpty(status.NewlyCompleted[0].ResultJson))
                    ParseResult(status.NewlyCompleted[0].ResultJson);
                await Task.CompletedTask;
            }, ct);

            TaskStatus = "done";
            StatusMessage = "识别完成！";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
        }
        catch (ApiException ex)
        {
            TaskStatus = "failed";
            StatusMessage = $"轮询失败: {ex.Message}";
        }
    }

    private async Task PollTaskAsync(string taskUuid)
    {
        // 兼容保留：旧单任务轮询（已不再使用）
        await PollBatchAsync(taskUuid);
    }

    private void ParseResult(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            ResultText = "(无结果)";
            return;
        }

        try
        {
            var doc = JsonDocument.Parse(resultJson);
            var texts = new List<string>();

            if (OcrResultJson.TryGetLayoutParsingResults(doc.RootElement, out var layouts))
            {
                foreach (var page in layouts.EnumerateArray())
                {
                    if (!page.TryGetProperty("prunedResult", out var pruned))
                        continue;

                    // 拓片 spotting：按行 rec_texts（与 parsing_res_list 并存时优先）
                    if (pruned.TryGetProperty("spotting_res", out var spotting)
                        && spotting.TryGetProperty("rec_texts", out var recTexts)
                        && recTexts.ValueKind == JsonValueKind.Array
                        && recTexts.GetArrayLength() > 0)
                    {
                        foreach (var t in recTexts.EnumerateArray())
                        {
                            var text = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                            if (!string.IsNullOrWhiteSpace(text))
                                texts.Add(text.Trim());
                        }
                        continue;
                    }

                    if (pruned.TryGetProperty("parsing_res_list", out var parsingList))
                    {
                        var sorted = parsingList.EnumerateArray()
                            .OrderBy(x =>
                            {
                                if (x.TryGetProperty("block_order", out var bo)
                                    && bo.ValueKind == JsonValueKind.Number)
                                    return bo.GetInt32();
                                return int.MaxValue;
                            });

                        foreach (var block in sorted)
                        {
                            var text = "";
                            if (block.TryGetProperty("block_content", out var bc)
                                && bc.ValueKind == JsonValueKind.String)
                                text = bc.GetString() ?? "";
                            else if (block.TryGetProperty("text", out var t)
                                && t.ValueKind == JsonValueKind.String)
                                text = t.GetString() ?? "";
                            else if (block.TryGetProperty("content", out var c)
                                && c.ValueKind == JsonValueKind.String)
                                text = c.GetString() ?? "";

                            if (!string.IsNullOrWhiteSpace(text))
                                texts.Add(text.Trim());
                        }
                    }
                }
            }

            ResultText = texts.Count > 0 ? string.Join("\n\n", texts) : "(文本为空)";
        }
        catch
        {
            ResultText = "(结果解析失败)";
        }
    }

    private void CancelPolling()
    {
        _pollCts?.Cancel();
        TaskStatus = "idle";
        StatusMessage = "已取消";
    }

    private async Task CopyResultAsync()
    {
        if (string.IsNullOrEmpty(ResultText)) return;
        // Will be handled by the View via TopLevel.GetTopLevel().Clipboard
        await Task.CompletedTask;
    }

    private async Task SaveResultAsync()
    {
        // Will be triggered from View via file dialog
        await Task.CompletedTask;
    }

    private static string GetMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".tiff" or ".tif" => "image/tiff",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }
}
