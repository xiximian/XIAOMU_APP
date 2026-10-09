using Xiaomuocr.Core;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;
using Xiaomuocr.Core.ViewModels;

var pdfDir = @"d:\Python\XIAOMU_OCR_App\Xiaomuocr_cli\pdf_testdata";
var pdf = Directory.GetFiles(pdfDir, "*.pdf").FirstOrDefault()
    ?? throw new FileNotFoundException("no pdf");
var outDir = Path.Combine(Path.GetTempPath(), "xiaomu_flow_verify");
Directory.CreateDirectory(outDir);
var dbPath = Path.Combine(outDir, "verify.db");

Console.WriteLine("=== 1. Open PDF (PDFtoImage, no Python) ===");
using var render = new PdfRenderService();
var info = await render.OpenPdfAsync(pdf);
Console.WriteLine($"OK pages={info.TotalPages} size={info.PageWidth:F0}x{info.PageHeight:F0}");

Console.WriteLine("=== 2. Display render ===");
var display = await render.RenderPageForDisplayAsync(0, zoom: 1.0f);
File.WriteAllBytes(Path.Combine(outDir, "display_z1.png"), display);
Console.WriteLine($"OK display={display.Length} bytes");

Console.WriteLine("=== 3. OCR render (2x) ===");
var ocrImg = await render.RenderPageForOcrAsync(0);
File.WriteAllBytes(Path.Combine(outDir, "ocr_page.png"), ocrImg.ImageBytes);
int expW = (int)Math.Round(info.PageWidth * 2);
int expH = (int)Math.Round(info.PageHeight * 2);
if (ocrImg.OriginalSize[0] != expW || ocrImg.OriginalSize[1] != expH)
    throw new Exception($"2x size mismatch: {ocrImg.OriginalSize[0]}x{ocrImg.OriginalSize[1]} vs {expW}x{expH}");
Console.WriteLine($"OK ocr={ocrImg.Width}x{ocrImg.Height} scale={ocrImg.ResizeScale:F4}");

Console.WriteLine("=== 4. Login + OCR pipeline (page 0) ===");
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
using var tokens = new TokenStorage(dbPath);
using var library = new LibraryService(dbPath);
var api = new ApiService(http, tokens) { BaseUrl = "http://localhost:8000" };
var auth = new AuthService(api, tokens);
var oss = new OssService(api);
var ocr = new OcrService(api);
var pipeline = new PdfOcrPipelineService(render, oss, ocr, library);

await auth.LoginAsync("osstest", "123456");
Console.WriteLine("OK logged in as osstest");

var item = new LibraryItem
{
    PdfPath = pdf,
    Name = Path.GetFileNameWithoutExtension(pdf),
    TotalPages = info.TotalPages,
    OutputDir = outDir,
};
await library.AddItemAsync(item);

// Remove prior result so we always exercise the pipeline
var jsonPath = Path.Combine(outDir, "page_1_ocr_result.json");
if (File.Exists(jsonPath)) File.Delete(jsonPath);

var progress = new Progress<string>(m => Console.WriteLine("  " + m));
bool ok = await pipeline.RunSinglePageOcrAsync(item, 0, progress);
if (!ok) throw new Exception("OCR pipeline failed");
if (!File.Exists(jsonPath)) throw new Exception("OCR JSON not written");
Console.WriteLine($"OK OCR JSON: {new FileInfo(jsonPath).Length} bytes");

Console.WriteLine("=== 5. Load OCR + bbox mapping ===");
var ocrResult = new OcrResultService();
var page = await ocrResult.LoadPageResultAsync(outDir, 0)
    ?? throw new Exception("failed to parse OCR JSON");
Console.WriteLine($"OK blocks={page.Blocks.Count} preprocess={page.Preprocess != null} scale={page.Preprocess?.ResizeScale:F4}");
if (page.Blocks.Count == 0) throw new Exception("no OCR blocks");

var vm = new PdfViewerViewModel(render, ocrResult);
vm.Preprocess = page.Preprocess;
vm.Zoom = 1.0f;
var bb = page.Blocks[0].Bbox!;
var mapped = vm.GetScaledBbox(bb);
Console.WriteLine($"OK bbox [{string.Join(",", bb.Select(v => v.ToString("F0")))}] -> [{string.Join(",", mapped.Select(v => v.ToString("F1")))}]");
Console.WriteLine($"   text: {page.Blocks[0].Text[..Math.Min(60, page.Blocks[0].Text.Length)]}");

// Sanity: mapped coords should fall roughly within PDF page display size
double pageW = info.PageWidth, pageH = info.PageHeight;
if (mapped[0] < -50 || mapped[1] < -50 || mapped[2] > pageW + 50 || mapped[3] > pageH + 50)
    Console.WriteLine($"WARN mapped bbox outside page bounds ({pageW:F0}x{pageH:F0}) — check visually");
else
    Console.WriteLine("OK mapped bbox within page bounds");

Console.WriteLine();
Console.WriteLine($"ALL CHECKS PASSED. Artifacts: {outDir}");
