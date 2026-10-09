using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.App;

public partial class App : Application
{
    public static ServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // 捕获未处理异常，写入崩溃日志（原生闪退仍可能捕不到）
        var crashLog = System.IO.Path.Combine(System.AppContext.BaseDirectory, "xiaomuocr_crash.log");
        void WriteCrash(string where, Exception? ex)
        {
            try
            {
                // 仅写异常消息和类型，不写堆栈（堆栈可能含文件路径等敏感信息）
                var safeMsg = ex != null
                    ? $"{ex.GetType().Name}: {ex.Message}"
                    : "null";
                System.IO.File.AppendAllText(crashLog,
                    $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{where}] {safeMsg}\n");
            }
            catch { }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("TaskScheduler", e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            WriteCrash("UIThread", e.Exception);
            e.Handled = true; // 尽量不闪退
        };

        try
        {
            // ---- DI ----
            var services = new ServiceCollection();
            var baseDir = System.AppContext.BaseDirectory;
            System.IO.Directory.CreateDirectory(baseDir);
            // 用户数据（文献库/登录/设置）放稳定目录，避免 Velopack 更新清空
            var dbPath = Core.Services.ClientDataPaths.GetDatabasePath();
            // 启动即清理冗余 db 备份 / data 旁路副本（失败忽略）
            Core.Services.ClientDataPaths.CleanupRedundantDataFiles();
            // 触发 imports 根解析（有非 C: 盘时落到该盘）
            _ = Core.Services.ClientDataPaths.GetImportsDirectory();

            // 大批次 status 曾可达十余 MB；瘦身后仍放宽超时，避免偶发慢查误杀。
            // 必须限制连接寿命：默认无限复用，本机后端重启/半开 TCP 后会出现
            // 「客户端一直等到 Timeout、服务端 access.log 完全没有记录」。
            services.AddSingleton(_ =>
            {
                var handler = new System.Net.Http.SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    MaxConnectionsPerServer = 20,
                };
                return new System.Net.Http.HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(180),
                };
            });
            services.AddSingleton<Core.Services.ITokenStorage>(_ => new Core.Services.TokenStorage(dbPath));
            services.AddSingleton<Core.Services.ISettingsService>(_ => new Core.Services.SettingsService(dbPath));
            services.AddSingleton<Core.Services.IApiService, Core.Services.ApiService>();
            services.AddSingleton<Core.Services.IAuthService, Core.Services.AuthService>();
            services.AddSingleton<Core.Services.IBalanceService, Core.Services.BalanceService>();
            services.AddSingleton<Core.Services.IPricingService, Core.Services.PricingService>();
            services.AddSingleton<Core.Services.IFeedbackService, Core.Services.FeedbackService>();
            services.AddSingleton<Core.Services.IPunctuateService, Core.Services.PunctuateService>();
            services.AddSingleton<Core.Services.IOcrService, Core.Services.OcrService>();
            services.AddSingleton<Core.Services.IOssService, Core.Services.OssService>();
            services.AddSingleton<Core.Services.IVersionService, Core.Services.VersionService>();
            services.AddSingleton<Core.Services.IUpdateService, Core.Services.UpdateService>();
            services.AddSingleton<Core.Services.ILibraryService>(_ => new Core.Services.LibraryService(dbPath));
            services.AddSingleton<Core.Services.IImageDocumentService, Core.Services.ImageDocumentService>();
            services.AddSingleton<Core.Services.IScreenCaptureService, Core.Services.ScreenCaptureService>();
            services.AddSingleton<Core.Services.IOcrResultService, Core.Services.OcrResultService>();
            // 日志仍写在安装目录（不迁到 data）
            services.AddSingleton<Core.Services.IAppLogService>(_ => new Core.Services.AppLogService());
            services.AddSingleton<Core.Services.IPdfRenderService>(sp =>
                new Core.Services.PdfRenderService(sp.GetRequiredService<Core.Services.IAppLogService>()));
            services.AddSingleton<Core.Services.IPdfOcrPipelineService, Core.Services.PdfOcrPipelineService>();
            services.AddSingleton<Core.Services.IHighlightService, Core.Services.HighlightService>();
            services.AddSingleton<Core.Services.ISearchService, Core.Services.SearchService>();
            services.AddSingleton<Core.Services.IFullTextExportService, Core.Services.FullTextExportService>();
            services.AddSingleton<Core.Services.INoteService, Core.Services.NoteService>();
            services.AddSingleton<IConnectivityService, ConnectivityService>();
            services.AddSingleton<IBatchUploadService, BatchUploadService>();
            services.AddSingleton<IBatchOcrService, BatchOcrService>();
            services.AddSingleton<IBatchCacheService>(_ => new BatchCacheService(dbPath));
            services.AddSingleton<ITaskSyncService, TaskSyncService>();
            services.AddSingleton<IUploadJobQueue, UploadJobQueue>();
            Services = services.BuildServiceProvider();

