using System.Diagnostics;
using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IPdfRenderService
{
    /// <summary>打开 PDF 作为当前阅读文档（供界面渲染用）。</summary>
    Task<PdfInfo> OpenPdfAsync(string pdfPath);

    /// <summary>只读探测 PDF 信息，不切换当前阅读文档（供后台批量任务用）。</summary>
    Task<PdfInfo> ProbePdfAsync(string pdfPath);

    /// <summary>当前已打开文档的指定页尺寸（PDF 点）。</summary>
    (double Width, double Height) GetOpenedPageSize(int pageIndex0Based);

    /// <summary>按当前已打开文档渲染 OCR 图（仅供阅读器 UI）。</summary>
    Task<PdfPageImage> RenderPageForOcrAsync(int pageIndex);

    /// <summary>按指定路径渲染 OCR 图（后台批量必须用此重载，避免与阅读器抢文档）。</summary>
    Task<PdfPageImage> RenderPageForOcrAsync(
        string pdfPath, int pageIndex, double pageWidth, double pageHeight);

    Task<byte[]> RenderPageForDisplayAsync(int pageIndex, float zoom = 1.0f, int rotation = 0);
}

public class PdfInfo
{
    public int TotalPages { get; init; }
    public string FilePath { get; init; } = "";
    /// <summary>首页尺寸（兼容旧逻辑）。</summary>
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }
    /// <summary>每页宽高（PDF 点），与 TotalPages 对齐；异形页各自独立。</summary>
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; init; }
        = Array.Empty<(double, double)>();

    public (double Width, double Height) GetPageSize(int pageIndex0Based)
    {
        if (pageIndex0Based >= 0 && pageIndex0Based < PageSizes.Count)
            return PageSizes[pageIndex0Based];
        return (PageWidth, PageHeight);
    }
}

/// <summary>
/// PDF 渲染 — 通过独立进程 Xiaomuocr.PdfHost 调用 Docnet/PDFium。
/// 主进程（Avalonia）不加载 pdfium，避免原生闪退。
/// </summary>
public class PdfRenderService : IPdfRenderService, IDisposable
{
    private const float OcrScale = OcrPreprocessStore.OcrScale;

    private string _pdfPath = "";
    private double _pageW = 595, _pageH = 842;
    private (double Width, double Height)[] _pageSizes = Array.Empty<(double, double)>();
    private int _totalPages;
    private readonly object _stateLock = new();
    private readonly IAppLogService? _log;
    private static string? _hostExe;
    private static readonly object HostLock = new();

    public PdfRenderService() { }
    public PdfRenderService(IAppLogService? log) { _log = log; }
    private void Log(string msg) => _log?.Info($"[PDF] {msg}");

    public Task<PdfInfo> OpenPdfAsync(string pdfPath)
    {
        Log($"打开: {Path.GetFileName(pdfPath)}");
        return Task.Run(() =>
        {
            var info = ReadPdfInfo(pdfPath);
            lock (_stateLock)
            {
                _pdfPath = pdfPath;
                _totalPages = info.TotalPages;
                _pageW = info.PageWidth;
                _pageH = info.PageHeight;
                _pageSizes = info.PageSizes.ToArray();
            }
            Log($"已打开 {info.TotalPages} 页, 首页 {info.PageWidth:F0}x{info.PageHeight:F0}");
            return info;
        });
    }

    public Task<PdfInfo> ProbePdfAsync(string pdfPath)
    {
        return Task.Run(() => ReadPdfInfo(pdfPath));
    }

    /// <summary>当前已打开文档的指定页尺寸（PDF 点）。</summary>
    public (double Width, double Height) GetOpenedPageSize(int pageIndex0Based)
    {
        lock (_stateLock)
        {
            if (_pageSizes != null && pageIndex0Based >= 0 && pageIndex0Based < _pageSizes.Length)
                return _pageSizes[pageIndex0Based];
            return (_pageW, _pageH);
        }
    }

