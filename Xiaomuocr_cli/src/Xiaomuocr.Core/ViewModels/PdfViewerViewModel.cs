using ReactiveUI;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class PdfViewerViewModel : ViewModelBase
{
    /// <summary>允许极小缩放，以适配过万点宽的拓片/扫描件横向铺满。</summary>
    private const float MinZoom = 0.01f;
    private const float MaxZoom = 5.0f;
    /// <summary>单边渲染上限（像素），避免超大页高倍放大 OOM。</summary>
    private const int MaxRenderEdgePx = 8192;

    private readonly IPdfRenderService _pdfRender;
    private readonly IOcrResultService _ocrResult;
    private readonly IPdfOcrPipelineService? _pdfOcr;
    private readonly IAppLogService? _appLog;
    private readonly ILibraryService? _libraryService;
    private readonly IHighlightService? _highlightService;
    private readonly ISearchService? _searchService;
    private readonly IFullTextExportService? _exportService;
    private readonly INoteService? _noteService;
    private readonly IPricingService? _pricingService;
    private readonly ISettingsService? _settingsService;
    private readonly LibraryItem? _libraryItem;
    private double _cachedPageW = 595, _cachedPageH = 842; // 当前页尺寸（翻页时更新）
    private (double Width, double Height)[] _pageSizes = Array.Empty<(double, double)>();

    // ---- PDF 文档信息 ----
    private string _pdfPath = "";
    public string PdfPath { get => _pdfPath; set => this.RaiseAndSetIfChanged(ref _pdfPath, value); }

    private string _documentName = "";
    public string DocumentName { get => _documentName; set => this.RaiseAndSetIfChanged(ref _documentName, value); }

    private string _jsonDir = "";
    public string JsonDir { get => _jsonDir; set => this.RaiseAndSetIfChanged(ref _jsonDir, value); }

    private int _currentPage;
    /// <summary>0-based 内部页码</summary>
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentPage, value);
            this.RaisePropertyChanged(nameof(CurrentPageDisplay));
        }
    }

    /// <summary>1-based 展示页码（工具栏 1/2）</summary>
    public int CurrentPageDisplay => CurrentPage + 1;

    private bool _pageLoaded; // 防止渲染时 PDF 未就绪

    private int _totalPages;
    public int TotalPages
    {
        get => _totalPages;
        set => this.RaiseAndSetIfChanged(ref _totalPages, value);
    }

    // ---- 显示状态 ----
    private float _zoom = 1.0f;
    public float Zoom
    {
        get => _zoom;
        set
        {
            float clamped = Math.Clamp(value, MinZoom, EffectiveMaxZoom());
            bool changed = Math.Abs(_zoom - clamped) > 0.0005f;
            _zoom = clamped;
            this.RaisePropertyChanged();
            if (changed && _pageLoaded) _ = RenderDisplayAsync();
        }
    }

    private int _rotation;
    public int Rotation
    {
        get => _rotation;
        set
        {
            bool changed = _rotation != value;
            _rotation = value;
            this.RaisePropertyChanged();
            if (changed && _pageLoaded) _ = RenderDisplayAsync();
        }
    }

    private byte[]? _displayImage;
    public byte[]? DisplayImage
    {
        get => _displayImage;
        set => this.RaiseAndSetIfChanged(ref _displayImage, value);
    }

    private int _displayWidth;
    public int DisplayWidth
    {
        get => _displayWidth;
        set => this.RaiseAndSetIfChanged(ref _displayWidth, value);
    }

    private int _displayHeight;
    public int DisplayHeight
    {
        get => _displayHeight;
        set => this.RaiseAndSetIfChanged(ref _displayHeight, value);
    }

    // ---- OCR 数据 ----
    private PreprocessInfo? _preprocess;
    public PreprocessInfo? Preprocess
    {
        get => _preprocess;
        set => this.RaiseAndSetIfChanged(ref _preprocess, value);
    }

    /// <summary>OCR 文本块列表 (用于 bbox 绘制)</summary>
    public ObservableCollection<OcrTextBlockViewModel> Blocks { get; } = new();

    /// <summary>左侧页码导航列表</summary>
    public ObservableCollection<PdfPageNavItem> PageNavItems { get; } = new();

    /// <summary>当前文档的高亮数据（pageIndex → highlights）</summary>
    private Dictionary<int, List<HighlightEntry>> _docHighlights;

    private int _selectedBlockIndex = -1;
    public int SelectedBlockIndex
    {
        get => _selectedBlockIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedBlockIndex, value);
            this.RaisePropertyChanged(nameof(SelectedBlockText));
        }
    }

    /// <summary>当前选中的文本块内容</summary>
    public string SelectedBlockText =>
        SelectedBlockIndex >= 0 && SelectedBlockIndex < Blocks.Count
            ? Blocks[SelectedBlockIndex].Text : "";

    // ---- 文本编辑 ----
    private string _fullText = "";
    public string FullText
    {
        get => _fullText;
        set => this.RaiseAndSetIfChanged(ref _fullText, value);
    }

    private bool _showConverted;
    public bool ShowConverted
    {
        get => _showConverted;
        set
        {
            if (_showConverted == value) return;
            // 切换前按「当前模式」提交编辑框（未改动的简体/句读预览不会覆盖原文）
            foreach (var b in Blocks)
                b.CommitEditorToText(_showConverted, ConvertToSimplified, GetPunctuatedBase(b));
            _showConverted = value;
            this.RaisePropertyChanged();
            _ = UpdateTextDisplayAsync();
        }
    }

    private string _convertBtnLabel = "繁→简";
    public string ConvertBtnLabel
    {
        get => _convertBtnLabel;
        set => this.RaiseAndSetIfChanged(ref _convertBtnLabel, value);
    }

    private bool _showPunctuated;
    /// <summary>会话内自动句读预览；默认 false，不持久化。</summary>
    public bool ShowPunctuated
    {
        get => _showPunctuated;
        set
        {
            if (_showPunctuated == value) return;
            foreach (var b in Blocks)
                b.CommitEditorToText(ShowConverted, ConvertToSimplified, GetPunctuatedBase(b));
            _showPunctuated = value;
            this.RaisePropertyChanged();
            PunctuateBtnLabel = value ? "原文" : "自动句逗";
            if (value)
                _ = EnsurePunctuateCurrentPageAsync();
            else
                _ = UpdateTextDisplayAsync();
        }
    }

    private string _punctuateBtnLabel = "自动句逗";
    public string PunctuateBtnLabel
    {
        get => _punctuateBtnLabel;
        set => this.RaiseAndSetIfChanged(ref _punctuateBtnLabel, value);
    }

    private bool _isPunctuating;
    public bool IsPunctuating
    {
        get => _isPunctuating;
        set => this.RaiseAndSetIfChanged(ref _isPunctuating, value);
    }

    private bool _isExporting;
    /// <summary>正在写入导出文件（不含选项/保存对话框），用于禁用「导出全文」并显示进度。</summary>
    public bool IsExporting
    {
        get => _isExporting;
        set => this.RaiseAndSetIfChanged(ref _isExporting, value);
    }

    private CancellationTokenSource? _exportCts;

    /// <summary>会话内句读缓存：key = pageIndex:SourceKind:SourceIndex:textHash</summary>
    private readonly Dictionary<string, string> _punctuateCache = new(StringComparer.Ordinal);
    /// <summary>已向服务端请求过的块（含返回值与原文相同的情况），避免反复打接口。</summary>
    private readonly HashSet<string> _punctuateFetchedKeys = new(StringComparer.Ordinal);

    // ---- OCR ----
    private bool _isOcrRunning;
    public bool IsOcrRunning
    {
        get => _isOcrRunning;
        set => this.RaiseAndSetIfChanged(ref _isOcrRunning, value);
    }

    private string _rangeFrom = "1";
    public string RangeFrom
    {
        get => _rangeFrom;
        set => this.RaiseAndSetIfChanged(ref _rangeFrom, value);
    }

    private string _rangeTo = "1";
    public string RangeTo
    {
        get => _rangeTo;
        set => this.RaiseAndSetIfChanged(ref _rangeTo, value);
    }

    private bool _isOffline;
    public bool IsOffline
    {
        get => _isOffline;
        set => this.RaiseAndSetIfChanged(ref _isOffline, value);
    }

    private bool _isRubbingAvailable = true;
    /// <summary>服务端是否有支持参数的 OCR 通道（拓片为参数预设）；不可用时拓片按钮置灰。</summary>
    public bool IsRubbingAvailable
    {
        get => _isRubbingAvailable;
        set => this.RaiseAndSetIfChanged(ref _isRubbingAvailable, value);
    }

    // ---- 批量处理 ----
    private bool _isBatchProcessing;
    public bool IsBatchProcessing
    {
        get => _isBatchProcessing;
        set => this.RaiseAndSetIfChanged(ref _isBatchProcessing, value);
    }

    private string _batchProgressText = "";
    public string BatchProgressText
    {
        get => _batchProgressText;
        set => this.RaiseAndSetIfChanged(ref _batchProgressText, value);
    }

    // ---- 命令 ----
    public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> RotateLeftCommand { get; }
    public ReactiveCommand<Unit, Unit> RotateRightCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveTextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleConvertCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePunctuateCommand { get; }
    public ReactiveCommand<Unit, Unit> OcrCurrentPageCommand { get; }
    public ReactiveCommand<Unit, Unit> OcrAllPagesCommand { get; }
    public ReactiveCommand<Unit, Unit> OcrRangeCommand { get; }
    public ReactiveCommand<Unit, Unit> OcrCurrentPageRubbingCommand { get; }
    public ReactiveCommand<Unit, Unit> OcrAllPagesRubbingCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchOcrAllPagesCommand { get; }
    public ReactiveCommand<Unit, Unit> BatchOcrRangeCommand { get; }
    public ReactiveCommand<int, Unit> GoToPageCommand { get; }

    // ---- 高亮 / 搜索 / 导出 / 笔记 ----
    public ReactiveCommand<Unit, Unit> AddHighlightCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearHighlightsCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> HighlightListCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> NoteCommand { get; }

    /// <summary>文本块被点击 (从文本面板)</summary>
    public event Action<int>? TextBlockClicked;

    /// <summary>请求视图测量 PDF 区域宽度，用于横向铺满</summary>
    public event Action? FitToWidthRequested;

    /// <summary>OCR 进度（供主窗口状态栏）</summary>
    public event Action<string>? OcrStatusChanged;

    /// <summary>视图注入：打开搜索对话框，返回跳转目标（null=关闭/取消）</summary>
    public Func<Task<(int pageIndex, int blockIndex)?>>? OpenSearchDialogAsync { get; set; }

    /// <summary>视图注入：批量 OCR 服务（由 MainWindow 设置）。</summary>
    public IBatchUploadService? BatchUploadSvc { get; set; }
    public IBatchOcrService? BatchOcrSvc { get; set; }
    public IBatchCacheService? BatchCacheSvc { get; set; }
    public ITaskSyncService? TaskSyncSvc { get; set; }
    public IUploadJobQueue? UploadJobQueue { get; set; }
    public IApiService? ApiSvc { get; set; }
    public IPunctuateService? PunctuateSvc { get; set; }
    /// <summary>视图注入：UI 线程调度器。</summary>
    public Action<Action>? PostToUiThread { get; set; }

    /// <summary>视图注入：打开笔记对话框。</summary>
    public Func<Task>? OpenNoteDialogAsync { get; set; }

    /// <summary>视图注入：批量 OCR 确认对话框 (title, message) -> true=确认</summary>
    public Func<string, string, Task<bool>>? ShowBatchOcrConfirmAsync { get; set; }

    /// <summary>视图注入：提交后跳转任务中心。</summary>
    public Action<string?>? NavigateToTaskCenter { get; set; }

    /// <summary>视图注入：打开高亮列表对话框，返回跳转目标（null=关闭/取消）</summary>
    public Func<Task<(int pageIndex, int blockIndex)?>>? OpenHighlightListDialogAsync { get; set; }

    private double _textFontSize = 13;
    /// <summary>PDF 文本区域的字体大小（8-48pt，持久化到设置）。</summary>
    public double TextFontSize
    {
        get => _textFontSize;
        set
        {
            this.RaiseAndSetIfChanged(ref _textFontSize, value);
            _ = PersistFontSizeAsync();
        }
    }

    /// <summary>供 View 层记录调试日志（写入 xiaomuocr_client.log，实时刷新）。</summary>
    public void LogDebug(string message) => _appLog?.Info(message);

    /// <summary>请求视图触发文件保存对话框（导出全文）：documentName, fileName, defaultExtension</summary>
    public event Func<string, string, string, Task<string?>>? SaveFileDialogRequested;

    /// <summary>视图注入：打开全文导出选项弹窗。</summary>
    public Func<int, Task<FullTextExportOptions?>>? OpenExportDialogAsync { get; set; }

    private TaskCompletionSource<double>? _fitWidthTcs;

    public PdfViewerViewModel(
        IPdfRenderService pdfRender,
        IOcrResultService ocrResult,
        IAppLogService? appLog = null)
        : this(pdfRender, ocrResult, null, null, null, null, null, null, appLog)
    {
    }

    public PdfViewerViewModel(
        IPdfRenderService pdfRender,
        IOcrResultService ocrResult,
        IPdfOcrPipelineService? pdfOcr,
        LibraryItem? libraryItem,
        IAppLogService? appLog = null)
        : this(pdfRender, ocrResult, pdfOcr, libraryItem, null, null, null, null, appLog)
    {
    }

    public PdfViewerViewModel(
        IPdfRenderService pdfRender,
        IOcrResultService ocrResult,
        IPdfOcrPipelineService? pdfOcr,
        LibraryItem? libraryItem,
        IHighlightService? highlightService,
        ISearchService? searchService,
        IFullTextExportService? exportService,
        INoteService? noteService,
        IAppLogService? appLog = null,
        ILibraryService? libraryService = null,
        IPricingService? pricingService = null,
        ISettingsService? settingsService = null)
    {
        _pdfRender = pdfRender;
        _ocrResult = ocrResult;
        _pdfOcr = pdfOcr;
        _libraryItem = libraryItem;
        _highlightService = highlightService;
        _searchService = searchService;
        _exportService = exportService;
        _noteService = noteService;
        _appLog = appLog;
        _libraryService = libraryService;
        _pricingService = pricingService;
        _settingsService = settingsService;

        _docHighlights = new Dictionary<int, List<HighlightEntry>>();

        // 加载持久化设置
        _ = LoadFontSizeAsync();

        var canPrev = this.WhenAnyValue(x => x.CurrentPage, p => p > 0);
        var canNext = this.WhenAnyValue(x => x.CurrentPage, x => x.TotalPages, (p, t) => p < t - 1);
        var canOcr = this.WhenAnyValue(
            x => x.IsOcrRunning, x => x.IsBusy, x => x.TotalPages, x => x.PdfPath, x => x.IsOffline,
            (ocr, busy, pages, path, offline) => !ocr && !busy && !offline && pages > 0 && !string.IsNullOrEmpty(path) && _pdfOcr != null && _libraryItem != null);
        var canOcrRubbing = this.WhenAnyValue(
            x => x.IsOcrRunning, x => x.IsBusy, x => x.TotalPages, x => x.PdfPath, x => x.IsOffline, x => x.IsRubbingAvailable,
            (ocr, busy, pages, path, offline, rubbing) =>
                !ocr && !busy && !offline && rubbing && pages > 0 && !string.IsNullOrEmpty(path)
                && _pdfOcr != null && _libraryItem != null);

        PrevPageCommand = ReactiveCommand.CreateFromTask(PrevPageAsync, canPrev);
        NextPageCommand = ReactiveCommand.CreateFromTask(NextPageAsync, canNext);
        // 乘除步进：大页 fit 后可能只有 5%–15%，固定 ±0.25 会导致缩小无效、放大过猛
        ZoomInCommand = ReactiveCommand.Create(() => { Zoom *= 1.25f; });
        ZoomOutCommand = ReactiveCommand.Create(() => { Zoom /= 1.25f; });
        RotateLeftCommand = ReactiveCommand.Create(() => { Rotation = (Rotation - 90) % 360; });
        RotateRightCommand = ReactiveCommand.Create(() => { Rotation = (Rotation + 90) % 360; });
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { TextFontSize = Math.Min(TextFontSize + 1, 48); });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { TextFontSize = Math.Max(TextFontSize - 1, 8); });
        SaveTextCommand = ReactiveCommand.CreateFromTask(SaveTextAsync);
        ToggleConvertCommand = ReactiveCommand.Create(() =>
        {
            ShowConverted = !ShowConverted;
            ConvertBtnLabel = ShowConverted ? "原文" : "繁→简";
        });
        TogglePunctuateCommand = ReactiveCommand.Create(() =>
        {
            ShowPunctuated = !ShowPunctuated;
        });
        OcrCurrentPageCommand = ReactiveCommand.CreateFromTask(OcrCurrentPageAsync, canOcr);
        OcrAllPagesCommand = ReactiveCommand.CreateFromTask(OcrAllPagesAsync, canOcr);
        OcrRangeCommand = ReactiveCommand.CreateFromTask(OcrRangeAsync, canOcr);
        OcrCurrentPageRubbingCommand = ReactiveCommand.CreateFromTask(OcrCurrentPageRubbingAsync, canOcrRubbing);
        OcrAllPagesRubbingCommand = ReactiveCommand.CreateFromTask(OcrAllPagesRubbingAsync, canOcrRubbing);

        var canBatchOcr = this.WhenAnyValue(
            x => x.IsBatchProcessing, x => x.IsBusy, x => x.TotalPages, x => x.PdfPath, x => x.IsOffline,
            (batch, busy, pages, path, offline) => !batch && !busy && !offline && pages > 0 && !string.IsNullOrEmpty(path) && _libraryItem != null);
        BatchOcrAllPagesCommand = ReactiveCommand.CreateFromTask(BatchOcrAllPagesAsync, canBatchOcr);
        BatchOcrRangeCommand = ReactiveCommand.CreateFromTask(BatchOcrRangeAsync, canBatchOcr);

        GoToPageCommand = ReactiveCommand.CreateFromTask<int>(GoToPageAsync);

        // 高亮 / 搜索 / 导出 / 笔记命令
        AddHighlightCommand = ReactiveCommand.CreateFromTask(AddHighlightAsync);
        ClearHighlightsCommand = ReactiveCommand.CreateFromTask(ClearHighlightsAsync);
        SearchCommand = ReactiveCommand.CreateFromTask(OpenSearchAsync);
        HighlightListCommand = ReactiveCommand.CreateFromTask(OpenHighlightListAsync);
        // 不用 CreateFromTask 包住整个流程：否则选项弹窗期间工具栏「导出全文」会一直灰色像卡死。
        // 真正写文件时用 IsExporting 禁用按钮。
        ExportCommand = ReactiveCommand.Create(
            () => { _ = ExportFullTextGuardedAsync(); },
            this.WhenAnyValue(x => x.IsExporting, exporting => !exporting));
        NoteCommand = ReactiveCommand.CreateFromTask(OpenNoteAsync);
    }

    /// <summary>打开 PDF 并加载第一页</summary>
    public async Task OpenDocumentAsync(string pdfPath, string? jsonDir = null)
    {
        System.Diagnostics.Debug.WriteLine($"[PdfVM] OpenDocumentAsync: {pdfPath}");
        _pageLoaded = false;
        IsBusy = true;
        ClearError();
        StatusMessage = "正在加载 PDF...";
        _appLog?.Info($"打开文档: {System.IO.Path.GetFileName(pdfPath)}");
        try
        {
            var info = await _pdfRender.OpenPdfAsync(pdfPath);
            System.Diagnostics.Debug.WriteLine($"[PdfVM] PDF opened: {info.TotalPages} pages, {info.PageWidth}x{info.PageHeight}");
            _appLog?.Info($"PDF 已加载: {info.TotalPages} 页, 首页尺寸 {info.PageWidth:F0}x{info.PageHeight:F0}");

            _pageSizes = info.PageSizes.Count > 0
                ? info.PageSizes.ToArray()
                : new[] { (info.PageWidth, info.PageHeight) };
            SyncCachedPageSize(0);

            PdfPath = pdfPath;
            DocumentName = System.IO.Path.GetFileNameWithoutExtension(pdfPath);
            TotalPages = info.TotalPages;
            RangeFrom = "1";
            RangeTo = Math.Max(1, info.TotalPages).ToString();
            JsonDir = jsonDir ?? System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(pdfPath) ?? "",
                System.IO.Path.GetFileNameWithoutExtension(pdfPath));
            RebuildPageNavItems();

            if (_libraryItem != null)
            {
                _libraryItem.TotalPages = info.TotalPages;
                if (string.IsNullOrEmpty(_libraryItem.OutputDir))
                    _libraryItem.OutputDir = JsonDir;
                if (string.IsNullOrEmpty(_libraryItem.PdfPath))
                    _libraryItem.PdfPath = pdfPath;
            }

            // 先设置不触发渲染的属性；Zoom 由横向铺满计算，翻页时保持不变
            _rotation = 0; this.RaisePropertyChanged(nameof(Rotation));
            _currentPage = 0;
            this.RaisePropertyChanged(nameof(CurrentPage));
            this.RaisePropertyChanged(nameof(CurrentPageDisplay));
            UpdatePageNavCurrent();
            _pageLoaded = true;

            StatusMessage = $"已加载 {info.TotalPages} 页, 正在适配宽度...";
            _appLog?.Info("计算横向铺满缩放...");
            _ = RefreshRubbingAvailabilityAsync();
            var viewportW = await RequestViewportWidthAsync();
            ApplyFitWidthZoom(viewportW);
            _appLog?.Info($"横向铺满 zoom={Zoom:F3} (viewport={viewportW:F0}, pageW={_cachedPageW:F0})");

            StatusMessage = $"已加载 {info.TotalPages} 页, 正在渲染...";
            _appLog?.Info("开始渲染首页...");
            await RenderDisplayAsync();
            var msg = $"第 1 页渲染完成: {DisplayWidth}x{DisplayHeight}, {DisplayImage?.Length ?? 0} bytes";
            System.Diagnostics.Debug.WriteLine($"[PdfVM] {msg}");
            _appLog?.Info(msg);

            StatusMessage = "正在加载 OCR 文本...";
            _appLog?.Info("开始加载 OCR 结果...");
            await LoadPageAsync();
            await LoadHighlightsAsync();
            _appLog?.Info($"OCR 加载完成: {Blocks.Count} 块, 高亮: {_docHighlights.Sum(kv => kv.Value.Count)} 条");
            StatusMessage = $"渲染完成 ({DisplayWidth}x{DisplayHeight}), {Blocks.Count} 文本块";
            _appLog?.Info("打开文档流程全部完成");
        }
        catch (Exception ex)
        {
            var err = $"打开文档失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[PdfVM] ERROR: {err}\n{ex.StackTrace}");
            _appLog?.Error(err);
            ErrorMessage = err;
        }
        finally
        {
            IsBusy = false;
            _pageLoaded = true;
        }
    }

    /// <summary>视图测得 PDF 可视区域宽度后回调</summary>
    public void ProvideViewportWidthForFit(double width)
        => _fitWidthTcs?.TrySetResult(width);

    /// <summary>切换到指定页的物理尺寸（异形页各自独立）。</summary>
    private void SyncCachedPageSize(int pageIndex0Based)
    {
        if (_pageSizes.Length > 0)
        {
            var idx = Math.Clamp(pageIndex0Based, 0, _pageSizes.Length - 1);
            _cachedPageW = _pageSizes[idx].Width;
            _cachedPageH = _pageSizes[idx].Height;
        }
        else if (_pdfRender != null)
        {
            var (w, h) = _pdfRender.GetOpenedPageSize(pageIndex0Based);
            _cachedPageW = w;
            _cachedPageH = h;
        }
        if (_cachedPageW < 1) _cachedPageW = 595;
        if (_cachedPageH < 1) _cachedPageH = 842;
    }

    private async Task<double> RequestViewportWidthAsync()
    {
        _fitWidthTcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        FitToWidthRequested?.Invoke();
        try
        {
            return await _fitWidthTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            _appLog?.Warn("等待视口宽度超时，使用默认 800");
            return 800;
        }
    }

    /// <summary>按可视宽度计算横向铺满缩放（不触发渲染）</summary>
    private void ApplyFitWidthZoom(double viewportWidth)
    {
        double pageW = (Rotation % 180 != 0) ? _cachedPageH : _cachedPageW;
        if (pageW < 1) pageW = 595;
        float z = (float)(Math.Max(viewportWidth - 8, 40) / pageW);
        _zoom = Math.Clamp(z, MinZoom, EffectiveMaxZoom());
        this.RaisePropertyChanged(nameof(Zoom));
    }

    /// <summary>按页面物理尺寸限制最大缩放，防止渲染位图过大。</summary>
    private float EffectiveMaxZoom()
    {
        double pageMax = Math.Max(_cachedPageW, _cachedPageH);
        if (pageMax < 1) return MaxZoom;
        float byEdge = MaxRenderEdgePx / (float)pageMax;
        return Math.Clamp(byEdge, MinZoom, MaxZoom);
    }

    public async Task LoadPageAsync()
    {
        await LoadPageAtIndexAsync(CurrentPage);
    }

    /// <summary>加载指定页 OCR 结果到文本列表与 bbox（pageIndex 为 0-based）</summary>
    public async Task LoadPageAtIndexAsync(int pageIndex)
    {
        Blocks.Clear();
        SelectedBlockIndex = -1;
        Preprocess = null;
        FullText = "";

        if (string.IsNullOrEmpty(JsonDir))
        {
            _appLog?.Warn("LoadPage: JsonDir 为空，跳过");
            return;
        }

        var jsonPath = System.IO.Path.Combine(JsonDir, $"page_{pageIndex + 1}_ocr_result.json");
        _appLog?.Info($"LoadPage: 第{pageIndex + 1}页 path={jsonPath} exists={File.Exists(jsonPath)}");

        try
        {
            SyncCachedPageSize(pageIndex);
            double pageW = _cachedPageW, pageH = _cachedPageH;
            var result = await _ocrResult.LoadPageResultAsync(
                JsonDir, pageIndex, pageW, pageH);
            if (result == null)
            {
                _appLog?.Warn($"LoadPage: 第{pageIndex + 1}页 无结果或解析失败");
                UpdateDisplayBboxes();
                return;
            }

            Preprocess = result.Preprocess;
            for (int i = 0; i < result.Blocks.Count; i++)
            {
                var b = result.Blocks[i];
                var full = b.Text ?? "";
                var vm = new OcrTextBlockViewModel
                {
                    RawBbox = b.Bbox ?? Array.Empty<double>(),
                    Label = b.Label,
                    Order = b.Order,
                    Index = i + 1,
                    SourceKind = b.SourceKind,
                    SourceIndex = b.SourceIndex,
                    DisplayBbox = Array.Empty<double>(),
                };
                vm.SetCanonicalText(full);
                Blocks.Add(vm);
            }

            await UpdateTextDisplayAsync();
            _applyPageHighlights();
            _appLog?.Info($"LoadPage: 第{pageIndex + 1}页 加载 {Blocks.Count} 块, scale={Preprocess?.ResizeScale:F4}, angle={Preprocess?.OrientationAngle ?? 0}, ocr={Preprocess?.OrientedWidth}x{Preprocess?.OrientedHeight}");
            if (ShowPunctuated)
                await EnsurePunctuateCurrentPageAsync();
        }
        catch (Exception ex)
        {
            _appLog?.Warn($"加载 OCR 失败: {ex.Message}");
            UpdateDisplayBboxes();
        }
    }

    private Task UpdateTextDisplayAsync()
    {
        void Apply()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Blocks.Count; i++)
            {
                Blocks[i].RefreshEditorText(ShowConverted, ConvertToSimplified, GetPunctuatedBase(Blocks[i]));
                var text = Blocks[i].EditorText;
                sb.AppendLine($"[{i + 1}]");
                sb.AppendLine(string.IsNullOrWhiteSpace(text) ? "(空)" : text.Trim());
                sb.AppendLine();
            }
            FullText = sb.ToString();
            UpdateDisplayBboxes();
        }

        // 句读等异步回调后必须在 UI 线程刷新，否则查看态块不更新（只有正在编辑的块看起来生效）
        if (PostToUiThread != null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PostToUiThread(() =>
            {
                try
                {
                    Apply();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return tcs.Task;
        }

        Apply();
        return Task.CompletedTask;
    }

    private static string BlockCacheKey(int pageIndex, OcrTextBlockViewModel b)
    {
        var text = b.Text ?? "";
        return $"{pageIndex}:{b.SourceKind}:{b.SourceIndex}:{text.GetHashCode()}";
    }

    private string BlockRequestId(OcrTextBlockViewModel b)
        => $"{b.SourceKind}:{b.SourceIndex}";

    private void ClearPunctuateCacheForPage(int pageIndex)
    {
        var prefix = $"{pageIndex}:";
        var keys = _punctuateCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var k in keys)
        {
            _punctuateCache.Remove(k);
            _punctuateFetchedKeys.Remove(k);
        }
        foreach (var k in _punctuateFetchedKeys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _punctuateFetchedKeys.Remove(k);
    }

    private string? GetPunctuatedBase(OcrTextBlockViewModel b)
    {
        if (!ShowPunctuated) return null;
        var key = BlockCacheKey(CurrentPage, b);
        return _punctuateCache.TryGetValue(key, out var cached) ? cached : null;
    }

    /// <summary>
    /// 仅正文类块做句读。黑名单跳过标题/页眉页脚/表格图片等；未知标签按正文处理。
    /// </summary>
    private static bool IsBodyTextBlockForPunctuate(OcrTextBlockViewModel b)
        => OcrBlockLabels.IsBodyLabel(b.Label);

    /// <summary>当前页缺缓存的块请求句读；有缓存则只刷新显示。</summary>
    private async Task EnsurePunctuateCurrentPageAsync()
    {
        if (!ShowPunctuated) return;
        // 防止连点并发打两次请求；仍刷新显示，避免切回「自动句读」时界面停在原文
        if (IsPunctuating)
        {
            await UpdateTextDisplayAsync();
            return;
        }
        if (IsOffline)
        {
            ErrorMessage = "离线模式下无法使用自动句逗";
            _showPunctuated = false;
            this.RaisePropertyChanged(nameof(ShowPunctuated));
            PunctuateBtnLabel = "自动句逗";
            await UpdateTextDisplayAsync();
            return;
        }
        if (PunctuateSvc == null)
        {
            ErrorMessage = "自动句逗服务未就绪";
            _showPunctuated = false;
            this.RaisePropertyChanged(nameof(ShowPunctuated));
            PunctuateBtnLabel = "自动句逗";
            await UpdateTextDisplayAsync();
            return;
        }
        if (Blocks.Count == 0)
        {
            await UpdateTextDisplayAsync();
            return;
        }

        var missing = new List<(string Id, string Text, string CacheKey)>();
        var skipped = 0;
        var cachedBody = 0;
        foreach (var b in Blocks)
        {
            var key = BlockCacheKey(CurrentPage, b);
            var raw = b.Text ?? "";

            // 标题等非正文：不请求句读，缓存原文以便预览态仍显示原文
            if (!IsBodyTextBlockForPunctuate(b))
            {
                if (!_punctuateCache.ContainsKey(key))
                    _punctuateCache[key] = raw;
                skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                _punctuateCache[key] = raw;
                continue;
            }

            // 已请求过或已有非原文缓存 → 复用；仅「等于原文」且未标记已请求的，可能是旧白名单误跳过，需重请求
            if (_punctuateCache.TryGetValue(key, out var cached))
            {
                if (_punctuateFetchedKeys.Contains(key)
                    || !string.Equals(cached, raw, StringComparison.Ordinal))
                {
                    cachedBody++;
                    continue;
                }
                _punctuateCache.Remove(key);
            }

            missing.Add((BlockRequestId(b), raw, key));
        }

        if (missing.Count == 0)
        {
            await UpdateTextDisplayAsync();
            if (cachedBody > 0)
            {
                StatusMessage = skipped > 0
                    ? $"已显示句逗缓存（正文 {cachedBody} 块，跳过非正文 {skipped}）"
                    : $"已显示句逗缓存（正文 {cachedBody} 块）";
            }
            else if (skipped > 0)
            {
                StatusMessage = $"本页无正文块需句逗（已跳过 {skipped} 个非正文块）";
            }
            return;
        }

        var pageAtStart = CurrentPage;
        IsPunctuating = true;
        StatusMessage = $"正在自动句逗正文（{missing.Count} 块" +
                        (skipped > 0 ? $"，跳过非正文 {skipped}" : "") +
                        (cachedBody > 0 ? $"，缓存 {cachedBody}" : "") +
                        "；首次加载模型可能需数分钟）…";
        try
        {
            var resp = await PunctuateSvc.PunctuatePageAsync(
                missing.Select(m => (m.Id, m.Text)),
                keepTraditional: null);
            if (pageAtStart != CurrentPage || !ShowPunctuated)
                return;

            var byId = (resp.Blocks ?? new List<PunctuateBlockResponse>())
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var (id, text, cacheKey) in missing)
            {
                if (byId.TryGetValue(id, out var item))
                {
                    _punctuateCache[cacheKey] = item.Text ?? text;
                    if (!string.IsNullOrEmpty(item.Error))
                        _appLog?.Warn($"句读块失败 {item.Id}: {item.Error}");
                }
                else
                {
                    _punctuateCache[cacheKey] = text;
                }
                _punctuateFetchedKeys.Add(cacheKey);
            }

            await UpdateTextDisplayAsync();
            ClearError();
            var bodyDone = missing.Count + cachedBody;
            StatusMessage = skipped > 0
                ? $"自动句逗完成（正文 {bodyDone} 块，跳过非正文 {skipped}）"
                : $"自动句逗完成（正文 {bodyDone} 块）";
            _appLog?.Info(
                $"自动句逗完成: page={pageAtStart + 1}, requested={missing.Count}, cached={cachedBody}, skipped={skipped}");
        }
        catch (Exception ex)
        {
            _appLog?.Warn($"自动句逗失败: {ex.Message}");
            ErrorMessage = $"自动句逗失败: {ex.Message}";
            if (ShowPunctuated)
            {
                _showPunctuated = false;
                this.RaisePropertyChanged(nameof(ShowPunctuated));
                PunctuateBtnLabel = "自动句逗";
            }
            await UpdateTextDisplayAsync();
        }
        finally
        {
            IsPunctuating = false;
        }
    }

    public async Task RenderDisplayAsync()
    {
        if (_pdfRender == null || string.IsNullOrEmpty(PdfPath)) return;

        try
        {
            SyncCachedPageSize(CurrentPage);
            _appLog?.Info(
                $"RenderDisplayAsync: page={CurrentPage + 1}, zoom={Zoom}, rot={Rotation}, " +
                $"pageSize={_cachedPageW:F0}x{_cachedPageH:F0}");
            var bytes = await _pdfRender.RenderPageForDisplayAsync(CurrentPage, Zoom, Rotation);
            _appLog?.Info($"RenderDisplayAsync: 收到 PNG {bytes.Length} bytes");

            bool swap = Rotation % 180 != 0;
            var expectW = (int)Math.Round((swap ? _cachedPageH : _cachedPageW) * Zoom);
            var expectH = (int)Math.Round((swap ? _cachedPageW : _cachedPageH) * Zoom);

            // 以实际 PNG 像素为准，避免异形页仍按首页尺寸裁切
            if (TryReadPngSize(bytes, out int pngW, out int pngH) && pngW > 0 && pngH > 0)
            {
                DisplayWidth = pngW;
                DisplayHeight = pngH;
            }
            else
            {
                DisplayWidth = Math.Max(1, expectW);
                DisplayHeight = Math.Max(1, expectH);
            }
            DisplayImage = bytes;

            UpdateDisplayBboxes();
            _appLog?.Info($"RenderDisplayAsync: DisplayImage 已设置 {DisplayWidth}x{DisplayHeight}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PdfVM] RenderDisplayAsync error: {ex.Message}");
            _appLog?.Error($"RenderDisplayAsync 失败: {ex.Message}");
            ErrorMessage = $"页面渲染失败: {ex.Message}";
        }
    }

    /// <summary>读取 PNG IHDR 宽高，避免整图解码。</summary>
    private static bool TryReadPngSize(byte[] png, out int width, out int height)
    {
        width = 0;
        height = 0;
        // signature(8) + IHDR len(4) + type(4) + width(4) + height(4)
        if (png == null || png.Length < 24) return false;
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return false;
        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width > 0 && height > 0;
    }

    /// <summary>
    /// 坐标映射：OCR 图片空间 → 显示空间。
    ///   摆正坐标系 → (逆方向角) → 上传图空间
    ///   → (÷resize_scale) → 2x渲染空间 → (÷2) → PDF空间 → (×zoom) → 显示空间
    /// </summary>
    public double[] GetScaledBbox(double[] rawBbox)
    {
        if (rawBbox.Length < 4) return rawBbox;

        // Step 0: 文档方向校正逆变换（angle=90/180/270 时 bbox 在摆正图上）
        var mapped = UndoDocOrientation(rawBbox, Preprocess);
        double x1 = mapped[0], y1 = mapped[1], x2 = mapped[2], y2 = mapped[3];

        // Step 1: 如果存在预处理缩放，先还原到 2x 渲染空间
        if (Preprocess != null && Preprocess.ResizeScale > 0 && Preprocess.ResizeScale < 1.0)
        {
            double scaleUp = 1.0 / Preprocess.ResizeScale;
            x1 *= scaleUp; y1 *= scaleUp; x2 *= scaleUp; y2 *= scaleUp;
        }

        // Step 2: 从 2x 空间 → PDF 坐标空间 (÷2) → 显示空间 (×Zoom)
        x1 = (x1 / 2.0) * Zoom;
        y1 = (y1 / 2.0) * Zoom;
        x2 = (x2 / 2.0) * Zoom;
        y2 = (y2 / 2.0) * Zoom;

        // Step 3: 处理阅读器旋转
        return ApplyRotation(x1, y1, x2, y2);
    }

    /// <summary>
    /// 将方向分类摆正后的 bbox 映射回上传原图坐标。
    /// 仅当 angle=90/180/270（方向分类开启并生效）时做逆变换；
    /// angle=0 时原样返回——禁止用结果图宽高再缩放，否则会与 resize_scale 叠乘导致 BOX 偏移。
    /// </summary>
    internal static double[] UndoDocOrientation(double[] bbox, PreprocessInfo? pp)
    {
        if (pp == null || bbox.Length < 4) return bbox;
        int angle = ((pp.OrientationAngle % 360) + 360) % 360;
        if (angle == 0)
            return bbox;

        int srcW = pp.OrientedWidth;
        int srcH = pp.OrientedHeight;
        int dstW = pp.ResizedSize is { Length: >= 2 } ? pp.ResizedSize[0]
                 : pp.OriginalSize is { Length: >= 2 } ? pp.OriginalSize[0] : 0;
        int dstH = pp.ResizedSize is { Length: >= 2 } ? pp.ResizedSize[1]
                 : pp.OriginalSize is { Length: >= 2 } ? pp.OriginalSize[1] : 0;

        if (srcW <= 0 || srcH <= 0)
            return bbox;

        double[] MapPoint(double x, double y) => angle switch
        {
            90 => new[] { srcH - y, x },
            180 => new[] { srcW - x, srcH - y },
            270 => new[] { y, srcW - x },
            _ => new[] { x, y },
        };

        var p1 = MapPoint(bbox[0], bbox[1]);
        var p2 = MapPoint(bbox[2], bbox[1]);
        var p3 = MapPoint(bbox[0], bbox[3]);
        var p4 = MapPoint(bbox[2], bbox[3]);
        double nx1 = Math.Min(Math.Min(p1[0], p2[0]), Math.Min(p3[0], p4[0]));
        double ny1 = Math.Min(Math.Min(p1[1], p2[1]), Math.Min(p3[1], p4[1]));
        double nx2 = Math.Max(Math.Max(p1[0], p2[0]), Math.Max(p3[0], p4[0]));
        double ny2 = Math.Max(Math.Max(p1[1], p2[1]), Math.Max(p3[1], p4[1]));

        // 摆正图尺寸 → 上传图尺寸（仅方向分类开启时需要）
        int midW = (angle is 90 or 270) ? srcH : srcW;
        int midH = (angle is 90 or 270) ? srcW : srcH;
        if (dstW > 0 && dstH > 0 && midW > 0 && midH > 0
            && (midW != dstW || midH != dstH))
        {
            double sx = (double)dstW / midW;
            double sy = (double)dstH / midH;
            nx1 *= sx; nx2 *= sx;
            ny1 *= sy; ny2 *= sy;
        }

        return new[] { nx1, ny1, nx2, ny2 };
    }

    private double[] ApplyRotation(double x1, double y1, double x2, double y2)
    {
        double w = DisplayWidth, h = DisplayHeight;
        return Rotation switch
        {
            90 => new[] { w - y2, x1, w - y1, x2 },
            180 => new[] { w - x2, h - y2, w - x1, h - y1 },
            270 => new[] { y1, h - x2, y2, h - x1 },
            _ => new[] { x1, y1, x2, y2 },
        };
    }

    private int _bboxVersion;
    /// <summary>bbox 坐标更新版本号，供视图重绘叠加层</summary>
    public int BboxVersion
    {
        get => _bboxVersion;
        private set => this.RaiseAndSetIfChanged(ref _bboxVersion, value);
    }

    private void UpdateDisplayBboxes()
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            try
            {
                Blocks[i].DisplayBbox = GetScaledBbox(Blocks[i].RawBbox);
            }
            catch
            {
                Blocks[i].DisplayBbox = Array.Empty<double>();
            }
        }
        BboxVersion++;
    }

    /// <summary>从显示坐标查找命中的 bbox 索引</summary>
    public int FindBlockAtPoint(double displayX, double displayY)
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            var bb = Blocks[i].DisplayBbox;
            if (bb.Length == 4)
            {
                if (displayX >= bb[0] && displayX <= bb[2] && displayY >= bb[1] && displayY <= bb[3])
                    return i;
            }
        }
        return -1;
    }

    /// <summary>处理画布点击</summary>
    public void OnCanvasClicked(double x, double y)
    {
        var idx = FindBlockAtPoint(x, y);
        SelectedBlockIndex = idx;
        TextBlockClicked?.Invoke(idx);
    }

    /// <summary>处理点击事件 (从画布 bbox 或文本面板)</summary>
    public void OnTextClicked(int blockIndex)
    {
        System.Diagnostics.Debug.WriteLine($"[PdfVM] OnTextClicked: block={blockIndex}, total={Blocks.Count}");
        if (blockIndex >= 0 && blockIndex < Blocks.Count)
        {
            // 清除所有旧高亮
            for (int i = 0; i < Blocks.Count; i++)
                Blocks[i].IsHighlighted = (i == blockIndex);

            SelectedBlockIndex = blockIndex;
            TextBlockClicked?.Invoke(blockIndex);  // 触发文本面板滚动
            _appLog?.Info($"选中文本块 [{blockIndex + 1}]: {Blocks[blockIndex].Text[..Math.Min(Blocks[blockIndex].Text.Length, 40)]}");
        }
    }

    /// <summary>
    /// 由 UI 注入：弹出批量识别进度窗并执行任务。
    /// 返回 true=正常完成，false=用户取消。
    /// </summary>
    public Func<string, string, Func<IProgress<OcrProgress>, CancellationToken, Task>, Task<bool>>? OcrProgressUi { get; set; }

    private async Task OcrCurrentPageAsync()
    {
        if (_pdfOcr == null || _libraryItem == null) return;
        int pageIndex = CurrentPage;
        // 确保结果目录与当前阅读器一致
        if (string.IsNullOrEmpty(_libraryItem.OutputDir))
            _libraryItem.OutputDir = JsonDir;
        if (string.IsNullOrEmpty(JsonDir) && !string.IsNullOrEmpty(_libraryItem.OutputDir))
            JsonDir = _libraryItem.OutputDir;

        var success = await RunOcrJobAsync($"单页识别 (第 {pageIndex + 1} 页)", async (progress, ct) =>
        {
            var log = new Progress<string>(msg =>
            {
                StatusMessage = msg;
                OcrStatusChanged?.Invoke(msg);
                progress.Report(new OcrProgress
                {
                    CurrentPage = pageIndex + 1,
                    TotalPages = TotalPages,
                    Status = msg,
                });
            });
            bool ok = await _pdfOcr.RunSinglePageOcrAsync(_libraryItem, pageIndex, log, ct);
            if (!ok) throw new Exception("单页识别失败");
        }, showProgressWindow: false);

        if (!success) return;

        MarkPageOcrDone(pageIndex);
        // 识别后强制回到该页并刷新文本 + 画面 bbox
        if (CurrentPage != pageIndex)
        {
            _currentPage = pageIndex;
            this.RaisePropertyChanged(nameof(CurrentPage));
            this.RaisePropertyChanged(nameof(CurrentPageDisplay));
            UpdatePageNavCurrent();
        }
        await LoadPageAtIndexAsync(pageIndex);
        await RenderDisplayAsync();
        _appLog?.Info($"单页识别后刷新: page={pageIndex + 1}, blocks={Blocks.Count}");
    }

    // ---- 批量 OCR（排队池模式，无模态弹窗） ----

    private async Task BatchOcrAllPagesAsync()
    {
        if (_libraryItem == null || string.IsNullOrEmpty(JsonDir)) return;
        await RunBatchOcrAsync(0, TotalPages - 1);
    }

    private async Task BatchOcrRangeAsync()
    {
        if (_libraryItem == null || string.IsNullOrEmpty(JsonDir)) return;
        if (!int.TryParse(RangeFrom.Trim(), out int from) || !int.TryParse(RangeTo.Trim(), out int to))
        {
            ErrorMessage = "页码范围请输入数字";
            return;
        }
        if (from < 1 || to < 1 || from > TotalPages || to > TotalPages || from > to)
        {
            ErrorMessage = $"页码范围无效，请输入 1–{TotalPages}，且起始 ≤ 结束";
            return;
        }
        await RunBatchOcrAsync(from - 1, to - 1);
    }

    private async Task RunBatchOcrAsync(int startPage, int endPage)
    {
        var uploadSvc = BatchUploadSvc;
        var batchOcr = BatchOcrSvc;
        var batchCache = BatchCacheSvc;
        var taskSync = TaskSyncSvc;
        if (uploadSvc == null || batchOcr == null || batchCache == null || _libraryItem == null)
        {
            ErrorMessage = "批量 OCR 服务未就绪";
            return;
        }

        IsBatchProcessing = true;
        IsOcrRunning = true;
        ClearError();
        BatchProgressText = "正在检查本地识别结果...";
        OcrStatusChanged?.Invoke(BatchProgressText);

        // 只对本地没有有效结果的页上传/计费，避免「本地已有却仍扣费」
        var rangePages = Enumerable.Range(startPage, endPage - startPage + 1).ToList();
        var needPages = rangePages.Where(i => !HasOcrResult(i)).ToList();
        var skipped = rangePages.Count - needPages.Count;

        if (needPages.Count == 0)
        {
            IsBatchProcessing = false;
            IsOcrRunning = false;
            BatchProgressText = "";
            StatusMessage = "所选页本地已有识别结果，无需重复识别（不扣费）";
            OcrStatusChanged?.Invoke(StatusMessage);
            RefreshPageNavOcrStatus();
            await LoadPageAsync();
            return;
        }

        int pageCount = needPages.Count;
        BatchProgressText = skipped > 0
            ? $"需识别 {pageCount} 页（已跳过本地已有 {skipped} 页）..."
            : "正在计算费用...";
        OcrStatusChanged?.Invoke(BatchProgressText);

        // ---- 费用确认：套餐优先，不足部分按余额预计 ----
        if (ShowBatchOcrConfirmAsync != null)
        {
            string confirmMsg;
            if (_pricingService != null)
            {
                try
                {
                    var assets = await _pricingService.GetAssetsAsync();
                    confirmMsg = OcrBillingConfirm.BuildMessage(pageCount, assets);
                }
                catch
                {
                    confirmMsg = OcrBillingConfirm.BuildFetchFailedMessage(pageCount);
                }
            }
            else
            {
                confirmMsg = OcrBillingConfirm.BuildFetchFailedMessage(pageCount);
            }

            bool confirmed = await ShowBatchOcrConfirmAsync("批量 OCR 确认", confirmMsg);
            if (!confirmed)
            {
                IsBatchProcessing = false;
                IsOcrRunning = false;
                BatchProgressText = "";
                StatusMessage = "已取消批量识别";
                OcrStatusChanged?.Invoke(StatusMessage);
                return;
            }
        }

        BatchProgressText = $"准备上传 0/{pageCount} 页...";
        OcrStatusChanged?.Invoke(BatchProgressText);

        string? localId = null;
        try
        {
            localId = taskSync?.BeginLocalUpload(DocumentName ?? "", pageCount, JsonDir);
            BatchProgressText = "排队上传…";
            OcrStatusChanged?.Invoke(BatchProgressText);

            // 上传前清陈旧 file_key（服务端 OCR 终态会删临时图，续传会导致「无法加载图片」）
            OcrResultDisk.InvalidateUploadCache(
                JsonDir, needPages.Select(i => i + 1), _appLog);

            var queue = UploadJobQueue;
            List<(int PageNumber, string FileKey)> uploaded;
            BatchSubmitResponse response;

            if (queue != null)
            {
                var submitResult = await queue.EnqueueAsync(localId, async ct =>
                {
                    var uploadProgress = new Progress<BatchUploadProgress>(p =>
                    {
                        if (localId != null)
                            taskSync?.UpdateLocalUpload(localId, p.UploadedPages);
                        BatchProgressText = p.IsFinished
                            ? $"上传完成，提交批次..."
                            : $"{p.Status}";
                        OcrStatusChanged?.Invoke(BatchProgressText);
                    });
                    var pages = await uploadSvc.UploadPagesAsync(
                        _libraryItem, needPages, uploadProgress, ct);
                    if (pages.Count == 0)
                        throw new Exception("上传失败：无有效文件");

                    BatchProgressText = $"提交批次 ({pages.Count} 页)...";
                    OcrStatusChanged?.Invoke(BatchProgressText);

                    // 普通 OCR 不传 Paddle 参数（模力方舟无法透传，避免无效参数困扰）
                    var resp = await batchOcr.SubmitBatchAsync(
                        pages.Select(x => x.FileKey).ToList(),
                        DocumentName ?? "",
                        options: null,
                        pages.Select(x => x.PageNumber).ToList());
                    return (Pages: pages, Response: resp);
                }, CancellationToken.None);
                uploaded = submitResult.Pages;
                response = submitResult.Response;
            }
            else
            {
                uploaded = await uploadSvc.UploadPagesAsync(
                    _libraryItem, needPages,
                    new Progress<BatchUploadProgress>(p =>
                    {
                        if (localId != null)
                            taskSync?.UpdateLocalUpload(localId, p.UploadedPages);
                        BatchProgressText = p.IsFinished
                            ? $"上传完成，提交批次..."
                            : $"{p.Status}";
                        OcrStatusChanged?.Invoke(BatchProgressText);
                    }),
                    CancellationToken.None);

                if (uploaded.Count == 0)
                {
                    taskSync?.FailLocalUpload(localId ?? "", "上传失败：无有效文件");
                    ErrorMessage = "上传失败：无有效文件";
                    IsBatchProcessing = false;
                    IsOcrRunning = false;
                    return;
                }

                BatchProgressText = $"提交批次 ({uploaded.Count} 页)...";
                OcrStatusChanged?.Invoke(BatchProgressText);

                response = await batchOcr.SubmitBatchAsync(
                    uploaded.Select(x => x.FileKey).ToList(),
                    DocumentName ?? "",
                    options: null,
                    uploaded.Select(x => x.PageNumber).ToList());
            }

            var fileKeys = uploaded.Select(x => x.FileKey).ToList();
            var pageNums = uploaded.Select(x => x.PageNumber).ToList();

            // 提交成功即清旧结果，确保 TaskSync 轮询能拿到 result_json 并落盘
            OcrResultDisk.ClearPages(JsonDir, pageNums, _appLog);

            if (taskSync != null)
            {
                await taskSync.TrackSubmittedAsync(
                    localId, response, DocumentName ?? "", JsonDir, fileKeys.Count);
                localId = null;
            }
            else
            {
                await batchCache.SaveBatchAsync(
                    response.BatchUuid, DocumentName ?? "",
                    response.TotalPages, JsonDir);
            }

            // 提交成功：交由任务中心 / TaskSync 后台识别与落盘，不再本地阻塞轮询
            BatchProgressText =
                $"已提交 {fileKeys.Count} 页，可在任务中心查看（关闭客户端不影响识别）"
                + (skipped > 0 ? $"；已跳过本地 {skipped} 页" : "");
            OcrStatusChanged?.Invoke(BatchProgressText);
            StatusMessage = BatchProgressText;

            IsBatchProcessing = false;
            IsOcrRunning = false;
            NavigateToTaskCenter?.Invoke(response.BatchUuid);
        }
        catch (Exception ex)
        {
            if (localId != null)
                taskSync?.FailLocalUpload(localId, ex.Message);
            _appLog?.Error($"BatchOCR 提交异常: {ex.Message}");
            ErrorMessage = $"批量识别失败: {ex.Message}";
            IsBatchProcessing = false;
            IsOcrRunning = false;
            BatchProgressText = "";
        }
    }

    // ---- 单页 OCR（保留原逻辑） ----

    private async Task OcrAllPagesAsync()
    {
        if (_pdfOcr == null || _libraryItem == null) return;
        if (string.IsNullOrEmpty(_libraryItem.OutputDir))
            _libraryItem.OutputDir = JsonDir;

        var success = await RunOcrJobAsync("全文识别", async (progress, ct) =>
        {
            await _pdfOcr.RunOcrAsync(_libraryItem, progress, ct);
        }, showProgressWindow: true);
        if (!success) return;
        RefreshPageNavOcrStatus();
        await LoadPageAsync();
        await RenderDisplayAsync();
    }

    private async Task OcrRangeAsync()
    {
        if (_pdfOcr == null || _libraryItem == null) return;
        if (!int.TryParse(RangeFrom.Trim(), out int from) || !int.TryParse(RangeTo.Trim(), out int to))
        {
            ErrorMessage = "页码范围请输入数字";
            return;
        }
        if (from < 1 || to < 1 || from > TotalPages || to > TotalPages || from > to)
        {
            ErrorMessage = $"页码范围无效，请输入 1–{TotalPages}，且起始 ≤ 结束";
            return;
        }
        if (string.IsNullOrEmpty(_libraryItem.OutputDir))
            _libraryItem.OutputDir = JsonDir;

        var success = await RunOcrJobAsync($"范围识别 ({from}-{to})", async (progress, ct) =>
        {
            await _pdfOcr.RunPageRangeOcrAsync(
                _libraryItem, from - 1, to - 1, skipExisting: true, progress, ct);
        }, showProgressWindow: true);
        if (!success) return;
        RefreshPageNavOcrStatus();
        await LoadPageAsync();
        await RenderDisplayAsync();
    }

    private async Task OcrCurrentPageRubbingAsync()
    {
        if (_pdfOcr == null || _libraryItem == null) return;
        int pageIndex = CurrentPage;
        if (string.IsNullOrEmpty(_libraryItem.OutputDir))
            _libraryItem.OutputDir = JsonDir;
        if (string.IsNullOrEmpty(JsonDir) && !string.IsNullOrEmpty(_libraryItem.OutputDir))
            JsonDir = _libraryItem.OutputDir;

        var rubbingOptions = OcrVlOptions.CreateRubbing();
        var success = await RunOcrJobAsync($"单页拓片识别 (第 {pageIndex + 1} 页)", async (progress, ct) =>
        {
            var log = new Progress<string>(msg =>
            {
                StatusMessage = msg;
                OcrStatusChanged?.Invoke(msg);
                progress.Report(new OcrProgress
                {
                    CurrentPage = pageIndex + 1,
                    TotalPages = TotalPages,
                    Status = msg,
                });
            });
            bool ok = await _pdfOcr.RunSinglePageOcrAsync(
                _libraryItem, pageIndex, log, ct, rubbingOptions);
            if (!ok) throw new Exception("单页拓片识别失败");
        }, showProgressWindow: false);

        if (!success) return;

        MarkPageOcrDone(pageIndex);
        if (CurrentPage != pageIndex)
        {
            _currentPage = pageIndex;
            this.RaisePropertyChanged(nameof(CurrentPage));
            this.RaisePropertyChanged(nameof(CurrentPageDisplay));
            UpdatePageNavCurrent();
        }
        await LoadPageAtIndexAsync(pageIndex);
        await RenderDisplayAsync();
        _appLog?.Info($"单页拓片识别后刷新: page={pageIndex + 1}, blocks={Blocks.Count}");
    }

    private async Task OcrAllPagesRubbingAsync()
    {
        if (_pdfOcr == null || _libraryItem == null) return;
        if (string.IsNullOrEmpty(_libraryItem.OutputDir))
            _libraryItem.OutputDir = JsonDir;

        var rubbingOptions = OcrVlOptions.CreateRubbing();
        var success = await RunOcrJobAsync("全文拓片识别", async (progress, ct) =>
        {
            await _pdfOcr.RunOcrAsync(
                _libraryItem, progress, ct, rubbingOptions, waitForCompletion: true);
        }, showProgressWindow: true);
        if (!success) return;
        RefreshPageNavOcrStatus();
        await LoadPageAsync();
        await RenderDisplayAsync();
    }

    /// <returns>是否成功完成（非异常中断；取消视为 false）</returns>
    private async Task<bool> RunOcrJobAsync(
        string title,
        Func<IProgress<OcrProgress>, CancellationToken, Task> work,
        bool showProgressWindow = false)
    {
        if (IsOcrRunning) return false;
        IsOcrRunning = true;
        ClearError();
        StatusMessage = $"{title}...";
        OcrStatusChanged?.Invoke(StatusMessage);
        _appLog?.Info($"PDF OCR: {title}");
        try
        {
            async Task BindAndRunAsync(IProgress<OcrProgress> sink, CancellationToken ct)
            {
                var progress = new Progress<OcrProgress>(p =>
                {
                    var msg = p.IsCancelled
                        ? $"{title}: {p.Status}"
                        : p.IsError
                            ? $"{title}: {p.Status}"
                            : $"{title} [{p.CurrentPage}/{p.TotalPages}]: {p.Status}";
                    StatusMessage = msg;
                    OcrStatusChanged?.Invoke(msg);
                    if (p.CurrentPage >= 1)
                        MarkPageOcrDone(p.CurrentPage - 1);
                    sink.Report(p);
                });
                await work(progress, ct);
            }

            if (showProgressWindow && OcrProgressUi != null)
            {
                bool completed = await OcrProgressUi(title, DocumentName, BindAndRunAsync);
                if (!completed)
                {
                    StatusMessage = $"{title} 已取消";
                    OcrStatusChanged?.Invoke(StatusMessage);
                    _appLog?.Info($"PDF OCR 已取消: {title}");
                    return false;
                }
            }
            else
            {
                await BindAndRunAsync(new Progress<OcrProgress>(_ => { }), CancellationToken.None);
            }

            StatusMessage = $"{title} 完成";
            OcrStatusChanged?.Invoke(StatusMessage);
            _appLog?.Info($"PDF OCR 完成: {title}");
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{title} 已取消";
            OcrStatusChanged?.Invoke(StatusMessage);
            _appLog?.Info($"PDF OCR 已取消: {title}");
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{title} 失败: {ex.Message}";
            if (ex.Message.Contains("no_param_provider", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("no_capability", StringComparison.OrdinalIgnoreCase))
                IsRubbingAvailable = false;
            OcrStatusChanged?.Invoke(ErrorMessage);
            _appLog?.Error($"PDF OCR 失败: {ex.Message}");
            return false;
        }
        finally
        {
            IsOcrRunning = false;
        }
    }

    public async Task RefreshRubbingAvailabilityAsync()
    {
        if (IsOffline || ApiSvc == null)
            return;
        try
        {
            IsRubbingAvailable = await ApiSvc.CheckRubbingAvailableAsync();
            _appLog?.Info($"参数通道(拓片可用): available={IsRubbingAvailable}");
        }
        catch (Exception ex)
        {
            _appLog?.Warn($"查询拓片能力失败，保持默认可用: {ex.Message}");
            IsRubbingAvailable = true;
        }
    }

    /// <summary>跳转到指定页（0-based）</summary>
    public Task JumpToPageAsync(int pageIndex) => GoToPageAsync(pageIndex);

    private async Task GoToPageAsync(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= TotalPages || pageIndex == CurrentPage) return;
        _currentPage = pageIndex;
        this.RaisePropertyChanged(nameof(CurrentPage));
        this.RaisePropertyChanged(nameof(CurrentPageDisplay));
        UpdatePageNavCurrent();
        SyncCachedPageSize(pageIndex);
        await RenderDisplayAsync();
        await LoadPageAsync();
    }

    private async Task NextPageAsync()
    {
        if (CurrentPage < TotalPages - 1)
            await GoToPageAsync(CurrentPage + 1);
    }

    private async Task PrevPageAsync()
    {
        if (CurrentPage > 0)
            await GoToPageAsync(CurrentPage - 1);
    }

    private void RebuildPageNavItems()
    {
        PageNavItems.Clear();
        for (int i = 0; i < TotalPages; i++)
        {
            PageNavItems.Add(new PdfPageNavItem
            {
                PageIndex = i,
                PageNumber = i + 1,
                HasOcr = HasOcrResult(i),
                IsCurrent = i == CurrentPage,
            });
        }
    }

    /// <summary>从 SettingsService 加载持久化的字体大小。</summary>
    private async Task LoadFontSizeAsync()
    {
        if (_settingsService == null) return;
        var saved = await _settingsService.GetAsync("pdf_text_font_size");
        if (int.TryParse(saved, out int fs) && fs >= 8 && fs <= 48)
            TextFontSize = fs;
    }

    /// <summary>持久化字体大小到 SettingsService。</summary>
    private async Task PersistFontSizeAsync()
    {
        if (_settingsService == null) return;
        await _settingsService.SetAsync("pdf_text_font_size", TextFontSize.ToString("F0"));
    }

    /// <summary>更新文献库中的已识别页数（批量识别完成后调用）。</summary>
    private async Task UpdateLibraryOcrCountAsync()
    {
        if (_libraryItem == null || _libraryService == null || string.IsNullOrEmpty(JsonDir))
            return;
        try
        {
            var count = System.IO.Directory.GetFiles(JsonDir, "page_*_ocr_result.json").Length;
            _libraryItem.OcrDonePages = count;
            await _libraryService.UpdateItemAsync(_libraryItem);
        }
        catch (Exception ex)
        {
            _appLog?.Error($"更新文献已识别页数失败: {ex.Message}");
        }
    }

    private void RefreshPageNavOcrStatus()
    {
        for (int i = 0; i < PageNavItems.Count; i++)
            PageNavItems[i].HasOcr = HasOcrResult(i);
    }

    /// <summary>后台 OCR 落盘后：刷新绿点并重新加载当前页结果（不重置阅读位置）。</summary>
    public async Task RefreshOcrFromDiskAsync()
    {
        RefreshPageNavOcrStatus();
        await UpdateLibraryOcrCountAsync();
        await LoadPageAsync();
        StatusMessage = $"OCR 已更新: {Blocks.Count} 文本块";
        _appLog?.Info($"OCR 结果已从磁盘刷新: {DocumentName}, blocks={Blocks.Count}");
    }

    /// <summary>切回本标签页时，把 PdfRender 切回本 PDF 并重绘当前页（保留页码/缩放）。</summary>
    public async Task ReactivateAsync()
    {
        if (string.IsNullOrEmpty(PdfPath) || _pdfRender == null) return;
        try
        {
            await _pdfRender.OpenPdfAsync(PdfPath);
            await RenderDisplayAsync();
        }
        catch (Exception ex)
        {
            _appLog?.Error($"重新激活文档失败: {ex.Message}");
        }
    }

    private void UpdatePageNavCurrent()
    {
        for (int i = 0; i < PageNavItems.Count; i++)
            PageNavItems[i].IsCurrent = i == CurrentPage;
    }

    private void MarkPageOcrDone(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageNavItems.Count) return;
        PageNavItems[pageIndex].HasOcr = HasOcrResult(pageIndex);
    }

    private bool HasOcrResult(int pageIndex)
    {
        if (string.IsNullOrEmpty(JsonDir)) return false;
        var jsonPath = System.IO.Path.Combine(JsonDir, $"page_{pageIndex + 1}_ocr_result.json");
        try
        {
            var fi = new FileInfo(jsonPath);
            return fi.Exists && fi.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task SaveTextAsync()
    {
        if (string.IsNullOrEmpty(JsonDir))
        {
            ErrorMessage = "未找到 OCR 结果目录";
            return;
        }
        if (Blocks.Count == 0)
        {
            StatusMessage = "当前页无文本可保存";
            return;
        }

        try
        {
            // 编辑框 → 规范文本（繁简/句读预览下仅当用户改过才覆盖原文）
            foreach (var b in Blocks)
                b.CommitEditorToText(ShowConverted, ConvertToSimplified, GetPunctuatedBase(b));

            var edits = Blocks
                .Where(b => b.SourceIndex >= 0)
                .Select(b => new OcrTextEdit
                {
                    SourceKind = b.SourceKind,
                    SourceIndex = b.SourceIndex,
                    Text = b.Text ?? "",
                })
                .ToList();

            if (edits.Count == 0)
            {
                ErrorMessage = "无法定位文本块写回位置";
                return;
            }

            await _ocrResult.SavePageTextsAsync(JsonDir, CurrentPage, edits);
            // 规范文本已变，清本页句读缓存；若仍开着句读则重新请求
            ClearPunctuateCacheForPage(CurrentPage);
            if (ShowPunctuated)
                await EnsurePunctuateCurrentPageAsync();
            else
                await UpdateTextDisplayAsync();
            ClearError();
            StatusMessage = $"已保存 {edits.Count} 个文本块";
            _appLog?.Info($"OCR 文本已写回: page={CurrentPage + 1}, blocks={edits.Count}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存失败: {ex.Message}";
            _appLog?.Error($"OCR 文本写回失败: {ex.Message}");
        }
    }

    // ---- 高亮管理 ----

    private async Task LoadHighlightsAsync()
    {
        if (_highlightService == null || string.IsNullOrEmpty(JsonDir)) return;
        _docHighlights = await _highlightService.LoadAsync(JsonDir);
        _appLog?.Info($"[HL] LoadHighlights — loaded {_docHighlights.Sum(kv => kv.Value.Count)} entries from highlights.json (pages: {string.Join(",", _docHighlights.Keys)})");
        _applyPageHighlights();
    }

    private void _applyPageHighlights()
    {
        _appLog?.Info($"[HL] ApplyHighlights — page={CurrentPage}, blocks={Blocks.Count}, docHLPages={_docHighlights.Count}");

        foreach (var b in Blocks)
        {
            b.UserHighlightColor = null;
            b.HighlightedSegments = Array.Empty<HighlightSegment>();
        }

        if (!_docHighlights.TryGetValue(CurrentPage, out var pageHighlights))
        {
            _appLog?.Info($"[HL] ApplyHighlights — no highlights for page {CurrentPage}");
            return;
        }
        _appLog?.Info($"[HL] ApplyHighlights — page {CurrentPage} has {pageHighlights.Count} entries: {string.Join(", ", pageHighlights.Select(h => $"'{h.Text?[..Math.Min(h.Text?.Length ?? 0, 20)]}'"))}");

        foreach (var block in Blocks)
        {
            var text = block.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            // 收集该块中所有匹配的高亮区间
            var matches = new List<(int start, int len, string color)>();
            foreach (var hl in pageHighlights)
            {
                var hlText = hl.Text ?? "";
                if (string.IsNullOrEmpty(hlText)) continue;
                int pos = 0;
                while ((pos = text.IndexOf(hlText, pos, StringComparison.Ordinal)) != -1)
                {
                    matches.Add((pos, hlText.Length, hl.Color));
                    pos += hlText.Length;
                }
            }

            if (matches.Count == 0) continue;

            // 合并重叠区间，按起始位置排序
            matches.Sort((a, b) => a.start.CompareTo(b.start));

            // 构建分段（高亮段 + 普通段交错）
            var segments = new List<HighlightSegment>();
            int cursor = 0;
            foreach (var (start, len, color) in matches)
            {
                if (start > cursor)
                {
                    // 高亮之前的普通文本
                    segments.Add(new HighlightSegment { Text = text[cursor..start] });
                }
                // 高亮文本
                var end = Math.Min(start + len, text.Length);
                segments.Add(new HighlightSegment { Text = text[start..end], BgHex = color });
                cursor = end;
            }
            // 剩余普通文本
            if (cursor < text.Length)
                segments.Add(new HighlightSegment { Text = text[cursor..] });

            block.HighlightedSegments = segments;
            // 全块匹配时同时设置整块底色
            if (matches.Any(m => m.start == 0 && m.len == text.Length))
                block.UserHighlightColor = matches.First(m => m.start == 0 && m.len == text.Length).color;
        }

        int totalSegments = Blocks.Sum(b => b.HighlightedSegments.Count);
        int blocksWithSegments = Blocks.Count(b => b.HighlightedSegments.Count > 0);
        _appLog?.Info($"[HL] ApplyHighlights DONE — totalSegments={totalSegments}, blocksWithHL={blocksWithSegments}");
    }

    private async Task AddHighlightAsync()
    {
        // 找到当前处于编辑模式的块，读取其选中文字
        var editBlock = Blocks.FirstOrDefault(b => b.IsEditing);
        if (editBlock != null && SelectedBlockIndex >= 0 && SelectedBlockIndex < Blocks.Count
            && Blocks[SelectedBlockIndex] != editBlock)
        {
            SelectedBlockIndex = Blocks.IndexOf(editBlock);
        }
        var block = editBlock ?? (SelectedBlockIndex >= 0 && SelectedBlockIndex < Blocks.Count ? Blocks[SelectedBlockIndex] : null);

        _appLog?.Info($"[HL] AddHighlight ENTER — selIdx={SelectedBlockIndex}, editingBlock={block != null}");

        if (_highlightService == null || block == null)
        {
            _appLog?.Warn($"[HL] AddHighlight ABORT — svc={_highlightService != null}, block={block != null}");
            StatusMessage = "请先单击文本块进入编辑，并选中要高亮的文字";
            return;
        }

        if (!block.IsEditing)
        {
            StatusMessage = "请先单击文本块进入编辑，并选中要高亮的文字";
            return;
        }

        var selectedText = block.CurrentSelectedText?.Trim();
        var text = ResolveHighlightCanonicalText(block, selectedText);

        if (string.IsNullOrWhiteSpace(text))
        {
            _appLog?.Warn($"[HL] AddHighlight ABORT — no valid selection (raw='{selectedText?[..Math.Min(selectedText?.Length ?? 0, 20)]}')");
            StatusMessage = "请先选中要高亮的文字（仅高亮选中部分，不会整块高亮）";
            return;
        }

        _appLog?.Info($"[HL] AddHighlight — textLen={text.Length}, block fullText='{(block.Text ?? "")[..Math.Min((block.Text ?? "").Length, 40)]}', highlightText='{text[..Math.Min(text.Length, 40)]}'");

        _highlightService.AddHighlight(_docHighlights, CurrentPage, text, "#FFEB3B");
        _appLog?.Info($"[HL] Added to memory — docHighlights total={_docHighlights.Sum(kv => kv.Value.Count)} entries");

        _applyPageHighlights();

        _appLog?.Info($"[HL] Saving highlights.json...");
        await _highlightService.SaveAsync(JsonDir, _docHighlights);
        _appLog?.Info($"[HL] Save complete — block.IsEditing=false, segments={block.HighlightedSegments.Count}");

        block.IsEditing = false;
        block.CurrentSelectedText = null;
        StatusMessage = $"高亮已标记: \"{text[..Math.Min(text.Length, 30)]}\"";
        _appLog?.Info($"高亮: 第{CurrentPage + 1}页 [{SelectedBlockIndex + 1}] \"{text[..Math.Min(text.Length, 30)]}\" 已标记");
    }

    /// <summary>
    /// 将编辑框中的选中文字映射为规范 Text 中的子串。
    /// 繁→简（等长）按下标回映；句读（变长）按对齐去掉插入标点后回映。
    /// 高亮始终落在规范 Text 上，与句读预览互不污染。
    /// </summary>
    private static string? ResolveHighlightCanonicalText(OcrTextBlockViewModel block, string? selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
            return null;

        var sel = selectedText.Trim();
        if (sel.Length == 0)
            return null;

        var blockText = block.Text ?? "";
        if (blockText.Length == 0)
            return null;

        // 原文模式或选中内容已是规范文本
        if (blockText.Contains(sel, StringComparison.Ordinal))
            return sel;

        var editor = block.EditorText ?? "";
        if (string.IsNullOrEmpty(editor))
            return null;

        // 繁→简：选区来自 EditorText，等长下标映射回 Text
        if (editor.Length == blockText.Length)
        {
            var idx = editor.IndexOf(sel, StringComparison.Ordinal);
            if (idx >= 0)
                return blockText.Substring(idx, sel.Length);
        }

        // 句读（或句读+繁简）：对齐映射，去掉预览插入的标点
        var mapped = PreviewTextAlign.MapEditorSelectionToCanon(blockText, editor, sel);
        if (!string.IsNullOrEmpty(mapped) && blockText.Contains(mapped, StringComparison.Ordinal))
            return mapped;

        // 兜底：去掉选区标点后再在规范文本中查找
        var stripped = new string(sel.Where(c => !PreviewTextAlign.IsInsertedPreviewChar(c)).ToArray());
        if (stripped.Length > 0 && blockText.Contains(stripped, StringComparison.Ordinal))
            return stripped;

        return null;
    }

    private async Task ClearHighlightsAsync()
    {
        if (_highlightService == null) return;
        if (!_docHighlights.ContainsKey(CurrentPage)) return; // 本页无高亮，跳过

        _highlightService.RemovePageHighlights(_docHighlights, CurrentPage);
        // 重新计算分段 → 所有块恢复无高亮状态
        _applyPageHighlights();

        // 确保所有块回到查看模式
        foreach (var b in Blocks)
            b.IsEditing = false;

        await _highlightService.SaveAsync(JsonDir, _docHighlights);
        _appLog?.Info($"清除高亮: 第{CurrentPage + 1}页");
    }

    // ---- 搜索 / 高亮列表 / 导出 ----

    private async Task OpenSearchAsync()
    {
        if (_searchService == null || string.IsNullOrEmpty(JsonDir)) return;

        var opener = OpenSearchDialogAsync;
        if (opener == null) return;

        var result = await opener();
        if (result.HasValue)
            await NavigateToBlock(result.Value.pageIndex, result.Value.blockIndex);
    }

    private async Task OpenHighlightListAsync()
    {
        if (_highlightService == null || string.IsNullOrEmpty(JsonDir)) return;
        if (_docHighlights.Count == 0) return;

        var opener = OpenHighlightListDialogAsync;
        if (opener == null) return;

        var result = await opener();
        if (result.HasValue)
            await NavigateToBlock(result.Value.pageIndex, result.Value.blockIndex);
    }

    private void PostUi(Action action)
    {
        if (PostToUiThread != null) PostToUiThread(action);
        else action();
    }

    private async Task ExportFullTextGuardedAsync()
    {
        try
        {
            await ExportFullTextAsync();
        }
        catch (Exception ex)
        {
            PostUi(() =>
            {
                ErrorMessage = $"导出失败: {ex.Message}";
                IsExporting = false;
            });
            _appLog?.Error($"全文导出失败: {ex.Message}");
        }
    }

    private async Task ExportFullTextAsync()
    {
        if (_exportService == null || string.IsNullOrEmpty(JsonDir)) return;
        if (TotalPages <= 0)
        {
            StatusMessage = "当前文献无页可导出";
            return;
        }

        var opener = OpenExportDialogAsync;
        if (opener == null)
        {
            ErrorMessage = "导出对话框未就绪";
            return;
        }

        ClearError();
        var options = await opener(TotalPages);
        if (options == null) return;

        if (options.ApplyPunctuate)
        {
            if (IsOffline || PunctuateSvc == null)
            {
                ErrorMessage = "句逗导出需要在线且句逗服务可用";
                return;
            }
        }

        var documentName = DocumentName ?? "未命名文献";
        var fileName = $"{documentName}{options.ContentModeFileSuffix}{options.FileExtension}";

        var outputPath = SaveFileDialogRequested != null
            ? await SaveFileDialogRequested.Invoke(documentName, fileName, options.FileExtension)
            : null;

        if (string.IsNullOrEmpty(outputPath)) return;

        // 取消同一 VM 上一次未完成的导出，避免占用句逗 HttpClient
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var ct = _exportCts.Token;

        // 快照：后台线程不要读可变的 VM 属性
        var jsonDir = JsonDir;
        var totalPages = TotalPages;
        var exportService = _exportService;
        var punctuateSvc = PunctuateSvc;
        var applyPunctuate = options.ApplyPunctuate;

        IsExporting = true;
        StatusMessage = applyPunctuate
            ? "正在句逗并导出全文（首次加载模型可能需数分钟）…"
            : "正在导出全文…";
        _appLog?.Info(
            $"开始全文导出: doc={documentName}, simplified={options.UseSimplified}, punctuate={applyPunctuate}, pages={options.PageFrom}-{options.PageTo}, dir={jsonDir}");

        try
        {
            // 整段导出离开 UI 同步上下文，避免第二次句逗 SendAsync 卡在 UI 线程
            await Task.Run(async () =>
            {
                Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>>? punctuate = null;
                if (applyPunctuate)
                {
                    if (punctuateSvc == null)
                        throw new InvalidOperationException("句逗服务未就绪");

                    punctuate = async (texts, token) =>
                    {
                        var blocks = texts
                            .Select((t, i) => (Id: $"export:{i}", Text: t ?? ""))
                            .ToList();
                        _appLog?.Info($"导出句逗请求中: blocks={blocks.Count}");
                        var resp = await punctuateSvc.PunctuatePageAsync(
                            blocks, keepTraditional: null, cancellationToken: token).ConfigureAwait(false);
                        var byId = (resp.Blocks ?? new List<PunctuateBlockResponse>())
                            .GroupBy(x => x.Id)
                            .ToDictionary(g => g.Key, g => g.First().Text ?? "");
                        return blocks
                            .Select(b => byId.TryGetValue(b.Id, out var t) ? t : b.Text)
                            .ToList();
                    };
                }

                var progress = new Progress<(int current, int total)>(p =>
                {
                    PostUi(() => StatusMessage = applyPunctuate
                        ? $"正在句逗并导出… {p.current}/{p.total} 页"
                        : $"正在导出全文… {p.current}/{p.total}");
                });

                await exportService.ExportAsync(
                    jsonDir,
                    totalPages,
                    documentName,
                    outputPath,
                    options,
                    punctuate,
                    progress,
                    ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();
            StatusMessage = $"全文已导出到: {outputPath}";
            _appLog?.Info(
                $"全文导出完成: {outputPath}, simplified={options.UseSimplified}, punctuate={applyPunctuate}, fmt={options.Format}, pages={options.PageFrom}-{options.PageTo}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "全文导出已取消";
            _appLog?.Info("全文导出已取消");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"导出失败: {ex.Message}";
            _appLog?.Error($"全文导出失败: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task OpenNoteAsync()
    {
        var opener = OpenNoteDialogAsync;
        if (opener == null) return;
        await opener();
    }

    /// <summary>获取搜索服务（供视图层创建搜索对话框）</summary>
    public ISearchService? SearchService => _searchService;

    /// <summary>获取笔记服务（供视图层创建笔记对话框）</summary>
    public INoteService? NoteService => _noteService;

    /// <summary>获取当前文档的全部高亮数据（供对话框使用）</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<HighlightEntry>> GetDocHighlights()
        => _docHighlights.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<HighlightEntry>)kv.Value.AsReadOnly());

    /// <summary>供对话框使用的跳转到指定块（强制翻页+选中）</summary>
    public async Task NavigateToBlock(int pageIndex, int blockIndex)
    {
        if (pageIndex < 0 || pageIndex >= TotalPages) return;

        if (CurrentPage != pageIndex)
        {
            _currentPage = pageIndex;
            this.RaisePropertyChanged(nameof(CurrentPage));
            this.RaisePropertyChanged(nameof(CurrentPageDisplay));
            UpdatePageNavCurrent();
            await RenderDisplayAsync();
            await LoadPageAsync();
        }

        if (blockIndex >= 0 && blockIndex < Blocks.Count)
            OnTextClicked(blockIndex);
    }

    private static string ConvertToSimplified(string text)
        => ChineseTextConverter.ToSimplified(text);
}

/// <summary>ViewModel 层的 OCR 文本块，附加显示 bbox</summary>
public class OcrTextBlockViewModel : ReactiveObject
{
    public double[] RawBbox { get; set; } = Array.Empty<double>();
    public double[] DisplayBbox { get; set; } = Array.Empty<double>();

    private string _text = "";
    /// <summary>规范文本（写回 JSON）</summary>
    public string Text
    {
        get => _text;
        set => this.RaiseAndSetIfChanged(ref _text, value);
    }

    private string _editorText = "";
    /// <summary>右侧编辑框绑定（可为繁→简预览）</summary>
    public string EditorText
    {
        get => _editorText;
        set => this.RaiseAndSetIfChanged(ref _editorText, value);
    }

    /// <summary>兼容旧绑定名</summary>
    public string PreviewText
    {
        get => EditorText;
        set => EditorText = value;
    }

    public string? Label { get; set; }
    public int Order { get; set; }
    public int Index { get; set; }  // 块序号 (1-based)
    public OcrTextSourceKind SourceKind { get; set; } = OcrTextSourceKind.Layout;
    public int SourceIndex { get; set; } = -1;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            this.RaiseAndSetIfChanged(ref _isHighlighted, value);
            this.RaisePropertyChanged(nameof(EffectiveHighlightColor));
        }
    }

    private string? _userHighlightColor;
    /// <summary>用户手动高亮的颜色（null = 无手动高亮）</summary>
    public string? UserHighlightColor
    {
        get => _userHighlightColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _userHighlightColor, value);
            this.RaisePropertyChanged(nameof(EffectiveHighlightColor));
        }
    }

    /// <summary>有效的块级底色（整块匹配时使用）</summary>
    public string? EffectiveHighlightColor
    {
        get
        {
            if (!string.IsNullOrEmpty(UserHighlightColor))
                return UserHighlightColor;
            if (IsHighlighted)
                return "#CCFFCC";
            return null;
        }
    }

    private IReadOnlyList<HighlightSegment> _highlightedSegments = Array.Empty<HighlightSegment>();
    public IReadOnlyList<HighlightSegment> HighlightedSegments
    {
        get => _highlightedSegments;
        set
        {
            this.RaiseAndSetIfChanged(ref _highlightedSegments, value);
            this.RaisePropertyChanged(nameof(HasHighlights));
        }
    }

    /// <summary>是否包含高亮分段（供编辑模式下显示块级提示）。</summary>
    public bool HasHighlights => HighlightedSegments.Any(s => s.BgHex != null);

    private string? _currentSelectedText;
    /// <summary>用户在当前块 TextBox 中选中的文字（由 View 层 SelectionChanged 事件更新）。</summary>
    public string? CurrentSelectedText
    {
        get => _currentSelectedText;
        set => this.RaiseAndSetIfChanged(ref _currentSelectedText, value);
    }

    private bool _isEditing;
    /// <summary>是否处于编辑模式（显示 TextBox）。false=只读 TextBlock 模式。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEditing, value);
            this.RaisePropertyChanged(nameof(IsViewing));
        }
    }
    /// <summary>查看模式（非编辑模式）。</summary>
    public bool IsViewing => !IsEditing;

    public void SetCanonicalText(string text)
    {
        Text = text ?? "";
        EditorText = Text;
    }

    /// <summary>按繁简/句读模式从规范文本刷新编辑框</summary>
    public void RefreshEditorText(bool showConverted, Func<string, string> convert, string? punctuatedBase = null)
    {
        var baseText = punctuatedBase ?? Text ?? "";
        EditorText = showConverted ? convert(baseText) : baseText;
    }

    /// <summary>
    /// 将编辑框写回规范文本。
    /// 繁简/句读预览模式下：仅当编辑内容相对「当前预览基线」有变化时才覆盖 Text。
    /// </summary>
    public void CommitEditorToText(bool editorWasConverted, Func<string, string> convert, string? punctuatedBase = null)
    {
        var edited = EditorText ?? "";
        var baselineSource = punctuatedBase ?? Text ?? "";
        if (editorWasConverted)
        {
            var baseline = convert(baselineSource);
            if (!string.Equals(edited, baseline, StringComparison.Ordinal))
                Text = edited;
        }
        else if (punctuatedBase != null)
        {
            if (!string.Equals(edited, punctuatedBase, StringComparison.Ordinal))
                Text = edited;
        }
        else
        {
            Text = edited;
        }
    }
}