            // ---- Window ----
            var window = new MainWindow();
            window.Initialize(Services);
            window.ShowLogin(); // Show login page immediately
            desktop.MainWindow = window;

            // ---- Background session restore ----
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try { await TryRestoreSession(window); }
                catch { /* already showing login */ }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            desktop.MainWindow = new Window
            {
                Title = "启动失败 - 小木史料阅读器",
                Width = 500, Height = 300,
                Content = new TextBlock
                {
                    Text = $"程序启动失败:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    Margin = new Thickness(20),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task TryRestoreSession(MainWindow window)
    {
        var tokenStorage = Services!.GetRequiredService<Core.Services.ITokenStorage>();
        var api = Services.GetRequiredService<Core.Services.IApiService>();
        var auth = Services.GetRequiredService<Core.Services.IAuthService>();
        var settings = Services.GetRequiredService<Core.Services.ISettingsService>();
        var connectivity = Services.GetRequiredService<IConnectivityService>();
        var appLog = Services.GetRequiredService<Core.Services.IAppLogService>();

        // 启动诊断日志：记录关键配置信息，便于排查服务器地址问题
        appLog.Info($"[诊断] 版本: {Core.Constants.AppVersion}, 路径: {System.AppContext.BaseDirectory}");
        appLog.Info($"[诊断] DB(用户): {Core.Services.ClientDataPaths.GetDatabasePath()}");
        appLog.Info($"[诊断] 旧DB(Velopack data): {Core.Services.ClientDataPaths.GetLegacyVelopackDataDbPath()} 存在={System.IO.File.Exists(Core.Services.ClientDataPaths.GetLegacyVelopackDataDbPath())}");
        appLog.Info($"[诊断] 旧DB(current): {Core.Services.ClientDataPaths.GetLegacyDatabasePath()} 存在={System.IO.File.Exists(Core.Services.ClientDataPaths.GetLegacyDatabasePath())}");
        appLog.Info($"[诊断] 备份目录: {Core.Services.ClientDataPaths.GetBackupDirectory()}");
        appLog.Info($"[诊断] imports: {Core.Services.ClientDataPaths.GetImportsRootForDiagnostics()}");
        var sdtPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "server_default.txt");
        appLog.Info($"[诊断] server_default.txt 存在: {System.IO.File.Exists(sdtPath)}");

        // 后台再清一次备份，并清掉无库记录的孤儿 imports 目录
        _ = Task.Run(async () =>
        {
            try
            {
                Core.Services.ClientDataPaths.CleanupRedundantDataFiles();
                var library = Services?.GetService<Core.Services.ILibraryService>();
                if (library == null) return;
                var items = await library.GetItemsAsync().ConfigureAwait(false);
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.Id))
                        keep.Add(item.Id);
                    var managedDir = Core.Services.ClientDataPaths.TryGetManagedImportItemDirectory(item.PdfPath);
                    if (!string.IsNullOrEmpty(managedDir))
                    {
                        var folderName = System.IO.Path.GetFileName(managedDir.TrimEnd('\\', '/'));
                        if (!string.IsNullOrEmpty(folderName))
                            keep.Add(folderName);
                    }
                }
                var orphans = Core.Services.ClientDataPaths.CleanupOrphanImportDirectories(keep);
                if (orphans > 0)
                    appLog.Info($"[清理] 已删除 {orphans} 个无文献对应的 imports 孤儿目录");
            }
            catch (Exception ex)
            {
                appLog.Warn($"[清理] 后台清理失败: {ex.Message}");
            }
        });

        // 检查是否已配置服务器地址
        var serverUrl = await settings.GetServerUrlAsync();
        var settingsSvc = settings as Core.Services.SettingsService;
        if (settingsSvc != null)
            appLog.Info($"[诊断] 决策路径: {settingsSvc.LastServerUrlDecision}");
        appLog.Info($"[诊断] GetServerUrlAsync 返回: {(string.IsNullOrEmpty(serverUrl) ? "(空/未配置)" : serverUrl)}");

        if (string.IsNullOrEmpty(serverUrl))
        {
            // 首次启动：未配置服务器 → 显示服务器设置页面
            await Dispatcher.UIThread.InvokeAsync(() =>
                window.ShowServerSetup());
            return;
        }
        api.BaseUrl = serverUrl;
        appLog.Info($"[诊断] 最终 BaseUrl: {api.BaseUrl}");

        var (access, refresh, expiresAt) = await tokenStorage.LoadTokensAsync();
        if (string.IsNullOrEmpty(access))
        {
            // 无缓存 token，启用登录页离线入口
            await Dispatcher.UIThread.InvokeAsync(() =>
                window.SetOfflineEntryEnabled(true));
            return;
        }

        api.SetAccessToken(access);
        api.SetRefreshToken(refresh!);

        try
        {
            // 尝试在线验证 token
            var user = await auth.GetCurrentUserAsync();

            // 缓存用户名（供后续离线使用）
            await tokenStorage.SaveUsernameAsync(user.Username);

            // 启动后台连接检测
            connectivity.StartBackgroundCheck(async () =>
            {
                try
                {
                    await auth.GetCurrentUserAsync();
                    return true;
                }
                catch { return false; }
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.SidebarBorder.IsVisible = true;
                window.WelcomeLabel.Text = $"欢迎, {user.Username}";
                window.ShowLibrary();
            });

            // 任务中心：登录同步 + 定时轮询（替代旧 RestorePendingBatches）
            await window.StartTaskSyncAsync();

            // 启动后静默校准文献 OCR 页数（不挡 UI；文献很多时限并发）
            window.StartSilentLibraryCountRefresh();

            // 启动后静默检查客户端更新（不阻塞）
            _ = CheckVersionOnStartup(window);
        }
        catch (Exception ex)
        {
            // 鉴权失败（token 被服务端拒绝 / 刷新失败）≠ 离线：清 token 回登录页
            if (ex is ApiException apiEx && (apiEx.StatusCode == 401 || apiEx.StatusCode == 403))
            {
                appLog.Warn($"会话恢复失败（鉴权 {apiEx.StatusCode}）: {apiEx.Message}，清除 token 并显示登录页");
                await tokenStorage.ClearTokensAsync();
                await Dispatcher.UIThread.InvokeAsync(() =>
                    window.SetOfflineEntryEnabled(true));
                return;
            }

            // 网络不通或服务器不可达 → 检查本地 token 是否过期
            appLog.Warn($"会话恢复失败（疑似网络）: {ex.GetType().Name}: {ex.Message}");
            bool tokenValid = expiresAt.HasValue && expiresAt.Value > DateTime.UtcNow;
            if (tokenValid)
            {
                // Token 未过期 → 进入离线模式
                var cachedUsername = await tokenStorage.LoadUsernameAsync();

                // 启动后台连接检测（尝试自动恢复）
                connectivity.StartBackgroundCheck(async () =>
                {
                    try
                    {
                        await auth.GetCurrentUserAsync();
                        return true;
                    }
                    catch { return false; }
                });

                await Dispatcher.UIThread.InvokeAsync(() =>
                    window.EnterOfflineMode(cachedUsername));
            }
            else
            {
                // Token 已过期 → 清除旧 token，留在登录页
                await tokenStorage.ClearTokensAsync();
                await Dispatcher.UIThread.InvokeAsync(() =>
                    window.SetOfflineEntryEnabled(true));
            }
        }
    }

    /// <summary>
    /// 启动后：查版本 → 静默后台下载 → 下载完成再弹窗提示安装。
    /// 下载失败或无更新时静默跳过；用户退出不影响，下次启动会再走一遍。
    /// </summary>
    private static async Task CheckVersionOnStartup(MainWindow window)
    {
        try
        {
            var updateService = Services!.GetRequiredService<Core.Services.IUpdateService>();
            var result = await updateService.CheckForUpdateAsync();
            if (!result.HasUpdate)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
                window.BeginSilentUpdateFlow(result));
        }
        catch
        {
            // 网络不通或版本检查失败 — 静默跳过，不阻塞主流程
        }
    }
}
