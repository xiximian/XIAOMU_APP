using System.Globalization;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

/*
 * 独立 PDF 渲染进程 — 与 Avalonia 主进程隔离，避免 PDFium 原生闪退。
 *
 * 用法:
 *   Xiaomuocr.PdfHost info  <pdfPath>
 *     → stdout: pages;w0,h0;w1,h1;...  （兼容旧：pages,w0,h0）
 *   Xiaomuocr.PdfHost render <pdfPath> <pageIndex> <zoom> <rotation>
 *     → stdout: PNG bytes
 */

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Xiaomuocr.PdfHost info|render ...");
    return 2;
}

try
{
    var cmd = args[0].ToLowerInvariant();
    var pdfPath = args[1];
    if (!File.Exists(pdfPath))
    {
        Console.Error.WriteLine($"file not found: {pdfPath}");
        return 1;
    }

    return cmd switch
    {
        "info" => CmdInfo(pdfPath),
        "render" => CmdRender(pdfPath, args),
        _ => Unknown(cmd),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    return 1;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"unknown command: {cmd}");
    return 2;
}

static int CmdInfo(string pdfPath)
{
    using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
    int n = doc.GetPageCount();
    // scale=1.0 时 GetPageWidth/Height ≈ PDF 点尺寸；逐页读取，支持异形页
    var sb = new System.Text.StringBuilder(n * 24);
    sb.Append(n.ToString(CultureInfo.InvariantCulture));
    for (int i = 0; i < n; i++)
    {
        using var page = doc.GetPageReader(i);
        sb.Append(CultureInfo.InvariantCulture, $";{page.GetPageWidth()},{page.GetPageHeight()}");
    }
    Console.Write(sb.ToString());
    return 0;
}

static int CmdRender(string pdfPath, string[] args)
{
    if (args.Length < 5)
    {
        Console.Error.WriteLine("usage: render <pdf> <pageIndex> <zoom> <rotation>");
        return 2;
    }

    int pageIndex = int.Parse(args[2], CultureInfo.InvariantCulture);
    double zoom = double.Parse(args[3], CultureInfo.InvariantCulture);
    int rotation = int.Parse(args[4], CultureInfo.InvariantCulture);
    // 允许极小缩放（大尺寸扫描件横向铺满可能低至 2%–5%）
    double scale = Math.Clamp(zoom, 0.01, 32.0);

    using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));
    using var page = doc.GetPageReader(pageIndex);
    var raw = page.GetImage();
    int w = page.GetPageWidth();
    int h = page.GetPageHeight();

    using var img = Image.LoadPixelData<Bgra32>(raw, w, h);
    img.Mutate(x =>
    {
        x.BackgroundColor(Color.White);
        int rot = ((rotation % 360) + 360) % 360;
        if (rot == 90) x.Rotate(RotateMode.Rotate90);
        else if (rot == 180) x.Rotate(RotateMode.Rotate180);
        else if (rot == 270) x.Rotate(RotateMode.Rotate270);

        // 硬上限：防止异常 zoom 生成超大位图
        const int maxEdge = 8192;
        if (img.Width > maxEdge || img.Height > maxEdge)
        {
            double shrink = Math.Min((double)maxEdge / img.Width, (double)maxEdge / img.Height);
            int nw = Math.Max(1, (int)Math.Round(img.Width * shrink));
            int nh = Math.Max(1, (int)Math.Round(img.Height * shrink));
            x.Resize(nw, nh);
        }
    });

    using var stdout = Console.OpenStandardOutput();
    img.Save(stdout, new PngEncoder { CompressionLevel = PngCompressionLevel.Level3 });
    return 0;
}
