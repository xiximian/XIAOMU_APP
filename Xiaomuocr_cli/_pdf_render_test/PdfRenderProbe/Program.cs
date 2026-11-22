using PDFtoImage;
using SkiaSharp;

var pdfDir = @"d:\Python\XIAOMU_OCR_App\Xiaomuocr_cli\pdf_testdata";
var pdf = Directory.GetFiles(pdfDir, "*.pdf").FirstOrDefault()
    ?? throw new FileNotFoundException("no pdf in testdata");
var outDir = Path.Combine(Path.GetTempPath(), "xiaomu_pdf_probe");
Directory.CreateDirectory(outDir);

Console.WriteLine($"PDF: {pdf}");
using var fs = File.OpenRead(pdf);
var pages = Conversion.GetPageCount(fs, leaveOpen: true);
fs.Position = 0;
var size = Conversion.GetPageSize(fs, 0, leaveOpen: true);
Console.WriteLine($"Pages={pages}, Page0={size.Width}x{size.Height} (PDF points)");

// zoom=2.0 equivalent: 144 DPI (same as PyMuPDF Matrix(2,2))
fs.Position = 0;
var options = new RenderOptions(Dpi: 144);
using var bmp = Conversion.ToImage(fs, 0, leaveOpen: true, options: options);
Console.WriteLine($"Rendered: {bmp.Width}x{bmp.Height}");

var outPath = Path.Combine(outDir, "page0_144dpi.png");
using (var img = SKImage.FromBitmap(bmp))
using (var data = img.Encode(SKEncodedImageFormat.Png, 90))
using (var outFs = File.OpenWrite(outPath))
    data.SaveTo(outFs);

Console.WriteLine($"Saved: {outPath} ({new FileInfo(outPath).Length} bytes)");
Console.WriteLine("OK");
