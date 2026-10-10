using ReactiveUI;
using System.Reactive;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private const string UnlockPasswordValue = "13123511975";
    private const int UnlockTapRequired = 5;
    private static readonly TimeSpan UnlockTapWindow = TimeSpan.FromSeconds(2);

    private readonly ISettingsService _settings;
    private readonly IApiService _api;
    private readonly IVersionService _version;
    private bool _suppressCustomMark;
    private readonly List<DateTime> _unlockTaps = new();

    private string _serverAddress = "";
    public string ServerAddress
    {
        get => _serverAddress;
        set => this.RaiseAndSetIfChanged(ref _serverAddress, value);
    }

    private bool _isServerUnlocked;
    /// <summary>本会话内是否已解锁「服务器设置」卡片。</summary>
    public bool IsServerUnlocked
    {
        get => _isServerUnlocked;
        set => this.RaiseAndSetIfChanged(ref _isServerUnlocked, value);
    }

    private bool _showUnlockPrompt;
    public bool ShowUnlockPrompt
    {
        get => _showUnlockPrompt;
        set => this.RaiseAndSetIfChanged(ref _showUnlockPrompt, value);
    }

    private string _unlockPassword = "";
    public string UnlockPassword
    {
        get => _unlockPassword;
        set => this.RaiseAndSetIfChanged(ref _unlockPassword, value);
    }

    private string? _unlockError;
    public string? UnlockError
    {
        get => _unlockError;
        set
        {
            this.RaiseAndSetIfChanged(ref _unlockError, value);
            this.RaisePropertyChanged(nameof(HasUnlockError));
        }
    }

    public bool HasUnlockError => !string.IsNullOrEmpty(UnlockError);

    private bool _connectionOk;
    public bool ConnectionOk
    {
        get => _connectionOk;
        set => this.RaiseAndSetIfChanged(ref _connectionOk, value);
    }

    private string _versionInfo = $"当前版本: {Constants.AppVersion}";
    public string VersionInfo
    {
        get => _versionInfo;
        set => this.RaiseAndSetIfChanged(ref _versionInfo, value);
    }

    private string _currentServerUrl = "";
    /// <summary>当前实际连接的服务器地址（只读显示，来自 ApiService.BaseUrl）。</summary>
    public string CurrentServerUrl
    {
        get => _currentServerUrl;
        set => this.RaiseAndSetIfChanged(ref _currentServerUrl, value);
    }

    /// <summary>应用程序目录路径（诊断用）。</summary>
    public string AppDirectory => System.AppContext.BaseDirectory;

    private string _ocrPreset = OcrVlOptions.PresetDefault;
    public string OcrPreset
    {
        get => _ocrPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _ocrPreset, value);
            this.RaisePropertyChanged(nameof(OcrPresetLabel));
        }
    }

    public string OcrPresetLabel => $"当前方案：{OcrVlOptions.PresetDisplayName(OcrPreset)}";

    private bool _useDocOrientationClassify;
    /// <summary>方向分类：固定 false，UI 不可开（开启会导致 BOX 坐标系与显示错位）。</summary>
    public bool UseDocOrientationClassify
    {
        get => false;
        set
        {
            if (_useDocOrientationClassify)
            {
                _useDocOrientationClassify = false;
                this.RaisePropertyChanged(nameof(UseDocOrientationClassify));
            }
        }
    }

    private bool _useDocUnwarping;
    public bool UseDocUnwarping
    {
        get => _useDocUnwarping;
        set => SetOpt(ref _useDocUnwarping, value);
    }

    private bool _useLayoutDetection = true;
    public bool UseLayoutDetection
    {
        get => _useLayoutDetection;
        set => SetOpt(ref _useLayoutDetection, value);
    }

    private bool _useChartRecognition;
    public bool UseChartRecognition
    {
        get => _useChartRecognition;
        set => SetOpt(ref _useChartRecognition, value);
    }

    private bool _useSealRecognition = true;
    public bool UseSealRecognition
    {
        get => _useSealRecognition;
        set => SetOpt(ref _useSealRecognition, value);
    }

    private bool _useOcrForImageBlock;
    public bool UseOcrForImageBlock
    {
        get => _useOcrForImageBlock;
        set => SetOpt(ref _useOcrForImageBlock, value);
    }

    private bool _mergeTables = true;
    public bool MergeTables
    {
        get => _mergeTables;
        set => SetOpt(ref _mergeTables, value);
    }

    private bool _relevelTitles = true;
    public bool RelevelTitles
    {
        get => _relevelTitles;
        set => SetOpt(ref _relevelTitles, value);
    }

    private bool _layoutNms = true;
    public bool LayoutNms
    {
        get => _layoutNms;
        set => SetOpt(ref _layoutNms, value);
    }

    private bool _restructurePages = true;
    public bool RestructurePages
    {
        get => _restructurePages;
        set => SetOpt(ref _restructurePages, value);
    }

    private string _layoutShapeMode = "auto";
    public string LayoutShapeMode
    {
        get => _layoutShapeMode;
        set => SetOpt(ref _layoutShapeMode, value);
    }

    private string _promptLabel = "ocr";
    public string PromptLabel
    {
        get => _promptLabel;
        set => SetOpt(ref _promptLabel, value);
    }

    private string _repetitionPenalty = "1";
    public string RepetitionPenalty
    {
        get => _repetitionPenalty;
        set => SetOpt(ref _repetitionPenalty, value);
    }

    private string _temperature = "0";
    public string Temperature
    {
        get => _temperature;
        set => SetOpt(ref _temperature, value);
    }

    private string _topP = "1";
    public string TopP
    {
        get => _topP;
        set => SetOpt(ref _topP, value);
    }

    private string _minPixels = "147384";
    public string MinPixels
    {
        get => _minPixels;
        set => SetOpt(ref _minPixels, value);
    }

    private string _maxPixels = "2822400";
    public string MaxPixels
    {
        get => _maxPixels;
        set => SetOpt(ref _maxPixels, value);
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckUpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyDefaultPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyGeneralPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyRubbingPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> UnlockServerTapCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmUnlockCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelUnlockCommand { get; }

    public SettingsViewModel(ISettingsService settings, IApiService api, IVersionService version)
    {
        _settings = settings;
        _api = api;
        _version = version;

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        CheckUpdateCommand = ReactiveCommand.CreateFromTask(CheckUpdateAsync);
        ApplyDefaultPresetCommand = ReactiveCommand.Create(() => ApplyPreset(OcrVlOptions.PresetDefault));
        ApplyGeneralPresetCommand = ReactiveCommand.Create(() => ApplyPreset(OcrVlOptions.PresetGeneral));
        ApplyRubbingPresetCommand = ReactiveCommand.Create(() => ApplyPreset(OcrVlOptions.PresetRubbing));
        UnlockServerTapCommand = ReactiveCommand.Create(OnUnlockServerTap);
        ConfirmUnlockCommand = ReactiveCommand.Create(ConfirmUnlock);
        CancelUnlockCommand = ReactiveCommand.Create(CancelUnlock);

        _ = LoadAsync();
    }

    private void OnUnlockServerTap()
    {
        if (IsServerUnlocked)
            return;

        var now = DateTime.UtcNow;
        _unlockTaps.RemoveAll(t => now - t > UnlockTapWindow);
        _unlockTaps.Add(now);

        if (_unlockTaps.Count < UnlockTapRequired)
            return;

        _unlockTaps.Clear();
        UnlockPassword = "";
        UnlockError = null;
        ShowUnlockPrompt = true;
    }

    private void ConfirmUnlock()
    {
        if (UnlockPassword == UnlockPasswordValue)
        {
            IsServerUnlocked = true;
            ShowUnlockPrompt = false;
            UnlockPassword = "";
            UnlockError = null;
            _unlockTaps.Clear();
            StatusMessage = "服务器设置已解锁";
            return;
        }

        UnlockError = "密码错误";
        UnlockPassword = "";
        _unlockTaps.Clear();
    }

    private void CancelUnlock()
    {
        ShowUnlockPrompt = false;
        UnlockPassword = "";
        UnlockError = null;
        _unlockTaps.Clear();
    }

    private void SetOpt<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        this.RaiseAndSetIfChanged(ref field, value);
        if (!_suppressCustomMark && OcrPreset != OcrVlOptions.PresetCustom)
            OcrPreset = OcrVlOptions.PresetCustom;
    }

    private void ApplyPreset(string preset)
    {
        _suppressCustomMark = true;
        try
        {
            ApplyOptionsDict(OcrVlOptions.FromPreset(preset));
            OcrPreset = preset;
            StatusMessage = $"已套用「{OcrVlOptions.PresetDisplayName(preset)}」推荐配置（需点保存生效）";
        }
        finally { _suppressCustomMark = false; }
    }

    private void ApplyOptionsDict(Dictionary<string, object> o)
    {
        // 表单用默认补齐缺省键，便于编辑；命名预设保存时仍写精确字典
        var display = OcrVlOptions.CreateDefault();
        foreach (var (k, v) in o)
            display[k] = v;

        _useDocOrientationClassify = false; // 方向分类固定关
        _useDocUnwarping = OcrVlOptions.GetBool(display, "useDocUnwarping");
        _useLayoutDetection = OcrVlOptions.GetBool(display, "useLayoutDetection", true);
        _useChartRecognition = OcrVlOptions.GetBool(display, "useChartRecognition");
        _useSealRecognition = OcrVlOptions.GetBool(display, "useSealRecognition", true);
        _useOcrForImageBlock = OcrVlOptions.GetBool(display, "useOcrForImageBlock");
        _mergeTables = OcrVlOptions.GetBool(display, "mergeTables", true);
        _relevelTitles = OcrVlOptions.GetBool(display, "relevelTitles", true);
        _layoutNms = OcrVlOptions.GetBool(display, "layoutNms", true);
        _restructurePages = OcrVlOptions.GetBool(display, "restructurePages", true);
        _layoutShapeMode = OcrVlOptions.GetString(display, "layoutShapeMode", "auto");
        _promptLabel = OcrVlOptions.GetString(display, "promptLabel", "ocr");
        _repetitionPenalty = FormatNum(OcrVlOptions.GetDouble(display, "repetitionPenalty", 1));
        _temperature = FormatNum(OcrVlOptions.GetDouble(display, "temperature", 0));
        _topP = FormatNum(OcrVlOptions.GetDouble(display, "topP", 1));
        _minPixels = OcrVlOptions.GetLong(display, "minPixels", 147384).ToString();
        _maxPixels = OcrVlOptions.GetLong(display, "maxPixels", 2822400).ToString();

        this.RaisePropertyChanged(nameof(UseDocOrientationClassify));
        this.RaisePropertyChanged(nameof(UseDocUnwarping));
        this.RaisePropertyChanged(nameof(UseLayoutDetection));
        this.RaisePropertyChanged(nameof(UseChartRecognition));
        this.RaisePropertyChanged(nameof(UseSealRecognition));
        this.RaisePropertyChanged(nameof(UseOcrForImageBlock));
        this.RaisePropertyChanged(nameof(MergeTables));
        this.RaisePropertyChanged(nameof(RelevelTitles));
        this.RaisePropertyChanged(nameof(LayoutNms));
        this.RaisePropertyChanged(nameof(RestructurePages));
        this.RaisePropertyChanged(nameof(LayoutShapeMode));
        this.RaisePropertyChanged(nameof(PromptLabel));
        this.RaisePropertyChanged(nameof(RepetitionPenalty));
        this.RaisePropertyChanged(nameof(Temperature));
        this.RaisePropertyChanged(nameof(TopP));
        this.RaisePropertyChanged(nameof(MinPixels));
        this.RaisePropertyChanged(nameof(MaxPixels));
    }

    private static string FormatNum(double v)
        => Math.Abs(v - Math.Round(v)) < 1e-9
            ? ((long)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

    private Dictionary<string, object> BuildOptionsFromUi()
    {
        if (OcrPreset is OcrVlOptions.PresetDefault
            or OcrVlOptions.PresetGeneral
            or OcrVlOptions.PresetRubbing)
            return OcrVlOptions.FromPreset(OcrPreset);

        var d = new Dictionary<string, object>
        {
            ["useDocOrientationClassify"] = _useDocOrientationClassify,
            ["useDocUnwarping"] = _useDocUnwarping,
            ["useLayoutDetection"] = _useLayoutDetection,
            ["useChartRecognition"] = _useChartRecognition,
            ["useSealRecognition"] = _useSealRecognition,
            ["useOcrForImageBlock"] = _useOcrForImageBlock,
            ["mergeTables"] = _mergeTables,
            ["relevelTitles"] = _relevelTitles,
            ["layoutShapeMode"] = string.IsNullOrWhiteSpace(_layoutShapeMode) ? "auto" : _layoutShapeMode.Trim(),
            ["promptLabel"] = string.IsNullOrWhiteSpace(_promptLabel) ? "ocr" : _promptLabel.Trim(),
            ["layoutNms"] = _layoutNms,
            ["restructurePages"] = _restructurePages,
        };

        if (double.TryParse(_repetitionPenalty, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rp))
            d["repetitionPenalty"] = rp;
        if (double.TryParse(_temperature, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var temp))
            d["temperature"] = temp;
        if (double.TryParse(_topP, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var topP))
            d["topP"] = topP;
        if (long.TryParse(_minPixels, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var minP))
            d["minPixels"] = minP;
        if (long.TryParse(_maxPixels, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var maxP))
            d["maxPixels"] = maxP;
        return OcrVlOptions.Sanitize(d);
    }

    private async Task LoadAsync()
    {
        ServerAddress = await _settings.GetServerUrlAsync();
        CurrentServerUrl = _api.BaseUrl ?? "(未配置)";
        var (preset, opts) = await OcrVlOptions.LoadAsync(_settings);
        _suppressCustomMark = true;
        try
        {
            ApplyOptionsDict(opts.Count > 0 ? opts : OcrVlOptions.FromPreset(preset));
            OcrPreset = preset;
        }
        finally { _suppressCustomMark = false; }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            await _settings.SetServerUrlAsync(ServerAddress);
            _api.BaseUrl = ServerAddress;
            await OcrVlOptions.SaveAsync(_settings, OcrPreset, BuildOptionsFromUi());
            StatusMessage = $"设置已保存（识别方案：{OcrVlOptions.PresetDisplayName(OcrPreset)}）";
        }
        catch (Exception ex) { StatusMessage = $"保存失败: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        ConnectionOk = false;
        StatusMessage = "正在测试...";
        try
        {
            var tempUrl = ServerAddress.TrimEnd('/');
            var url = $"{tempUrl}/health";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync(url);
            ConnectionOk = resp.IsSuccessStatusCode;
            StatusMessage = ConnectionOk ? "✓ 连接成功！服务正常运行" : $"✗ 连接失败: HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            ConnectionOk = false;
            StatusMessage = $"✗ 连接失败: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task CheckUpdateAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _version.CheckUpdateAsync();
            if (result.HasUpdate)
            {
                VersionInfo = $"新版本: {result.LatestVersion}"
                    + (result.ForceUpdate ? " (强制更新)" : "");
                StatusMessage = string.IsNullOrEmpty(result.Changelog)
                    ? "发现新版本！" : $"更新日志:\n{result.Changelog}";
            }
            else
            {
                VersionInfo = $"当前版本: {Constants.AppVersion} (已是最新)";
                StatusMessage = "已是最新版本";
            }
        }
        catch (Exception ex) { StatusMessage = $"检查更新失败: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}
