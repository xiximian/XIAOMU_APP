using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xiaomuocr.Core;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;
using Xiaomuocr.Core.ViewModels;
using Xiaomuocr.Views.Views;

namespace Xiaomuocr.App;

public partial class MainWindow : Window
{
    private IServiceProvider? _services;
    private IAuthService? _auth;
    private IApiService? _api;
    private ITokenStorage? _tokenStorage;
    private ILibraryService? _library;
    private IPdfRenderService? _pdfRender;
    private IOcrResultService? _ocrResult;
    private IAppLogService? _appLog;
    private IPdfOcrPipelineService? _pdfOcr;
    private IConnectivityService? _connectivity;
    private string? _loggedInUser;
    private AccountViewModel? _accountVm;
    private LibraryViewModel? _libraryVm;
    private int _libraryCountRefreshRunning;
    private Window? _logWindow;
    private TextBox? _logTextBox;
    private bool _isOffline;

    /// <summary>已打开的 PDF 阅读标签（切换侧栏菜单时保活，不销毁）。</summary>
    private readonly List<DocumentTab> _docTabs = new();
    private string? _activeDocTabId;

    private sealed class DocumentTab
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required LibraryItem Item { get; init; }
        public required PdfViewerViewModel Vm { get; init; }
        public required PdfViewerView View { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Initialize(IServiceProvider services)
    {
        _services = services;
        _auth = services.GetRequiredService<IAuthService>();
        _api = services.GetRequiredService<IApiService>();
        _tokenStorage = services.GetRequiredService<ITokenStorage>();
        _library = services.GetRequiredService<ILibraryService>();
        _pdfRender = services.GetRequiredService<IPdfRenderService>();
        _ocrResult = services.GetRequiredService<IOcrResultService>();
        _appLog = services.GetRequiredService<IAppLogService>();
        _pdfOcr = services.GetRequiredService<IPdfOcrPipelineService>();
        _connectivity = services.GetRequiredService<IConnectivityService>();
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
        _appLog.Info("小木史料阅读器 已启动");
        _appLog.Info($"日志文件: {_appLog.LogFilePath}");
    }

    // ---- Navigation methods ----

    public void ShowLogin()
    {
        CloseAllDocumentTabs();
        SidebarBorder.IsVisible = false;
        WelcomeLabel.Text = "";
        OfflineBadge.IsVisible = _isOffline;

        var vm = new LoginViewModel(_auth!, _api!, _tokenStorage!);
        vm.OnLoginSucceeded = () =>
        {
            _ = OnLoggedInAsync();
        };
        vm.OnGoToRegister = ShowRegister;
        vm.OnGoToResetPassword = ShowResetPassword;
        vm.OnOfflineReading = () =>
        {
            _appLog?.Info("用户选择离线阅读");
            EnterOfflineMode(null);
        };
        vm.OnWxLoginRequested = ShowWxLoginAsync;

        ShowShellContent(new LoginView { DataContext = vm });
        SetConnectedStatusText(forLoginScreen: true);
    }

    private async Task ShowWxLoginAsync()
    {
        var win = new WxLoginWindow(_auth!);
        // 与 PaymentWindow 相同：先启动拉码/轮询，再模态显示
        _ = win.StartAsync();
        await win.ShowDialog(this);
        if (win.LoginSuccess && win.Tokens != null)
        {
            _auth!.ApplyTokens(win.Tokens);
            _api!.SetAccessToken(win.Tokens.AccessToken);
            _api.SetRefreshToken(win.Tokens.RefreshToken);
            await _tokenStorage!.SaveTokensAsync(
                win.Tokens.AccessToken, win.Tokens.RefreshToken, win.Tokens.ExpiresAt);
            await OnLoggedInAsync();
        }
    }

    /// <summary>首次启动引导：用户尚未设置服务器地址。</summary>
    public void ShowServerSetup(string? errorMessage = null)
    {
        CloseAllDocumentTabs();
        SidebarBorder.IsVisible = false;
        WelcomeLabel.Text = "";
        OfflineBadge.IsVisible = false;

        var settings = _services!.GetRequiredService<ISettingsService>();
        var api = _services.GetRequiredService<IApiService>();
        var vm = new ServerSetupViewModel(settings, api)
        {
            ErrorMessage = errorMessage ?? "",
        };
        vm.OnSetupComplete = async () =>
        {
            _appLog?.Info("服务器地址已配置，开始恢复会话");
            await TryRestoreSessionAfterSetup();
        };
        vm.OnOfflineReading = () =>
        {
            _appLog?.Info("用户跳过服务器设置，进入离线阅读");
            EnterOfflineMode(null);
        };

        ShowShellContent(new ServerSetupView { DataContext = vm });
        StatusText.Text = "首次使用 — 请设置服务器地址";
    }

    /// <summary>服务器地址设置完成后，重试会话恢复流程。</summary>
    private async Task TryRestoreSessionAfterSetup()
    {
        var tokenStorage = _services!.GetRequiredService<ITokenStorage>();
        var api = _services.GetRequiredService<IApiService>();
        var auth = _services.GetRequiredService<IAuthService>();
        var settings = _services.GetRequiredService<ISettingsService>();
        var connectivity = _services.GetRequiredService<IConnectivityService>();

        api.BaseUrl = await settings.GetServerUrlAsync() ?? "";

        var (access, refresh, expiresAt) = await tokenStorage.LoadTokensAsync();
        if (string.IsNullOrEmpty(access))
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowLogin());
            return;
        }

        api.SetAccessToken(access);
        api.SetRefreshToken(refresh!);