    public Task<PdfPageImage> RenderPageForOcrAsync(int pageIndex)
    {
        string path;
        double pageW, pageH;
        lock (_stateLock)
        {
            path = _pdfPath;
            if (_pageSizes != null && pageIndex >= 0 && pageIndex < _pageSizes.Length)
            {
                pageW = _pageSizes[pageIndex].Width;
                pageH = _pageSizes[pageIndex].Height;
            }
            else
            {
                pageW = _pageW;
                pageH = _pageH;
            }
        }
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("尚未打开 PDF");
        return RenderPageForOcrAsync(path, pageIndex, pageW, pageH);
    }

    public Task<PdfPageImage> RenderPageForOcrAsync(
        string pdfPath, int pageIndex, double pageWidth, double pageHeight)
    {
        Log($"OCR 第{pageIndex + 1}页 (2x) {Path.GetFileName(pdfPath)}");
        return Task.Run(() =>
        {
            var png = RunHostPng(
                "render", pdfPath,
                pageIndex.ToString(CultureInfo.InvariantCulture),
                OcrScale.ToString(CultureInfo.InvariantCulture), "0");
            int w = (int)Math.Round(pageWidth * OcrScale);
            int h = (int)Math.Round(pageHeight * OcrScale);
            return CompressImage(png, w, h);
        });
    }

    public Task<byte[]> RenderPageForDisplayAsync(int pageIndex, float zoom = 1.0f, int rotation = 0)
    {
        string path;
        lock (_stateLock)
            path = _pdfPath;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException("尚未打开 PDF");

        Log($"渲染第{pageIndex + 1}页 (zoom={zoom:P0})");
        return Task.Run(() =>
        {
            var png = RunHostPng(
                "render", path,
                pageIndex.ToString(CultureInfo.InvariantCulture),
                zoom.ToString(CultureInfo.InvariantCulture),
                rotation.ToString(CultureInfo.InvariantCulture));
            Log($"渲染完成: {png.Length} bytes");
            return png;
        });
    }

    private static PdfInfo ReadPdfInfo(string pdfPath)
    {
        var output = RunHostText("info", pdfPath).Trim();
        // 新格式: pages;w0,h0;w1,h1;...
        if (output.Contains(';'))
        {
            var segs = output.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segs.Length >= 1
                && int.TryParse(segs[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages))
            {
                var sizes = new List<(double, double)>(Math.Max(pages, segs.Length - 1));
                for (int i = 1; i < segs.Length; i++)
                {
                    var xy = segs[i].Split(',');
                    if (xy.Length >= 2
                        && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                        && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                        sizes.Add((w, h));
                }
                while (sizes.Count < pages && sizes.Count > 0)
                    sizes.Add(sizes[^1]);
                if (sizes.Count == 0)
                    sizes.Add((595, 842));
                return new PdfInfo
                {
                    TotalPages = pages,
                    FilePath = pdfPath,
                    PageWidth = sizes[0].Item1,
                    PageHeight = sizes[0].Item2,
                    PageSizes = sizes,
                };
            }
        }

        // 旧格式兼容: pages,width,height
        var parts = output.Split(',');
        if (parts.Length == 3
            && int.TryParse(parts[0], out int p)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ow)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double oh))
        {
            var sizes = Enumerable.Repeat((ow, oh), Math.Max(1, p)).ToArray();
            return new PdfInfo
            {
                TotalPages = p,
                FilePath = pdfPath,
                PageWidth = ow,
                PageHeight = oh,
                PageSizes = sizes,
            };
        }

