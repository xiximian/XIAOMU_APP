using System.Globalization;
using System.Text;

namespace Xiaomuocr.Core.Services;

/// <summary>
/// 将已编码的 JPEG 文件直接嵌入为多页 PDF（DCTDecode）。
/// 不经过 PdfSharpCore / ImageSharp 再解码，避免 ImageSharp 2/3 API 冲突。
/// </summary>
internal static class JpegPdfWriter
{
    /// <param name="pages">JPEG 路径与像素宽高（用于 MediaBox，按 96 DPI 换算为 point）。</param>
    public static void Write(IReadOnlyList<(string Path, int Width, int Height)> pages, string pdfPath)
    {
        if (pages == null || pages.Count == 0)
            throw new ArgumentException("没有可写入的页面", nameof(pages));

        const double dpi = 96.0;
        var jpegBytes = new List<byte[]>(pages.Count);
        var sizes = new List<(double Wpt, double Hpt, int Wpx, int Hpx)>(pages.Count);
        foreach (var (path, w, h) in pages)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("JPEG 页文件不存在", path);
            if (w <= 0 || h <= 0)
                throw new ArgumentException($"无效图片尺寸 {w}x{h}: {path}");
            jpegBytes.Add(File.ReadAllBytes(path));
            sizes.Add((w * 72.0 / dpi, h * 72.0 / dpi, w, h));
        }

        // 对象编号：1=Catalog, 2=Pages, 之后每页 3 个对象：Page / Image / Content
        var objectCount = 2 + pages.Count * 3;
        var offsets = new long[objectCount + 1]; // 1-based

        using var fs = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

        void WriteAscii(string s) => writer.Write(Encoding.ASCII.GetBytes(s));

        WriteAscii("%PDF-1.4\n");
        // 二进制标记，提示阅读器按二进制处理
        writer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        offsets[1] = fs.Position;
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            var pageObj = 3 + i * 3;
            if (i > 0) kids.Append(' ');
            kids.Append(CultureInfo.InvariantCulture, $"{pageObj} 0 R");
        }

        offsets[2] = fs.Position;
        WriteAscii(
            $"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var pageObj = 3 + i * 3;
            var imageObj = pageObj + 1;
            var contentObj = pageObj + 2;
            var (wpt, hpt, wpx, hpx) = sizes[i];
            var jpeg = jpegBytes[i];

            var wptS = FormatNum(wpt);
            var hptS = FormatNum(hpt);

            offsets[pageObj] = fs.Position;
            WriteAscii(
                $"{pageObj} 0 obj\n" +
                $"<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {wptS} {hptS}] " +
                $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> " +
                $"/Contents {contentObj} 0 R >>\n" +
                "endobj\n");

            offsets[imageObj] = fs.Position;
            WriteAscii(
                $"{imageObj} 0 obj\n" +
                $"<< /Type /XObject /Subtype /Image " +
                $"/Width {wpx} /Height {hpx} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 " +
                $"/Filter /DCTDecode /Length {jpeg.Length} >>\n" +
                "stream\n");
            writer.Write(jpeg);
            WriteAscii("\nendstream\nendobj\n");

            // 把图像画满整页：q … cm /Im0 Do Q
            var content =
                $"q\n{wptS} 0 0 {hptS} 0 0 cm\n/Im0 Do\nQ\n";
            var contentBytes = Encoding.ASCII.GetBytes(content);
            offsets[contentObj] = fs.Position;
            WriteAscii(
                $"{contentObj} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            writer.Write(contentBytes);
            WriteAscii("endstream\nendobj\n");
        }

        var xrefPos = fs.Position;
        WriteAscii($"xref\n0 {objectCount + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
            WriteAscii($"{offsets[i]:D10} 00000 n \n");

        WriteAscii(
            "trailer\n" +
            $"<< /Size {objectCount + 1} /Root 1 0 R >>\n" +
            "startxref\n" +
            $"{xrefPos}\n" +
            "%%EOF\n");
    }

    private static string FormatNum(double v)
        => v.ToString("0.####", CultureInfo.InvariantCulture);
}