        try
        {
            var user = await auth.GetCurrentUserAsync();
            await tokenStorage.SaveUsernameAsync(user.Username);
            connectivity.StartBackgroundCheck(async () =>
            {
                try { await auth.GetCurrentUserAsync(); return true; }
                catch { return false; }
            });
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loggedInUser = user.Username;
                SidebarBorder.IsVisible = true;
                WelcomeLabel.Text = $"欢迎, {user.Username}";
                ShowLibrary();
                SetConnectedStatusText();
            });
            await StartTaskSyncAsync();
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowLogin());
        }
    }

    public void ShowRegister()
    {
        CloseAllDocumentTabs();
        SidebarBorder.IsVisible = false;
        WelcomeLabel.Text = "";

        var vm = new RegisterViewModel(_auth!, _api!, _tokenStorage!);
        vm.OnRegisterSucceeded = () =>
        {
            _ = OnPhoneRegisterSucceededAsync();
        };
        vm.OnBackToLogin = ShowLogin;

        ShowShellContent(new RegisterView { DataContext = vm });
        StatusText.Text = "注册新账户";
    }

    /// <summary>
    /// 仅手机号注册成功路径：弹一次赠送提示，再进入主界面。
    /// 登录 / 微信扫码 / 会话恢复不得调用。
    /// </summary>
    private async Task OnPhoneRegisterSucceededAsync()
    {
        await ShowRegisterGiftDialogAsync();
        await OnLoggedInAsync();
    }

    private async Task ShowRegisterGiftDialogAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var okBtn = new Button
        {
            Content = "知道了",
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brush.Parse("#4A7C59"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 96,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };

        var dlg = new Window
        {
            Title = "注册成功",
            Width = 360,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(24, 20),
                Child = new Grid
                {
                    RowDefinitions = RowDefinitions.Parse("*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "首次注册赠送5元余额",
                            FontSize = 15,
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brush.Parse("#2D3A2C"),
                            [Grid.RowProperty] = 0,
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 16, 0, 0),
                            [Grid.RowProperty] = 1,
                            Children = { okBtn },
                        },
                    },
                },
            },
        };

        okBtn.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(true);

        await dlg.ShowDialog(this);
        await tcs.Task;
    }

    public void ShowResetPassword()
    {
        CloseAllDocumentTabs();
        SidebarBorder.IsVisible = false;
        WelcomeLabel.Text = "";

        var vm = new ResetPasswordViewModel(_auth!);
        vm.OnResetSucceeded = ShowLogin;
        vm.OnBackToLogin = ShowLogin;

        ShowShellContent(new ResetPasswordView { DataContext = vm });
        StatusText.Text = "重置密码";
    }

    private async Task OnLoggedInAsync()
    {
        try
        {
            var user = await _auth!.GetCurrentUserAsync();
            _loggedInUser = user.Username;
            WelcomeLabel.Text = $"欢迎, {user.Username}";
            // 缓存用户名供离线使用
            await _tokenStorage!.SaveUsernameAsync(user.Username);
        }
        catch
        {
            WelcomeLabel.Text = "已登录";
        }
        _isOffline = false;
        OfflineBadge.IsVisible = false;
        SidebarBorder.IsVisible = true;
        ShowLibrary();
        SetConnectedStatusText();
        await StartTaskSyncAsync();
    }

    /// <summary>状态栏显示「已连接服务器」，不暴露具体服务器地址。</summary>
    private void SetConnectedStatusText(bool forLoginScreen = false)
    {
        var hasServer = !string.IsNullOrWhiteSpace(_api?.BaseUrl);
        if (hasServer)
        {
            StatusText.Foreground = Avalonia.Media.Brushes.Green;
            StatusText.Text = "已连接服务器";
        }
        else if (forLoginScreen)
        {
            StatusText.Foreground = Avalonia.Media.Brushes.Gray;
            StatusText.Text = "请登录";
        }
    }

    public async Task StartTaskSyncAsync()
    {
        if (_services == null || _isOffline) return;
        try
        {
            var sync = _services.GetRequiredService<ITaskSyncService>();
            sync.StatusBarMessage -= OnTaskSyncStatus;
            sync.StatusBarMessage += OnTaskSyncStatus;
            sync.ResultsSynced -= OnTaskResultsSynced;
            sync.ResultsSynced += OnTaskResultsSynced;
            RecentTasksList.ItemsSource = sync.RecentTasks;
            await sync.StartAsync();
        }
        catch (Exception ex)
        {
            _appLog?.Warn($"TaskSync 启动失败: {ex.Message}");
        }
    }

    private void OnTaskSyncStatus(string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Foreground = Avalonia.Media.Brushes.Goldenrod;
            StatusText.Text = msg;
        });
    }

    private void OnTaskResultsSynced(string batchUuid)
    {
        _appLog?.Info($"TaskSync 结果已同步 batch={batchUuid[..Math.Min(8, batchUuid.Length)]}");
        Dispatcher.UIThread.Post(() => _ = RefreshOpenTabsForBatchAsync(batchUuid));
    }

    /// <summary>进入离线模式：跳过验证，直接进入文献库。</summary>
    public void UpdateBatchStatus(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(text))
            {
                StatusText.Foreground = Avalonia.Media.Brushes.Goldenrod;
                StatusText.Text = text;
            }
            else
            {
                StatusText.Foreground = Avalonia.Media.Brushes.Green;
                StatusText.Text = "批量识别完成";
            }
        });
    }

    public void EnterOfflineMode(string? cachedUsername)
    {
        _isOffline = true;
        _connectivity!.SetOffline();
        _loggedInUser = cachedUsername;

        SidebarBorder.IsVisible = true;
        OfflineBadge.IsVisible = true;

        if (!string.IsNullOrEmpty(cachedUsername))
            WelcomeLabel.Text = $"欢迎, {cachedUsername}";
        else
            WelcomeLabel.Text = "离线阅读";

        StatusText.Foreground = Avalonia.Media.Brushes.Gray;
        StatusText.Text = "离线模式 — 文献库可用，OCR 不可用";
        _appLog?.Info("进入离线模式");
        ShowLibrary();
    }

    /// <summary>允许登录页显示「离线阅读」入口。</summary>
    public void SetOfflineEntryEnabled(bool enabled)
    {
        if (ContentArea.Content is LoginView loginView && loginView.DataContext is LoginViewModel loginVm)
        {
            loginVm.IsOfflineEntryVisible = enabled;
        }
    }

    /// <summary>连接状态变化回调。</summary>
    private void OnConnectivityChanged(bool isOffline)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (isOffline && !_isOffline)
            {
                // 在线 → 离线
                _isOffline = true;
                OfflineBadge.IsVisible = true;
                StatusText.Text = "网络已断开 — 离线模式";
                _appLog?.Warn("网络连接已断开，进入离线模式");
            }
            else if (!isOffline && _isOffline)
            {
                // 离线 → 在线（后台自动恢复）
                _isOffline = false;
                OfflineBadge.IsVisible = false;
                StatusText.Foreground = Avalonia.Media.Brushes.Green;
                StatusText.Text = "网络已恢复 — 在线模式";
                _appLog?.Info("网络已恢复，切换到在线模式");
            }
        });
    }

    public void ShowAccount()
    {
        SidebarBorder.IsVisible = true;
        if (_accountVm != null)
        {
            // 已有缓存的ViewModel，复用并后台软刷新
            ShowShellContent(new AccountView { DataContext = _accountVm });
            _ = _accountVm.RefreshOrLoadAsync();
        }
        else
        {
            var balance = new BalanceService(_api!);
            var pricing = new PricingService(_api!);
            _accountVm = new AccountViewModel(_auth!, balance, pricing);
            ShowShellContent(new AccountView { DataContext = _accountVm });
        }
        StatusText.Text = "账户信息";
    }

    private void ShowHistory()
    {
        var ocr = _services!.GetRequiredService<IOcrService>();
        var vm = new TaskHistoryViewModel(ocr);
        ShowShellContent(new TaskHistoryView { DataContext = vm });
        StatusText.Text = "识别历史记录";
    }

    private void ShowSettings()
    {
        var settings = _services!.GetRequiredService<ISettingsService>();
        var vm = new SettingsViewModel(settings, _api!, new VersionService(_api!));
        ShowShellContent(new SettingsView { DataContext = vm });
        StatusText.Text = "系统设置";
    }

    private void ShowHelp()
    {
        ShowShellContent(new HelpView());
        StatusText.Text = "帮助";
    }

    private void ShowFeedback()
    {
        var feedback = _services!.GetRequiredService<IFeedbackService>();
        var vm = new FeedbackViewModel(feedback);
        ShowShellContent(new FeedbackView { DataContext = vm });
        StatusText.Text = "联系反馈";
    }

    public void ShowLibrary()
    {
        _appLog?.Info("切换到文献库");
        System.Diagnostics.Debug.WriteLine("[MainWindow] ShowLibrary called");
        SidebarBorder.IsVisible = true;

        if (_libraryVm == null)
        {
            _libraryVm = new LibraryViewModel(
                _library!,
                _services?.GetService<IPricingService>(),
                _services?.GetService<IImageDocumentService>(),
                _services?.GetService<ITaskSyncService>());
            _libraryVm.OpenItemRequested += (item) =>
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] OpenItemRequested: {item.Name}, path={item.PdfPath}");
                ShowPdfViewer(item);
            };
            _libraryVm.ScreenshotImportRequested += () => _ = RunScreenshotImportAsync(_libraryVm);
            _libraryVm.StartOcrRequested += async (item) =>
            {
                _appLog?.Info($"开始 OCR（后台提交）: {item.Name} ({item.TotalPages} 页)");
                StatusText.Text = $"OCR: {item.Name} — 上传提交中...";
                string? batchUuid = null;
                try
                {
                    var progress = new Progress<OcrProgress>(p =>
                    {
                        if (!string.IsNullOrEmpty(p.BatchUuid))
                            batchUuid = p.BatchUuid;
                        Dispatcher.UIThread.Post(() =>
                        {
                            StatusText.Text = p.IsError || p.IsCancelled
                                ? $"OCR: {p.Status}"
                                : $"OCR: {p.Status}";
                        });
                    });
                    // 只等到提交；识别与落盘由任务中心 / TaskSync 后台完成
                    await _pdfOcr!.RunOcrAsync(item, progress, waitForCompletion: false);
                    StatusText.Text = $"已提交识别: {item.Name}，后台进行中";
                    _appLog?.Info($"OCR 已提交: {item.Name}, batch={batchUuid}");
                    ShowTaskCenter(batchUuid);
                }
                catch (OperationCanceledException)
                {
                    StatusText.Text = $"OCR 已取消: {item.Name}";
                    _appLog?.Info($"OCR 已取消: {item.Name}");
                }
                catch (Exception ex)
                {
                    _appLog?.Error($"OCR 失败: {ex.Message}");
                    StatusText.Text = $"OCR 失败: {ex.Message}";
                }
            };
        }

        ShowShellContent(new LibraryView { DataContext = _libraryVm });
        StatusText.Text = "文献库";
        // 打开文献库时再触发一次静默校准（已在跑则立刻返回）
        StartSilentLibraryCountRefresh();
    }

    private async Task RunScreenshotImportAsync(LibraryViewModel vm)
    {
        var capture = _services?.GetService<IScreenCaptureService>();
        var imageDocs = _services?.GetService<IImageDocumentService>();
        if (capture == null || !capture.IsSupported || imageDocs == null)
        {
            vm.ErrorMessage = "当前环境不支持截图导入";
            return;
        }

        var prev = WindowState;
        WindowState = WindowState.Minimized;
        await Task.Delay(280);
        try
        {
            var rect = await ScreenshotOverlayWindow.PickRegionAsync();
            if (rect == null)
            {
                vm.StatusMessage = "已取消截图";
                return;
            }

            // 等蒙层从桌面消失后再截取，避免截到半透明遮罩
            await Task.Delay(120);

            var scale = Screens?.Primary?.Scaling ?? DesktopScaling;
            if (scale <= 0) scale = 1.0;

            var png = await capture.CaptureRegionAsPngAsync(
                rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height, scale);
            if (png == null || png.Length == 0)
            {
                vm.ErrorMessage = OperatingSystem.IsMacOS()
                    ? "截图失败。请在「系统设置 → 隐私与安全性 → 屏幕录制」中允许本应用后重试"
                    : "截图失败";
                return;
            }

            var name = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}";
            var item = await imageDocs.CreateFromImageBytesAsync(png, name, "截图导入");
            await vm.AddItemAsync(item);
            vm.StatusMessage = $"已导入截图: {name}";
            StatusText.Text = $"已导入截图: {name}";
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"截图导入失败: {ex.Message}";
            _appLog?.Error($"截图导入失败: {ex.Message}");
        }
        finally
        {
            WindowState = prev == WindowState.Minimized ? WindowState.Normal : prev;
            Activate();
        }
    }

    /// <summary>
    /// 启动后/进入文献库：后台静默校准各文献 OCR 完成页数，不挡 UI。
    /// 大量文献时限并发、分批写库；UI 就地打补丁，不清列表、不 IsBusy。
    /// </summary>
    public void StartSilentLibraryCountRefresh()
    {
        if (_library == null) return;
        if (Interlocked.CompareExchange(ref _libraryCountRefreshRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var changes = await _library.SilentSyncOcrDonePagesAsync().ConfigureAwait(false);
                if (changes.Count == 0)
                    return;

                _appLog?.Info($"文献库计数静默刷新: 更新 {changes.Count} 条");

                // 分片上 UI，避免一次改几百项卡顿
                const int chunk = 40;
                for (var i = 0; i < changes.Count; i += chunk)
                {
                    var slice = changes.Skip(i).Take(chunk).ToList();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _libraryVm?.ApplyOcrCountPatches(slice);
                    });
                    if (i + chunk < changes.Count)
                        await Task.Delay(8).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _appLog?.Info($"文献库计数静默刷新跳过: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _libraryCountRefreshRunning, 0);
            }
        });
    }

    public async void ShowPdfViewer(LibraryItem item)
    {
        System.Diagnostics.Debug.WriteLine($"[MainWindow] ShowPdfViewer: {item.Name}");
        SidebarBorder.IsVisible = true;
        StatusText.Text = $"加载文献: {item.Name}...";

        var existing = FindDocTab(item);
        if (existing != null)
        {
            await ActivateDocumentTabAsync(existing.Id);
            StatusText.Text = $"正在阅读: {item.Name}";
            return;
        }

        _appLog?.Info($"打开文献: {item.Name} (页数: {item.TotalPages})");
        var highlightSvc = _services!.GetRequiredService<IHighlightService>();
        var searchSvc = _services!.GetRequiredService<ISearchService>();
        var exportSvc = _services!.GetRequiredService<IFullTextExportService>();
        var noteSvc = _services!.GetRequiredService<INoteService>();
        var librarySvc = _services!.GetRequiredService<ILibraryService>();
        var pricingSvc = _services!.GetRequiredService<IPricingService>();
        var settingsSvc = _services!.GetRequiredService<ISettingsService>();
        var vm = new PdfViewerViewModel(_pdfRender!, _ocrResult!, _pdfOcr, item,
            highlightSvc, searchSvc, exportSvc, noteSvc, _appLog, librarySvc,
            pricingSvc, settingsSvc);
        vm.IsOffline = _isOffline;
        vm.OcrStatusChanged += msg =>
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = msg);
        };
        vm.BatchUploadSvc = _services!.GetRequiredService<IBatchUploadService>();
        vm.BatchOcrSvc = _services!.GetRequiredService<IBatchOcrService>();
        vm.BatchCacheSvc = _services!.GetRequiredService<IBatchCacheService>();
        vm.TaskSyncSvc = _services!.GetRequiredService<ITaskSyncService>();
        vm.UploadJobQueue = _services!.GetRequiredService<IUploadJobQueue>();
        vm.ApiSvc = _services!.GetRequiredService<IApiService>();
        vm.PunctuateSvc = _services!.GetRequiredService<IPunctuateService>();
        vm.PostToUiThread = action => Dispatcher.UIThread.Post(action);
        vm.NavigateToTaskCenter = uuid => Dispatcher.UIThread.Post(() => ShowTaskCenter(uuid));
        vm.OcrProgressUi = (title, docName, work) =>
            OcrProgressWindow.RunAsync(this, title, docName, work);
        var view = new PdfViewerView { DataContext = vm };

        var tab = new DocumentTab
        {
            Id = item.Id,
            Title = string.IsNullOrWhiteSpace(item.Name) ? "未命名文献" : item.Name,
            Item = item,
            Vm = vm,
            View = view,
        };
        _docTabs.Add(tab);
        RebuildDocTabBar();

        try
        {
            await ActivateDocumentTabAsync(tab.Id);
            await vm.OpenDocumentAsync(item.PdfPath, item.OutputDir);
            StatusText.Text = $"正在阅读: {item.Name}";
        }
        catch (Exception ex)
        {
            _appLog?.Error($"打开文献异常: {ex.Message}");
            StatusText.Text = $"打开失败: {ex.Message}";
        }
    }

    // ---- Document tabs (persist across sidebar menu switches) ----

    private void ShowShellContent(Control content)
    {
        ContentArea.Content = content;
        ContentArea.IsVisible = true;
        DocHost.IsVisible = false;
        RebuildDocTabBar();
    }

    private DocumentTab? FindDocTab(LibraryItem item)
    {
        return _docTabs.FirstOrDefault(t =>
            t.Id == item.Id
            || (!string.IsNullOrEmpty(item.PdfPath)
                && string.Equals(t.Item.PdfPath, item.PdfPath, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(item.OutputDir)
                && string.Equals(t.Vm.JsonDir, item.OutputDir, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task ActivateDocumentTabAsync(string tabId)
    {
        var tab = _docTabs.FirstOrDefault(t => t.Id == tabId);
        if (tab == null) return;

        _activeDocTabId = tabId;
        DocHost.Content = tab.View;
        DocHost.IsVisible = true;
        ContentArea.IsVisible = false;
        RebuildDocTabBar();

        // 切回标签：把渲染服务路径切回本 PDF（单例 PdfRender）
        await tab.Vm.ReactivateAsync();
    }

    private void CloseDocumentTab(string tabId)
    {
        var idx = _docTabs.FindIndex(t => t.Id == tabId);
        if (idx < 0) return;
        _docTabs.RemoveAt(idx);

        if (_activeDocTabId == tabId)
        {
            _activeDocTabId = null;
            DocHost.Content = null;
            if (_docTabs.Count > 0)
            {
                var next = _docTabs[Math.Min(idx, _docTabs.Count - 1)];
                _ = ActivateDocumentTabAsync(next.Id);
            }
            else
            {
                DocHost.IsVisible = false;
                ContentArea.IsVisible = true;
                if (ContentArea.Content == null)
                    ShowLibrary();
            }
        }
        RebuildDocTabBar();
    }

    private void CloseAllDocumentTabs()
    {
        _docTabs.Clear();
        _activeDocTabId = null;
        DocHost.Content = null;
        DocHost.IsVisible = false;
        ContentArea.IsVisible = true;
        RebuildDocTabBar();
    }

    private void RebuildDocTabBar()
    {
        if (DocTabPanel == null || DocTabBar == null) return;
        DocTabPanel.Children.Clear();
        DocTabBar.IsVisible = _docTabs.Count > 0;
        foreach (var tab in _docTabs)
        {
            var isActive = tab.Id == _activeDocTabId && DocHost.IsVisible;
            var tabBorder = new Border
            {
                Background = Avalonia.Media.Brush.Parse(isActive ? "#3D5A40" : "#5A6E5C"),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Padding = new Thickness(10, 5),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Tag = tab.Id,
            };
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            var title = TruncateTabTitle(tab.Title, 18);
            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = Avalonia.Media.Brushes.White,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var closeBtn = new Button
            {
                Content = "×",
                Background = Avalonia.Media.Brushes.Transparent,
                Foreground = Avalonia.Media.Brushes.White,
                Padding = new Thickness(4, 0),
                FontSize = 14,
                MinWidth = 20,
                Tag = tab.Id,
            };
            ToolTip.SetTip(closeBtn, "关闭");
            closeBtn.Click += OnDocTabCloseClick;
            row.Children.Add(titleBlock);
            row.Children.Add(closeBtn);
            tabBorder.Child = row;
            tabBorder.PointerPressed += OnDocTabPressed;
            DocTabPanel.Children.Add(tabBorder);
        }
    }

    private static string TruncateTabTitle(string title, int max)
    {
        if (string.IsNullOrEmpty(title) || title.Length <= max) return title;
        return title[..(max - 1)] + "…";
    }

    private void OnDocTabPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tabId }) return;
        if (e.Source is Button) return; // 关闭按钮自己处理
        e.Handled = true;
        _ = ActivateDocumentTabAsync(tabId);
        var tab = _docTabs.FirstOrDefault(t => t.Id == tabId);
        if (tab != null)
            StatusText.Text = $"正在阅读: {tab.Title}";
    }

    private void OnDocTabCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: string tabId })
            CloseDocumentTab(tabId);
    }

    private async Task RefreshOpenTabsForItemAsync(LibraryItem item)
    {
        var tab = FindDocTab(item);
        if (tab == null) return;
        await tab.Vm.RefreshOcrFromDiskAsync();
        if (tab.Id == _activeDocTabId && DocHost.IsVisible)
            StatusText.Text = $"OCR 结果已加载: {item.Name}";
    }

    private async Task RefreshOpenTabsForBatchAsync(string batchUuid)
    {
        try
        {
            var sync = _services?.GetService<ITaskSyncService>();
            var task = sync?.Tasks.FirstOrDefault(t => t.BatchUuid == batchUuid);
            var outputDir = task?.OutputDir;
            var docName = task?.DocumentName;

            foreach (var tab in _docTabs.ToList())
            {
                var match = (!string.IsNullOrEmpty(outputDir)
                             && string.Equals(tab.Vm.JsonDir, outputDir, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrEmpty(docName)
                                && string.Equals(tab.Title, docName, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrEmpty(docName)
                                && string.Equals(tab.Vm.DocumentName, docName, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;

                await tab.Vm.RefreshOcrFromDiskAsync();
                if (tab.Id == _activeDocTabId && DocHost.IsVisible)
                    StatusText.Text = $"OCR 结果已加载: {tab.Title}";
            }
        }
        catch (Exception ex)
        {
            _appLog?.Warn($"刷新打开文献 OCR 失败: {ex.Message}");
        }
    }

    // ---- Sidebar button handlers ----

    private void OnLibraryClick(object? sender, RoutedEventArgs e) => ShowLibrary();
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (_isOffline)
        {
            StatusText.Text = "离线模式下账户信息不可用";
            return;
        }
        ShowAccount();
    }
    private void OnTaskCenterClick(object? sender, RoutedEventArgs e)
    {
        if (_isOffline)
        {
            StatusText.Text = "离线模式下任务中心不可用";
            return;
        }
        ShowTaskCenter();
    }
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (_isOffline)
        {
            StatusText.Text = "离线模式下识别历史不可用";
            return;
        }
        ShowHistory();
    }
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => ShowSettings();
    private void OnHelpClick(object? sender, RoutedEventArgs e) => ShowHelp();
    private void OnFeedbackClick(object? sender, RoutedEventArgs e) => ShowFeedback();

    private void ShowTaskCenter(string? selectBatchUuid = null)
    {
        SidebarBorder.IsVisible = true;
        var sync = _services!.GetRequiredService<ITaskSyncService>();
        var vm = new TaskCenterViewModel(sync);
        if (!string.IsNullOrEmpty(selectBatchUuid))
            vm.SelectBatch(selectBatchUuid);
        ShowShellContent(new TaskCenterView { DataContext = vm });
        StatusText.Text = "任务中心";
    }

    private void OnRecentTaskPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: string uuid })
            ShowTaskCenter(uuid);
    }

    private void OnLogClick(object? sender, RoutedEventArgs e)
    {
        if (_appLog == null) return;

        // 窗口已存在 → 刷新内容并激活，不创建新窗口
        if (_logWindow is { IsVisible: true })
        {
            RefreshLogContent(_appLog);
            _logWindow.Activate();
            return;
        }

        _logWindow = new Window
        {
            Title = "运行日志 — 小木史料阅读器",
            Width = 750,
            Height = 450,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        _logWindow.Closed += (_, _) =>
        {
            _appLog.OnNewLog -= OnNewLogEntry;
            _logWindow = null;
            _logTextBox = null;
        };

        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var header = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12, 8),
        };
        var titleBlock = new TextBlock
        {
            FontSize = 14, FontWeight = Avalonia.Media.FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        header.Children.Add(titleBlock);
        var clearBtn = new Button
        {
            Content = "清空", Padding = new Thickness(10, 4),
            Background = Avalonia.Media.Brushes.White,
        };
        clearBtn.Click += (_, _) =>
        {
            _appLog?.Clear();
            if (_logTextBox != null) _logTextBox.Text = "";
            titleBlock.Text = "📋 运行日志 (共 0 条)";
        };
        header.Children.Add(clearBtn);
        var refreshBtn = new Button
        {
            Content = "刷新", Padding = new Thickness(10, 4),
            Background = Avalonia.Media.Brushes.White,
        };
        refreshBtn.Click += (_, _) =>
        {
            if (_appLog != null) { LoadAllLogs(_appLog); titleBlock.Text = $"📋 运行日志 (共 {_appLog.GetRecentLogs(500).Count} 条) — 实时更新中"; }
        };
        header.Children.Add(refreshBtn);
        var closeBtn = new Button
        {
            Content = "关闭", Padding = new Thickness(10, 4),
            Background = Avalonia.Media.Brushes.White,
        };
        closeBtn.Click += (_, _) => _logWindow?.Close();
        header.Children.Add(closeBtn);
        grid.Children.Add(header);

        _logTextBox = new TextBox
        {
            FontFamily = "Consolas,Microsoft YaHei",
            FontSize = 12, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 12),
        };
        Grid.SetRow(_logTextBox, 1);
        grid.Children.Add(_logTextBox);

        // 加载已有日志
        LoadAllLogs(_appLog);
        titleBlock.Text = $"📋 运行日志 (共 {_appLog.GetRecentLogs(500).Count} 条) — 实时更新中";

        // 自动刷新：每新增一条日志就追加到 TextBox
        _appLog.OnNewLog += OnNewLogEntry;

        _logWindow.Content = grid;
        _logWindow.Show(this);
    }

    private void LoadAllLogs(IAppLogService appLog)
    {
        if (_logTextBox == null) return;
        var logs = appLog.GetRecentLogs(500);
        var sb = new System.Text.StringBuilder();
        foreach (var log in logs)
        {
            var level = log.Level switch
            {
                Core.Services.LogLevel.Error => "ERR",
                Core.Services.LogLevel.Warn => "WRN",
                Core.Services.LogLevel.Info => "INF",
                _ => "DBG",
            };
            sb.AppendLine($"{log.Time:HH:mm:ss} [{level}] {log.Message}");
        }
        _logTextBox.Text = sb.Length > 0 ? sb.ToString() : "暂无日志";
        _logTextBox.CaretIndex = _logTextBox.Text.Length;
    }

    private void OnNewLogEntry(LogEntry entry)
    {
        if (_logTextBox == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var level = entry.Level switch
            {
                Core.Services.LogLevel.Error => "ERR",
                Core.Services.LogLevel.Warn => "WRN",
                Core.Services.LogLevel.Info => "INF",
                _ => "DBG",
            };
            _logTextBox.Text += $"{entry.Time:HH:mm:ss} [{level}] {entry.Message}\n";
            _logTextBox.CaretIndex = _logTextBox.Text.Length;
        });
    }

    private void RefreshLogContent(IAppLogService appLog)
    {
        LoadAllLogs(appLog);
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _services?.GetService<ITaskSyncService>()?.Stop();
            RecentTasksList.ItemsSource = null;
        }
        catch { }

        await _tokenStorage!.ClearTokensAsync();
        _api!.SetAccessToken(null!);
        _api.SetRefreshToken(null!);
        _loggedInUser = null;
        _accountVm = null;
        _libraryVm = null;
        _isOffline = false;
        OfflineBadge.IsVisible = false;
        CloseAllDocumentTabs();
        ShowLogin();
    }

    private CancellationTokenSource? _updateDownloadCts;
    private CancellationTokenSource? _forceUpdateSnoozeCts;
    private bool _updatePromptOpen;

    /// <summary>
    /// 查到新版本后：静默后台下载，完成后再提示安装。
    /// 强制更新可预约「10 分钟后再更新」；用户直接退出无妨，下次启动会继续。
    /// </summary>
    public async void BeginSilentUpdateFlow(VersionCheckResult update)
    {
        var updateService = _services?.GetRequiredService<IUpdateService>();
        if (updateService == null) return;

        _updateDownloadCts?.Cancel();
        _updateDownloadCts = new CancellationTokenSource();
        var ct = _updateDownloadCts.Token;

        try
        {
            // 已有合法缓存则立刻提示；否则后台静默下载（不弹窗、不挡操作）
            string setupPath;
            if (updateService.TryGetCachedPackage(update, out var cached))
            {
                setupPath = cached;
            }
            else
            {
                _appLog?.Info($"[更新] 开始静默下载 {update.LatestVersion}");
                ShowUpdateToast($"新版本 {update.LatestVersion}", "正在连接下载…", 0);
                var progress = new Progress<UpdateProgress>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                        ShowUpdateToast($"正在下载新版本 {update.LatestVersion}", p.Message, p.Percent));
                });
                setupPath = await updateService.EnsurePackageDownloadedAsync(update, progress, ct);
                _appLog?.Info($"[更新] 静默下载完成: {setupPath}");
            }

            HideUpdateToast();
            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!IsVisible) return;
                await ShowReadyToInstallDialogAsync(update, setupPath, updateService);
            });
        }
        catch (OperationCanceledException)
        {
            HideUpdateToast();
        }
        catch (Exception ex)
        {
            HideUpdateToast();
            _appLog?.Warn($"[更新] 静默下载失败: {ex.Message}");
        }
    }

    private void ShowUpdateToast(string title, string message, int percent)
    {
        if (UpdateToast == null) return;
        UpdateToast.IsVisible = true;
        if (UpdateToastTitle != null)
            UpdateToastTitle.Text = title;
        if (UpdateToastText != null)
            UpdateToastText.Text = string.IsNullOrWhiteSpace(message) ? "正在下载安装包…" : message;
        if (UpdateToastProgress != null)
            UpdateToastProgress.Value = Math.Clamp(percent, 0, 100);
    }

    private void HideUpdateToast()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (UpdateToast != null)
                UpdateToast.IsVisible = false;
        });
    }

    private async Task ShowReadyToInstallDialogAsync(
        VersionCheckResult update, string setupPath, IUpdateService updateService)
    {
        if (_updatePromptOpen) return;
        _updatePromptOpen = true;

        try
        {
            var win = new Window
            {
                Title = update.ForceUpdate
                    ? "必须更新 — 小木史料阅读器"
                    : "更新已就绪 — 小木史料阅读器",
                Width = 480,
                Height = update.ForceUpdate ? 340 : 320,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Topmost = true,
                ShowInTaskbar = true,
                WindowState = WindowState.Normal,
            };
            // 主窗口若被遮挡/最小化，先恢复再弹更新框，避免用户错过
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            win.Opened += (_, _) =>
            {
                win.WindowState = WindowState.Normal;
                win.Activate();
            };

            var sp = new StackPanel { Margin = new Thickness(24, 20), Spacing = 12 };

            var autoInstall = updateService.SupportsAutoInstall;
            sp.Children.Add(new TextBlock
            {
                Text = update.ForceUpdate
                    ? $"新版本 {update.LatestVersion} 已下载完成。\n当前版本 {Constants.AppVersion} 需要更新后才能继续获得完整支持。"
                    : autoInstall
                        ? $"新版本 {update.LatestVersion} 已下载完成\n（当前版本 {Constants.AppVersion}）\n\n是否立即安装并重启？"
                        : $"新版本 {update.LatestVersion} 已下载完成\n（当前版本 {Constants.AppVersion}）\n\n请解压安装包并手动替换应用后重新打开。",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });

            if (!string.IsNullOrEmpty(update.Changelog))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = $"更新内容:\n{update.Changelog}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#5C5344")),
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 90,
                });
            }

            var statusLabel = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#5C5344")),
                TextWrapping = TextWrapping.Wrap,
            };
            sp.Children.Add(statusLabel);

            var btnCol = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 10,
                Margin = new Thickness(0, 8, 0, 0),
            };

            void SetButtonsEnabled(bool enabled)
            {
                foreach (var child in btnCol.Children)
                {
                    if (child is Button b) b.IsEnabled = enabled;
                    else if (child is Panel panel)
                    {
                        foreach (var inner in panel.Children)
                        {
                            if (inner is Button ib) ib.IsEnabled = enabled;
                        }
                    }
                }
            }

            async Task ApplyNowAsync()
            {
                try
                {
                    statusLabel.Text = autoInstall ? "正在启动安装器..." : "正在打开安装包位置...";
                    SetButtonsEnabled(false);
                    await updateService.LaunchInstallerAsync(setupPath);
                    win.Close(true);
                    await ShutdownForUpdateAsync();
                }
                catch (Exception ex)
                {
                    statusLabel.Text = autoInstall
                        ? $"启动安装失败: {ex.Message}"
                        : $"打开失败: {ex.Message}（请到临时目录手动解压安装包）";
                    SetButtonsEnabled(true);
                }
            }

            var btnNow = new Button
            {
                Content = autoInstall ? "立即更新" : "打开安装包位置",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Background = new SolidColorBrush(Color.Parse("#4A7C59")),
                Foreground = Brushes.White,
                Padding = new Thickness(24, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth = 160,
            };
            btnNow.Click += async (_, _) => await ApplyNowAsync();
            btnCol.Children.Add(btnNow);

            if (update.ForceUpdate)
            {
                var laterRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 10,
                };
                var btnLater10 = new Button
                {
                    Content = "10 分钟后再更新",
                    FontSize = 13,
                    Padding = new Thickness(14, 8),
                };
                btnLater10.Click += (_, _) =>
                {
                    ScheduleForceUpdateSnooze(TimeSpan.FromMinutes(10), update, setupPath, updateService);
                    win.Close(false);
                };
                laterRow.Children.Add(btnLater10);

                var btnLater30 = new Button
                {
                    Content = "30 分钟后再更新",
                    FontSize = 13,
                    Padding = new Thickness(14, 8),
                };
                btnLater30.Click += (_, _) =>
                {
                    ScheduleForceUpdateSnooze(TimeSpan.FromMinutes(30), update, setupPath, updateService);
                    win.Close(false);
                };
                laterRow.Children.Add(btnLater30);
                btnCol.Children.Add(laterRow);
            }
            else
            {
                var btnLater = new Button
                {
                    Content = "稍后提醒",
                    FontSize = 14,
                    Padding = new Thickness(20, 8),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                btnLater.Click += (_, _) => win.Close(false);
                btnCol.Children.Add(btnLater);
            }

            sp.Children.Add(btnCol);

            if (update.ForceUpdate)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "也可直接退出程序；下次启动会继续提示更新。",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#8B8370")),
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            win.Content = sp;
            await win.ShowDialog(this);
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    /// <summary>强制更新延后：本会话内到期再弹窗；中途退出则下次启动继续。</summary>
    private void ScheduleForceUpdateSnooze(
        TimeSpan delay, VersionCheckResult update, string setupPath, IUpdateService updateService)
    {
        _forceUpdateSnoozeCts?.Cancel();
        _forceUpdateSnoozeCts = new CancellationTokenSource();
        var ct = _forceUpdateSnoozeCts.Token;
        _appLog?.Info($"[更新] 强制更新已延后 {delay.TotalMinutes:0} 分钟");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!IsVisible) return;
                    await ShowReadyToInstallDialogAsync(update, setupPath, updateService);
                });
            }
            catch (OperationCanceledException)
            {
                // 正常取消（再次预约或进程退出）
            }
        }, ct);
    }

    /// <summary>
    /// 更新前优雅关闭：释放资源 → 终止子进程 → 关闭窗体 → 退出。
    /// 确保 Velopack 安装器能顺利替换旧版文件（current 目录不被占用）。
    /// </summary>
    private async Task ShutdownForUpdateAsync()
    {
        try
        {
            _updateDownloadCts?.Cancel();
            _forceUpdateSnoozeCts?.Cancel();

            // 1. 释放 ServiceProvider（关闭 SQLite 连接等 IDisposable 资源）
            //    TokenStorage / SettingsService / LibraryService 等持有 SqliteConnection
            try { App.Services?.DisposeAsync().AsTask().Wait(3000); } catch { }

            // 1b. 连接已关闭后，把文献库备份到安装根之外（%LocalAppData%\xiaomu-ocr-user\）
            //     Setup.exe 重装会删除整个 packId 目录 xiaomu-ocr\，旧 data\ 放在里面会被一起清掉
            try { ClientDataPaths.BackupBeforeUpdate(); } catch { }

            // 2. 刷写 Serilog 日志（释放日志文件句柄）
            try { Serilog.Log.CloseAndFlush(); } catch { }

            // 3. 杀掉所有 PdfHost 子进程（避免锁定 app 目录下的 PdfHost.exe）
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Xiaomuocr.PdfHost"))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }

            // 4. 关闭主窗口，触发 Avalonia 关闭 → 释放 UI 资源
            Close();

            // 5. 等待资源释放（bat 脚本会额外等待进程退出，此处只需短暂等待）
            await Task.Delay(500);
        }
        catch { }

        // 6. 最终兜底：硬退出
        Environment.Exit(0);
    }
}