        throw new Exception($"PdfHost info 输出异常: {output}");
    }

    private static byte[] RunHostPng(params string[] args)
    {
        var (exit, stdout, stderr) = RunHost(args, binaryStdout: true);
        if (exit != 0)
            throw new Exception($"PdfHost 失败 (exit={exit}): {TrimErr(stderr)}");
        if (stdout.Length < 500)
            throw new Exception($"PdfHost 渲染输出异常 ({stdout.Length} bytes): {TrimErr(stderr)}");
        return stdout;
    }

    private static string RunHostText(params string[] args)
    {
        var (exit, stdout, stderr) = RunHost(args, binaryStdout: false);
        if (exit != 0)
            throw new Exception($"PdfHost 失败 (exit={exit}): {TrimErr(stderr)}");
        return Encoding.UTF8.GetString(stdout);
    }

    private static (int exit, byte[] stdout, string stderr) RunHost(string[] args, bool binaryStdout)
    {
        var exe = GetHostExe();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new Exception("无法启动 Xiaomuocr.PdfHost");

        using var ms = new MemoryStream();
        var readTask = process.StandardOutput.BaseStream.CopyToAsync(ms);
        var errTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("PdfHost 超时");
        }
        readTask.Wait(2000);
        var stderr = errTask.Result;
        return (process.ExitCode, ms.ToArray(), stderr);
    }

    private static string TrimErr(string err)
        => string.IsNullOrWhiteSpace(err) ? "(no stderr)" : err[..Math.Min(err.Length, 400)];

    private static string GetHostExe()
    {
        lock (HostLock)
        {
            if (_hostExe != null && File.Exists(_hostExe))
                return _hostExe;

            foreach (var c in HostCandidates())
            {
                if (File.Exists(c))
                {
                    _hostExe = c;
                    return c;
                }
            }

            throw new FileNotFoundException(
                $"未找到 {AppPlatform.PdfHostFileName}。请重新编译客户端（会一并生成 PdfHost）。");
        }
    }

    private static IEnumerable<string> HostCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in PdfHostFileNames())
        {
            yield return Path.Combine(baseDir, name);
            yield return Path.Combine(baseDir, "Xiaomuocr.PdfHost", name);
        }

        // 开发时：从 App 输出目录旁找 PdfHost 工程输出（仅 DEBUG 构建）
#if DEBUG
        var ridHints = new[]
        {
            "",
            "win-x64",
            "osx-arm64",
            "osx-x64",
            "linux-x64",
        };
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                foreach (var root in new[]
                         {
                             Path.Combine(dir.FullName, "Xiaomuocr.PdfHost", "bin", config, "net9.0"),
                             Path.Combine(dir.FullName, "src", "Xiaomuocr.PdfHost", "bin", config, "net9.0"),
                         })
                {
                    foreach (var rid in ridHints)
                    {
                        var folder = string.IsNullOrEmpty(rid) ? root : Path.Combine(root, rid);
                        foreach (var name in PdfHostFileNames())
                            yield return Path.Combine(folder, name);
                    }
                }
            }
        }
#endif
    }

    private static IEnumerable<string> PdfHostFileNames()
    {
        // 当前平台优先，再试另一种后缀（交叉调试 / 拷贝产物时容错）
        yield return AppPlatform.PdfHostFileName;
        if (AppPlatform.IsWindows)
            yield return "Xiaomuocr.PdfHost";
        else
            yield return "Xiaomuocr.PdfHost.exe";
    }

    private static PdfPageImage CompressImage(byte[] png, int ow, int oh)
    {
        // 限边 2000px 等比缩小 + 转 JPG 品质 100（不降质）。服务端遇 JPEG 跳过二次处理。
        // 以实际 PNG 像素为准（异形页不得用首页尺寸强行 Resize）。
        const int jpegQuality = 100;
        const int maxImageDim = OcrPreprocessStore.MaxImageDim; // 2000
        using var img = Image.Load<Rgba32>(png);
        int srcW = img.Width;
        int srcH = img.Height;
        // 若宿主给了预期尺寸且与位图接近，优先用预期（坐标映射一致）；否则用位图
        if (ow > 1 && oh > 1
            && Math.Abs(ow - srcW) <= 2 && Math.Abs(oh - srcH) <= 2)
        {
            srcW = ow;
            srcH = oh;
        }

        double s = 1.0;
        int sw = srcW, sh = srcH;
        if (Math.Max(srcW, srcH) > maxImageDim)
        {
            s = (double)maxImageDim / Math.Max(srcW, srcH);
            sw = Math.Max(1, (int)Math.Round(srcW * s));
            sh = Math.Max(1, (int)Math.Round(srcH * s));
            img.Mutate(x => x.Resize(sw, sh));
        }

        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = jpegQuality });
        var jpeg = ms.ToArray();

        return new PdfPageImage
        {
            ImageBytes = jpeg,
            Width = sw,
            Height = sh,
            ResizeScale = s,
            OriginalSize = new[] { srcW, srcH },
            SentSize = new[] { sw, sh },
        };
    }

    public void Dispose() { }
}
