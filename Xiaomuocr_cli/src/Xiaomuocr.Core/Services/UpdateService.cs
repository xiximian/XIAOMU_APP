using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>更新下载进度（含阶段文案）。</summary>
public sealed class UpdateProgress
{
    public int Percent { get; init; }
    public string Message { get; init; } = "";
}

public interface IUpdateService
{
    /// <summary>静默检查更新（后台，不弹窗）。platform 为空时自动用当前 OS。</summary>
    Task<VersionCheckResult> CheckForUpdateAsync(string? platform = null);

    /// <summary>
    /// 若本地已有校验通过的安装包则直接返回路径；否则静默下载并校验 MD5。
    /// </summary>
    Task<string> EnsurePackageDownloadedAsync(
        VersionCheckResult update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动安装：Windows 优先 Update.exe apply --waitPid（Velopack 官方）；否则先拉起入口/Setup 再退出。
    /// macOS 解压 zip 覆盖并重启。
    /// </summary>
    Task LaunchInstallerAsync(string setupFilePath);

    /// <summary>当前平台是否支持一键自动安装（Windows Setup / macOS 自替换）。</summary>
    bool SupportsAutoInstall { get; }

    /// <summary>尝试复用已下载且 MD5/大小匹配的本地包。</summary>
    bool TryGetCachedPackage(VersionCheckResult update, out string setupPath);
}

public class UpdateService : IUpdateService
{
    /// <summary>
    /// 专用下载客户端：与 API 共用的 HttpClient 默认 Timeout=100s，
    /// 慢速 OSS 会在下载未完成时被取消。
    /// </summary>
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private readonly IVersionService _version;

    public UpdateService(IVersionService version)
    {
        _version = version;
    }

    public bool SupportsAutoInstall => AppPlatform.IsWindows || AppPlatform.IsMacOS;

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return client;
    }

    public async Task<VersionCheckResult> CheckForUpdateAsync(string? platform = null)
    {
        return await _version.CheckUpdateAsync(platform ?? AppPlatform.Id);
    }

