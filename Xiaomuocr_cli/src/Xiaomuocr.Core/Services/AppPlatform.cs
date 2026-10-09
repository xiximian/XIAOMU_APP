using System.Runtime.InteropServices;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 运行时平台探测，供版本检查 / 更新包命名等使用。
/// 与后端 ClientVersion.platform 取值一致：win / mac / mac-x64 / linux。
/// mac = Apple Silicon（osx-arm64）；mac-x64 = Intel（osx-x64）。
/// </summary>
public static class AppPlatform
{
    /// <summary>后端版本接口用的平台标识。</summary>
    public static string Id =>
        OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac" : "mac-x64")
        : OperatingSystem.IsLinux() ? "linux"
        : "win";

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>当前平台更新包文件后缀。</summary>
    public static string PackageExtension =>
        IsWindows ? ".exe" : ".zip";

    /// <summary>PdfHost 可执行文件名（Windows 带 .exe）。</summary>
    public static string PdfHostFileName =>
        IsWindows ? "Xiaomuocr.PdfHost.exe" : "Xiaomuocr.PdfHost";
}
