using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

var pdf = Directory.GetFiles(@"d:\Python\XIAOMU_OCR_App\Xiaomuocr_cli\pdf_testdata", "*.pdf").First();
var outDir = Path.Combine(Path.GetTempPath(), "xiaomu_docnet_probe");
Directory.CreateDirectory(outDir);
Console.WriteLine(pdf);

using var library = DocLib.Instance;
using (var doc = library.GetDocReader(pdf, new PageDimensions(1.0)))
using (var page = doc.GetPageReader(0))
{
    Console.WriteLine($"pages={doc.GetPageCount()} w={page.GetPageWidth()} h={page.GetPageHeight()} @scale1");
}

using (var doc = library.GetDocReader(pdf, new PageDimensions(2.0)))
using (var page = doc.GetPageReader(0))
{
    var raw = page.GetImage();
    int w = page.GetPageWidth(), h = page.GetPageHeight();
    Console.WriteLine($"2x render {w}x{h} bytes={raw.Length}");
    using var img = Image.LoadPixelData<Bgra32>(raw, w, h);
    img.Mutate(x => x.BackgroundColor(Color.White));
    var path = Path.Combine(outDir, "page0_2x.png");
    await img.SaveAsPngAsync(path);
    Console.WriteLine($"saved {path} ({new FileInfo(path).Length} bytes)");
}
Console.WriteLine("OK");