    public bool TryGetCachedPackage(VersionCheckResult update, out string setupPath)
    {
        setupPath = GetSetupPath(update);
        if (!File.Exists(setupPath))
            return false;

        try
        {
            if (!string.IsNullOrEmpty(update.FileMd5))
            {
                var actual = ComputeFileMd5(setupPath);
                if (!string.Equals(actual, update.FileMd5, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else if (update.FileSize is > 0)
            {
                var len = new FileInfo(setupPath).Length;
                if (len != update.FileSize.Value)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> EnsurePackageDownloadedAsync(
        VersionCheckResult update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            throw new InvalidOperationException(
                $"下载地址为空（版本 {update.LatestVersion}）。" +
                "多半是后端升了 APP_VERSION 但未用发布工具注册本平台安装包；" +
                "请重新发布 Windows/Mac 客户端或在管理端补全 download_url。");

        if (TryGetCachedPackage(update, out var cached))
        {
            progress?.Report(new UpdateProgress { Percent = 100, Message = "已使用本地缓存安装包" });
            return cached;
        }

        var tempDir = GetUpdateTempDir();
        Directory.CreateDirectory(tempDir);
        var setupFile = GetSetupPath(update);
        var partialFile = setupFile + ".partial";

        try
        {
            progress?.Report(new UpdateProgress { Percent = 0, Message = "正在连接下载服务器..." });

            using var response = await DownloadHttp.GetAsync(
                update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength
                             ?? update.FileSize
                             ?? -1L;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var fileStream = new FileStream(
                             partialFile, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long totalRead = 0;
                int lastReported = -1;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;

                    if (progress == null) continue;

                    int pct = totalBytes > 0
                        ? (int)Math.Min(99, totalRead * 100 / totalBytes)
                        : 0;

                    if (pct != lastReported || totalBytes <= 0)
                    {
                        lastReported = pct;
                        var sizeHint = totalBytes > 0
                            ? $" {(totalRead / 1024.0 / 1024.0):F1}/{(totalBytes / 1024.0 / 1024.0):F1} MB"
                            : $" {(totalRead / 1024.0 / 1024.0):F1} MB";
                        progress.Report(new UpdateProgress
                        {
                            Percent = pct,
                            Message = $"正在下载... {pct}%{sizeHint}",
                        });
                    }
                }

                await fileStream.FlushAsync(cancellationToken);
            }

            progress?.Report(new UpdateProgress { Percent = 100, Message = "下载完成，正在校验文件..." });

            if (!string.IsNullOrEmpty(update.FileMd5))
            {
                progress?.Report(new UpdateProgress { Percent = 100, Message = "正在校验文件完整性..." });
                var actualMd5 = await Task.Run(() => ComputeFileMd5(partialFile), cancellationToken);
                if (!string.Equals(actualMd5, update.FileMd5, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(partialFile); } catch { /* ignore */ }
                    throw new InvalidOperationException(
                        $"文件校验失败: 期望 {update.FileMd5}, 实际 {actualMd5}");
                }
            }

            // 原子替换为正式文件名
            if (File.Exists(setupFile))
            {
                try { File.Delete(setupFile); } catch { /* ignore */ }
            }
            File.Move(partialFile, setupFile);

            progress?.Report(new UpdateProgress { Percent = 100, Message = "安装包已就绪" });
            return setupFile;
        }
        catch
        {
            try { if (File.Exists(partialFile)) File.Delete(partialFile); } catch { /* ignore */ }
            throw;
        }
    }

    public Task LaunchInstallerAsync(string setupFilePath)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath) || !File.Exists(setupFilePath))
            throw new FileNotFoundException("安装包不存在", setupFilePath);

        if (AppPlatform.IsWindows)
            return LaunchWindowsInstallerAsync(setupFilePath);

        if (AppPlatform.IsMacOS)
            return LaunchMacInstallerAsync(setupFilePath);

        return RevealPackageForManualInstallAsync(setupFilePath);
    }

    private static Task LaunchWindowsInstallerAsync(string setupFilePath)
    {
        var tempDir = GetUpdateTempDir();
        Directory.CreateDirectory(tempDir);

        var myPid = Environment.ProcessId;
        var logFile = Path.Combine(tempDir, "update.log");
        var setupFull = Path.GetFullPath(setupFilePath);
        var setupDir = Path.GetDirectoryName(setupFull) ?? tempDir;

        void Wlog(string m)
        {
            try
            {
                File.AppendAllText(logFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {m}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { /* ignore */ }
        }

        Wlog($"launch-now start pid={myPid} setup={setupFull}");
        if (!File.Exists(setupFull))
            throw new FileNotFoundException("安装包不存在", setupFull);

        // Velopack 官方路径：先拉起 Update.exe apply --waitPid，再由调用方退出
        var nupkg = ResolveNupkgForApply(setupFull);
        var updateExe = FindVelopackUpdateExe();
        if (nupkg != null && updateExe != null)
        {
            Wlog($"velopack apply nupkg={nupkg} updateExe={updateExe}");
            TryKillLeftoverInstallers(Wlog);
            StartUpdateExeApply(updateExe, nupkg, myPid, Wlog);
            ScheduleSchtasksApplyOnce(updateExe, nupkg, Wlog);
            Wlog("launch-now done (Update.exe apply armed)");
            return Task.CompletedTask;
        }

        Wlog(nupkg == null
            ? "no local nupkg; fallback to launch package exe before exit"
            : "Update.exe not found; fallback to launch package exe before exit");

        TryKillLeftoverInstallers(Wlog);

        // 回退：主进程仍在时拉起入口/Setup（仅一次）
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start \"\" \"" + setupFull + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = setupDir,
            });
            Wlog("launched via cmd start");
        }
        catch (Exception ex)
        {
            Wlog("cmd start failed: " + ex.Message);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupFull,
                    UseShellExecute = true,
                    WorkingDirectory = setupDir,
                    WindowStyle = ProcessWindowStyle.Normal,
                });
                Wlog("launched via Start-Process");
            }
            catch (Exception ex2)
            {
                Wlog("Start-Process failed: " + ex2.Message);
                throw;
            }
        }

