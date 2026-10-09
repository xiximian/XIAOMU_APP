using Avalonia;
using Avalonia.ReactiveUI;
using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Velopack;

namespace Xiaomuocr.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 尽早落盘：若连此文件都没有，说明进程在进 Main 前就被系统杀掉（隔离/签名/架构）
        WriteBootstrapLog("Main entered");
        try
        {
            // Velopack 目前仅 Windows 流水线使用；macOS/Linux 用 zip 手动分发
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                VelopackApp.Build().Run();

            WriteBootstrapLog("Starting Avalonia");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteBootstrapLog("FATAL: " + ex);
            try
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
            }
            catch
            {
                // Serilog 可能未配置文件 sink
            }
        }
        finally
        {
            try
            {
                Log.CloseAndFlush();
            }
            catch
            {
                // ignore
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI()
            .LogToTrace();

    private static void WriteBootstrapLog(string message)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(dir))
                dir = Directory.GetCurrentDirectory();
            var path = Path.Combine(dir, "xiaomuocr_bootstrap.log");
            var line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}" +
                $" | OS={RuntimeInformation.OSDescription}" +
                $" | Arch={RuntimeInformation.ProcessArchitecture}" +
                $" | Base={dir}" +
                Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // 目录只读或权限不足时忽略
        }
    }
}
