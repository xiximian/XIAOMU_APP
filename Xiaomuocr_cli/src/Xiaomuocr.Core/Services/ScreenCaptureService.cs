using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Xiaomuocr.Core.Services;

/// <summary>屏幕区域截图（Windows BitBlt / macOS screencapture）。</summary>
public interface IScreenCaptureService
{
    bool IsSupported { get; }

    /// <summary>
    /// 按屏幕像素矩形截取 PNG。坐标为虚拟桌面绝对像素。
    /// </summary>
    /// <param name="deviceScale">
    /// 主屏缩放（Avalonia Screens.Primary.Scaling）。macOS 的 screencapture -R 使用逻辑点，需除以该值。
    /// </param>
    Task<byte[]?> CaptureRegionAsPngAsync(int x, int y, int width, int height, double deviceScale = 1.0);
}

public class ScreenCaptureService : IScreenCaptureService
{
    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public Task<byte[]?> CaptureRegionAsPngAsync(int x, int y, int width, int height, double deviceScale = 1.0)
    {
        if (width <= 0 || height <= 0)
            return Task.FromResult<byte[]?>(null);

        if (OperatingSystem.IsWindows())
            return Task.Run(() => CaptureRegionWindows(x, y, width, height));

        if (OperatingSystem.IsMacOS())
            return Task.Run(() => CaptureRegionMac(x, y, width, height, deviceScale));

        return Task.FromResult<byte[]?>(null);
    }

    [SupportedOSPlatform("macos")]
    private static byte[]? CaptureRegionMac(int x, int y, int width, int height, double deviceScale)
    {
        var scale = deviceScale > 0.01 ? deviceScale : 1.0;
        // screencapture -R 使用逻辑点（相对主屏左上）
        var rx = (int)Math.Round(x / scale);
        var ry = (int)Math.Round(y / scale);
        var rw = Math.Max(1, (int)Math.Round(width / scale));
        var rh = Math.Max(1, (int)Math.Round(height / scale));

        var tmp = Path.Combine(Path.GetTempPath(), $"xiaomu_cap_{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/screencapture",
                ArgumentList = { "-x", "-t", "png", "-R", $"{rx},{ry},{rw},{rh}", tmp },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            // 部分系统只在 /usr/bin
            if (!File.Exists(psi.FileName))
                psi.FileName = "/usr/bin/screencapture";

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            if (proc.ExitCode != 0 || !File.Exists(tmp))
                return null;

            var bytes = File.ReadAllBytes(tmp);
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? CaptureRegionWindows(int x, int y, int width, int height)
    {
        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;
        try
        {
            hdcScreen = Native.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return null;

            hdcMem = Native.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero) return null;

            hBitmap = Native.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero) return null;

            hOld = Native.SelectObject(hdcMem, hBitmap);
            if (!Native.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, x, y, Native.SRCCOPY))
                return null;

            using var image = BitmapToImageSharp(hBitmap, width, height);
            if (image == null) return null;

            using var ms = new MemoryStream();
            image.Save(ms, new PngEncoder());
            return ms.ToArray();
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
                Native.SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero)
                Native.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                Native.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                Native.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    [SupportedOSPlatform("windows")]
    private static Image<Bgra32>? BitmapToImageSharp(IntPtr hBitmap, int width, int height)
    {
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Native.BI_RGB,
            },
        };

        var hdc = Native.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;

        try
        {
            var buffer = new byte[width * height * 4];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var result = Native.GetDIBits(
                    hdc, hBitmap, 0, (uint)height,
                    handle.AddrOfPinnedObject(), ref bmi, Native.DIB_RGB_COLORS);
                if (result == 0) return null;

                // GetDIBits(BI_RGB/32) 内存序为 B,G,R,unused，与 Bgra32 字段布局一致。
                // 注意：Bgra32 构造函数参数是 (r,g,b,a)，不能按缓冲区顺序直接 new。
                var image = Image.LoadPixelData<Bgra32>(buffer, width, height);
                image.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < height; row++)
                    {
                        var span = accessor.GetRowSpan(row);
                        for (int col = 0; col < width; col++)
                            span[col].A = 255;
                    }
                });
                return image;
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        public const int SRCCOPY = 0x00CC0020;
        public const int BI_RGB = 0;
        public const int DIB_RGB_COLORS = 0;

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(
            IntPtr hdcDest, int xDest, int yDest, int w, int h,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(
            IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
            IntPtr lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public int biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }
    }
}