        ScheduleSchtasksRunOnce(setupFull, Wlog);
        ScheduleWindowsInstallerWatchdog(setupFull, myPid, logFile);

        var found = false;
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(400);
            if (IsWindowsInstallerProcessRunning())
            {
                found = true;
                Wlog($"installer process detected after {(i + 1) * 400}ms");
                break;
            }
        }

        Wlog(found
            ? "launch-now done (installer seen)"
            : "launch-now done (installer not seen; schtasks/watchdog armed)");
        return Task.CompletedTask;
    }

    /// <summary>安装根：%LocalAppData%\xiaomu-ocr\（current 的父目录）。</summary>
    private static string? GetVelopackRootDir()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = new DirectoryInfo(baseDir);
            if (current.Name.Equals("current", StringComparison.OrdinalIgnoreCase)
                && current.Parent != null)
            {
                return current.Parent.FullName;
            }

            var installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "xiaomu-ocr");
            if (File.Exists(Path.Combine(installed, "Update.exe")))
                return installed;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? FindVelopackUpdateExe()
    {
        var root = GetVelopackRootDir();
        if (root == null) return null;
        var exe = Path.Combine(root, "Update.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// 若下载物本身是 nupkg，或 TEMP/packages 下已有同版本 full nupkg，则返回其路径。
    /// 对 Entry/Setup .exe 不盲目取 packages 最新包，避免误 apply 旧版。
    /// </summary>
    private static string? ResolveNupkgForApply(string setupFull)
    {
        if (setupFull.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
            && File.Exists(setupFull))
        {
            return setupFull;
        }

        var name = Path.GetFileNameWithoutExtension(setupFull);
        const string prefix = "XiaomuOCR-Setup-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var ver = name[prefix.Length..];
        if (string.IsNullOrWhiteSpace(ver) || ver.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return null;

        var nupkgName = $"xiaomu-ocr-{ver}-full.nupkg";
        var candidates = new List<string>
        {
            Path.Combine(GetUpdateTempDir(), nupkgName),
            Path.ChangeExtension(setupFull, ".nupkg"),
        };
        var root = GetVelopackRootDir();
        if (root != null)
            candidates.Add(Path.Combine(root, "packages", nupkgName));

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return null;
    }

    private static void StartUpdateExeApply(
        string updateExe, string nupkg, int waitPid, Action<string> wlog)
    {
        var args = waitPid > 0
            ? $"apply -p \"{nupkg}\" --waitPid {waitPid}"
            : $"apply -p \"{nupkg}\"";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = updateExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(updateExe) ?? GetUpdateTempDir(),
            });
            wlog($"Update.exe started args={args}");
        }
        catch (Exception ex)
        {
            wlog("Update.exe start failed: " + ex.Message);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{updateExe}\" {args}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            wlog("Update.exe via cmd start");
        }
    }

    /// <summary>用 schtasks ONCE 脱离作业对象，约 1 分钟后补刀 apply。</summary>
    private static void ScheduleSchtasksApplyOnce(
        string updateExe, string nupkg, Action<string> wlog)
    {
        try
        {
            var when = DateTime.Now.AddMinutes(1);
            var st = when.ToString("HH:mm");
            var tr = $"\\\"{updateExe}\\\" apply -p \\\"{nupkg}\\\"";
            var tn = "XiaomuOCR_ApplyOnce_" + when.ToString("HHmmss");
            var cmd =
                $"schtasks /Create /TN \"{tn}\" /TR \"{tr}\" /SC ONCE /ST {st} /RL LIMITED /F";
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                wlog($"schtasks apply-once exit={p.ExitCode} {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            wlog("schtasks apply-once failed: " + ex.Message);
        }
    }

    private static void ScheduleSchtasksRunOnce(string exePath, Action<string> wlog)
    {
        try
        {
            var when = DateTime.Now.AddMinutes(1);
            var st = when.ToString("HH:mm");
            var tr = $"\\\"{exePath}\\\"";
            var tn = "XiaomuOCR_RunOnce_" + when.ToString("HHmmss");
            var cmd =
                $"schtasks /Create /TN \"{tn}\" /TR \"{tr}\" /SC ONCE /ST {st} /RL LIMITED /F";
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                wlog($"schtasks run-once exit={p.ExitCode} {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            wlog("schtasks run-once failed: " + ex.Message);
        }
    }

    private static bool IsWindowsInstallerProcessRunning()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName ?? "";
                    if (name.StartsWith("XiaomuOCR-Setup", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Update", StringComparison.OrdinalIgnoreCase)
                        || (name.Equals("setup", StringComparison.OrdinalIgnoreCase)
                            && (p.MainWindowTitle?.Contains("小木", StringComparison.Ordinal) == true
                                || p.MainWindowTitle?.Contains("Xiaomu", StringComparison.OrdinalIgnoreCase) == true
                                || p.MainWindowTitle?.Contains("Velopack", StringComparison.OrdinalIgnoreCase) == true)))
                    {
                        return true;
                    }

                    var title = p.MainWindowTitle ?? "";
                    if (title.Contains("小木史料", StringComparison.Ordinal)
                        && (title.Contains("安装", StringComparison.Ordinal)
                            || title.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                            || title.Contains("更新", StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // access denied on some processes
                }
                finally
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static void TryKillLeftoverInstallers(Action<string> wlog)
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName ?? "";
                if (!name.StartsWith("XiaomuOCR-Setup", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* ignore */ }
                if (path != null
                    && path.IndexOf("XiaomuOCR", StringComparison.OrdinalIgnoreCase) < 0
                    && path.IndexOf("xiaomu-ocr", StringComparison.OrdinalIgnoreCase) < 0
                    && path.IndexOf("Velopack", StringComparison.OrdinalIgnoreCase) < 0
                    && !name.StartsWith("XiaomuOCR-Setup", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                wlog($"killing leftover pid={p.Id} name={name} path={path}");
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                wlog("kill leftover failed: " + ex.Message);
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// 主程序退出后：若安装器未在跑则再拉一次，并尝试置顶；仍无窗口则开文件夹 + MessageBox。
    /// </summary>
    private static void ScheduleWindowsInstallerWatchdog(string setupFilePath, int appPid, string logFile)
    {
        var tempDir = GetUpdateTempDir();
        var batFile = Path.Combine(tempDir, $"XiaomuOCR_UpdateWatch_{appPid}.bat");
        var ps1File = Path.Combine(tempDir, $"XiaomuOCR_UpdateWatch_{appPid}.ps1");
        var setupPs = setupFilePath.Replace("'", "''", StringComparison.Ordinal);
        var logPs = logFile.Replace("'", "''", StringComparison.Ordinal);

        var ps1 = new StringBuilder();
        ps1.AppendLine("$ErrorActionPreference = 'Continue'");
        ps1.AppendLine($"$setup = '{setupPs}'");
        ps1.AppendLine($"$log = '{logPs}'");
        ps1.AppendLine("function Wlog([string]$m) {");
        ps1.AppendLine("  try { Add-Content -LiteralPath $log -Value (\"[{0}] {1}\" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m) -Encoding UTF8 } catch {}");
        ps1.AppendLine("}");
        ps1.AppendLine("Wlog 'watchdog start'");
        ps1.AppendLine("function Test-Installer {");
        ps1.AppendLine("  @(Get-Process -ErrorAction SilentlyContinue | Where-Object {");
        ps1.AppendLine("    ($_.ProcessName -like 'XiaomuOCR-Setup*') -or");
        ps1.AppendLine("    ($_.MainWindowTitle -match '小木史料.*(安装|更新|Setup)') -or");
        ps1.AppendLine("    ($_.ProcessName -eq 'Update' -and $_.MainWindowTitle -match 'Velopack|Xiaomu|小木')");
        ps1.AppendLine("  }).Count -gt 0");
        ps1.AppendLine("}");
        ps1.AppendLine("if (-not (Test-Installer)) {");
        ps1.AppendLine("  Wlog 'watchdog: installer missing, relaunch'");
        ps1.AppendLine("  try { cmd.exe /c start \"\" \"$setup\" | Out-Null; Wlog 'watchdog cmd start' } catch { Wlog $_.Exception.Message }");
        ps1.AppendLine("  Start-Sleep -Milliseconds 600");
        ps1.AppendLine("  try { Start-Process -FilePath $setup -WorkingDirectory (Split-Path -Parent $setup) -WindowStyle Normal | Out-Null } catch { Wlog $_.Exception.Message }");
        ps1.AppendLine("}");
        ps1.AppendLine("$src = @\"");
        ps1.AppendLine("using System;");
        ps1.AppendLine("using System.Runtime.InteropServices;");
        ps1.AppendLine("public static class XiaomuWinActivate {");
        ps1.AppendLine("  [DllImport(\"user32.dll\")] public static extern bool ShowWindow(IntPtr h, int n);");
        ps1.AppendLine("  [DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr h);");
        ps1.AppendLine("  [DllImport(\"user32.dll\")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);");
        ps1.AppendLine("}");
        ps1.AppendLine("\"");
        ps1.AppendLine("Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue | Out-Null");
        ps1.AppendLine("$deadline = (Get-Date).AddSeconds(30)");
        ps1.AppendLine("$found = $false");
        ps1.AppendLine("while ((Get-Date) -lt $deadline) {");
        ps1.AppendLine("  $list = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {");
        ps1.AppendLine("    $_.MainWindowHandle -ne [IntPtr]::Zero -and (");
        ps1.AppendLine("      $_.ProcessName -like 'XiaomuOCR-Setup*' -or");
        ps1.AppendLine("      $_.MainWindowTitle -match '小木史料.*(安装|更新|Setup)|Velopack'");
        ps1.AppendLine("    )");
        ps1.AppendLine("  })");
        ps1.AppendLine("  foreach ($p in $list) {");
        ps1.AppendLine("    $h = $p.MainWindowHandle");
        ps1.AppendLine("    [XiaomuWinActivate]::ShowWindow($h, 9) | Out-Null");
        ps1.AppendLine("    [XiaomuWinActivate]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null");
        ps1.AppendLine("    [XiaomuWinActivate]::SetForegroundWindow($h) | Out-Null");
        ps1.AppendLine("    [XiaomuWinActivate]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null");
        ps1.AppendLine("    Wlog \"watchdog activated pid=$($p.Id) title=$($p.MainWindowTitle)\"");
        ps1.AppendLine("    $found = $true; break");
        ps1.AppendLine("  }");
        ps1.AppendLine("  if ($found) { break }");
        ps1.AppendLine("  Start-Sleep -Milliseconds 400");
        ps1.AppendLine("}");
        ps1.AppendLine("if (-not $found) {");
        ps1.AppendLine("  Wlog 'watchdog: no window; Explorer + MessageBox'");
        ps1.AppendLine("  try { Start-Process explorer.exe -ArgumentList ('/select,\"{0}\"' -f $setup) | Out-Null } catch {}");
        ps1.AppendLine("  try {");
        ps1.AppendLine("    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue");
        ps1.AppendLine("    [System.Windows.Forms.MessageBox]::Show(");
        ps1.AppendLine("      '未能自动弹出安装窗口。已打开安装包所在文件夹，请双击 XiaomuOCR-Setup-*.exe 完成更新。安装过程中请勿再次打开旧版客户端。',");
        ps1.AppendLine("      '小木史料阅读器 - 更新',");
        ps1.AppendLine("      [System.Windows.Forms.MessageBoxButtons]::OK,");
        ps1.AppendLine("      [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null");
        ps1.AppendLine("  } catch { Wlog $_.Exception.Message }");
        ps1.AppendLine("}");
        ps1.AppendLine("Wlog 'watchdog done'");
        File.WriteAllText(ps1File, ps1.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var batContent =
            $"""
            @echo off
            chcp 65001 >nul
            :wait_exit
            tasklist /FI "PID eq {appPid}" /NH 2>nul | findstr /I "{appPid}" >nul
            if %ERRORLEVEL%==0 (
                ping -n 2 127.0.0.1 >nul
                goto wait_exit
            )
            ping -n 2 127.0.0.1 >nul
            powershell -NoProfile -ExecutionPolicy Bypass -File "{ps1File}"
            del "{ps1File}" >nul 2>&1
            del "%~f0" >nul 2>&1
            """;
        File.WriteAllText(batFile, batContent, Encoding.Default);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = batFile,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // 主路径已尝试拉起 Setup；看门狗失败不阻断退出
        }
    }

    /// <summary>
    /// macOS 自动更新：等本进程退出 → 解压 zip → 只覆盖 Resources/app 载荷 → 用 open 启动 .app。
    /// 禁止对已公证 .app 内文件做 ad-hoc codesign（会破坏密封，导致 Gatekeeper「打不开」）。
    /// </summary>
    private static Task LaunchMacInstallerAsync(string zipPath)
    {
        var tempDir = GetUpdateTempDir();
        Directory.CreateDirectory(tempDir);

        var myPid = Environment.ProcessId;
        var targetDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var extractDir = Path.Combine(tempDir, $"extract_{myPid}");
        var scriptPath = Path.Combine(tempDir, $"XiaomuOCR_Update_{myPid}.sh");
        var mainExe = Path.Combine(targetDir, "Xiaomuocr.App");
        var logFile = Path.Combine(tempDir, $"update_{myPid}.log");

        // 路径经 printf %q 风格转义：用单引号包裹并处理内部单引号
        static string Q(string p) => "'" + p.Replace("'", "'\\''") + "'";

        var script =
            $"""
            #!/bin/bash
            set -e
            PID={myPid}
            ZIP={Q(zipPath)}
            TARGET={Q(targetDir)}
            EXTRACT={Q(extractDir)}
            MAIN={Q(mainExe)}
            LOG={Q(logFile)}
            exec >>"$LOG" 2>&1
            echo "[update] start $(date) pid=$PID"
            echo "[update] TARGET=$TARGET"
            echo "[update] ZIP=$ZIP"
            while kill -0 "$PID" 2>/dev/null; do sleep 1; done
            sleep 1
            echo "[update] extracting $ZIP"
            rm -rf "$EXTRACT"
            mkdir -p "$EXTRACT"
            if command -v ditto >/dev/null 2>&1; then
              ditto -x -k "$ZIP" "$EXTRACT"
            else
              unzip -qo "$ZIP" -d "$EXTRACT"
            fi
            # 只认真正的 Mach-O 主程序；绝不能用 Contents/MacOS 里的薄 bash 启动器
            APP_BIN=""
            cand=$(find "$EXTRACT" -path '*/Contents/Resources/app/Xiaomuocr.App' -type f 2>/dev/null | head -n 1 || true)
            if [ -n "$cand" ]; then
              ft=$(file -b "$cand" 2>/dev/null || true)
              case "$ft" in *Mach-O*) APP_BIN=$cand ;; esac
            fi
            if [ -z "$APP_BIN" ]; then
              while IFS= read -r cand; do
                case "$cand" in */Contents/MacOS/*) continue ;; esac
                ft=$(file -b "$cand" 2>/dev/null || true)
                case "$ft" in
                  *Mach-O*) APP_BIN=$cand; break ;;
                esac
              done < <(find "$EXTRACT" -name 'Xiaomuocr.App' -type f 2>/dev/null)
            fi
            if [ -z "$APP_BIN" ]; then
              echo "[update] Xiaomuocr.App (Mach-O) not found in zip"
              ls -laR "$EXTRACT" | head -n 80 || true
              exit 1
            fi
            SRC_DIR=$(dirname "$APP_BIN")
            echo "[update] APP_BIN=$APP_BIN ($(file -b "$APP_BIN" 2>/dev/null || true))"
            echo "[update] sync $SRC_DIR -> $TARGET"
            mkdir -p "$TARGET"
            if command -v rsync >/dev/null 2>&1; then
              rsync -a --exclude '.DS_Store' "$SRC_DIR/" "$TARGET/"
            else
              cp -R "$SRC_DIR/." "$TARGET/"
            fi
            chmod +x "$TARGET/Xiaomuocr.App" 2>/dev/null || true
            chmod +x "$TARGET/Xiaomuocr.PdfHost" 2>/dev/null || true
            # 去掉隔离属性即可；切勿 ad-hoc 重签（会破坏外层 Developer ID / 公证密封）
            xattr -cr "$TARGET" 2>/dev/null || true
            echo "[update] skip ad-hoc codesign (preserve notarized .app seal)"
            # 从 …/Contents/Resources/app 回到 Foo.app，用 open 启动（Dock/Gatekeeper 正常）
            BUNDLE=""
            case "$TARGET" in
              */Contents/Resources/app)
                BUNDLE=$(cd "$TARGET/../../.." && pwd)
                ;;
            esac
            if [ -n "$BUNDLE" ] && [ -d "$BUNDLE/Contents" ]; then
              echo "[update] open bundle: $BUNDLE"
              open -n "$BUNDLE" || open "$BUNDLE"
            else
              echo "[update] relaunch binary (flat install): $MAIN"
              nohup "$MAIN" >/dev/null 2>&1 &
            fi
            rm -rf "$EXTRACT"
            echo "[update] done $(date)"
            rm -f "$0"
            """;
        File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var chmod = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            ArgumentList = { "+x", scriptPath },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        chmod?.WaitForExit(5000);

        // 用 nohup 脱离当前进程树，避免主程序 Exit 时把更新脚本一起带走
        var launched = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/nohup",
            ArgumentList = { "/bin/bash", scriptPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (launched == null)
            throw new InvalidOperationException("无法启动 macOS 更新脚本");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Linux：在文件管理器中定位 zip，由用户手动解压安装。
    /// </summary>
    private static Task RevealPackageForManualInstallAsync(string packagePath)
    {
        if (AppPlatform.IsLinux)
        {
            var dir = Path.GetDirectoryName(packagePath) ?? packagePath;
            var launched = Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { dir },
                UseShellExecute = false,
            });
            if (launched == null)
                throw new InvalidOperationException("无法打开文件管理器，请到临时目录手动解压安装包。");
            return Task.CompletedTask;
        }

        throw new PlatformNotSupportedException($"当前平台暂不支持自动安装: {AppPlatform.Id}");
    }

    private static string GetUpdateTempDir()
        => Path.Combine(Path.GetTempPath(), "XiaomuOCR_Update");

    private static string GetSetupPath(VersionCheckResult update)
    {
        var versionTag = string.IsNullOrWhiteSpace(update.LatestVersion) ? "latest" : update.LatestVersion;
        // 纯 ASCII，避免 cmd/bat 中文路径问题；mac/linux 为 .zip；Windows 也可直接下 nupkg
        var ext = AppPlatform.PackageExtension;
        if (AppPlatform.IsWindows
            && !string.IsNullOrWhiteSpace(update.DownloadUrl)
            && update.DownloadUrl.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".nupkg";
        }

        return Path.Combine(GetUpdateTempDir(), $"XiaomuOCR-Setup-{versionTag}{ext}");
    }

    private static string ComputeFileMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }
}
