using Xiaomuocr.Core.Services;
var dir = @"C:\Users\xiximian\Desktop\横行与蠕动底一群 (2)";
var svc = new OcrResultService();
var page = await svc.LoadPageResultAsync(dir, 1);
Console.WriteLine(page == null ? "NULL" : $"blocks={page.Blocks.Count} preprocess={page.Preprocess?.ResizeScale}");
if (page?.Blocks.Count > 0)
  Console.WriteLine($"first bbox=[{string.Join(",", page.Blocks[0].Bbox!)}] textlen={page.Blocks[0].Text.Length}");
