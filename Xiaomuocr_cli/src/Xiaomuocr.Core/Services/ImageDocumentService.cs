using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

/// <summary>将图片 / 截图转为受管 PDF 文献，供现有 PdfViewer / OCR 复用。</summary>
public interface IImageDocumentService
{
    Task<LibraryItem> CreateFromImageFileAsync(string imagePath, string? displayName = null, string? source = null);
    Task<LibraryItem> CreateFromImageBytesAsync(byte[] imageBytes, string displayName, string? source = null);
    /// <summary>合并多篇受管图片/截图文献为新的多页文献（不删原条目，由调用方删除）。</summary>
    Task<LibraryItem> MergeManagedItemsAsync(IReadOnlyList<LibraryItem> items, string? displayName = null);
    /// <summary>删除受管 imports\{id} 目录（非受管路径则忽略）。</summary>
    void TryDeleteManagedFiles(LibraryItem item);
}

public class ImageDocumentService : IImageDocumentService
{
    private const int MaxEdgePx = 4000;
    private const int JpegQuality = 92;

    public async Task<LibraryItem> CreateFromImageFileAsync(
        string imagePath, string? displayName = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw new FileNotFoundException("图片不存在", imagePath);

        var bytes = await File.ReadAllBytesAsync(imagePath);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(imagePath)
            : displayName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = $"图片_{DateTime.Now:yyyyMMdd_HHmmss}";
        return await CreateFromImageBytesAsync(bytes, name, source ?? "图片导入");
    }

    public async Task<LibraryItem> CreateFromImageBytesAsync(
        byte[] imageBytes, string displayName, string? source = null)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new ArgumentException("图片数据为空", nameof(imageBytes));

        var jpeg = await NormalizeToJpegAsync(imageBytes);
        return await CreateFromJpegPagesAsync(
            new[] { jpeg },
            displayName,
            source ?? "图片导入");
    }

    public async Task<LibraryItem> MergeManagedItemsAsync(
        IReadOnlyList<LibraryItem> items, string? displayName = null)
    {
        if (items == null || items.Count < 2)
            throw new ArgumentException("至少选择两篇图片/截图文献才能合并");

        var pages = new List<byte[]>();
        foreach (var item in items)
        {
            if (!ClientDataPaths.IsManagedImportPath(item.PdfPath))
                throw new InvalidOperationException($"「{item.Name}」不是图片/截图文献，无法合并");

            var root = ClientDataPaths.TryGetManagedImportItemDirectory(item.PdfPath)
                ?? throw new InvalidOperationException($"找不到「{item.Name}」的本地文件");

            var pageFiles = EnumeratePageJpegs(root);
            if (pageFiles.Count == 0)
                throw new InvalidOperationException($"「{item.Name}」缺少可合并的图片页");

            foreach (var f in pageFiles)
                pages.Add(await File.ReadAllBytesAsync(f));
        }

        var name = string.IsNullOrWhiteSpace(displayName)
            ? $"合并_{DateTime.Now:yyyyMMdd_HHmmss}"
            : displayName.Trim();

        return await CreateFromJpegPagesAsync(pages, name, "合并导入");
    }

    public void TryDeleteManagedFiles(LibraryItem item)
    {
        var root = ClientDataPaths.TryGetManagedImportItemDirectory(item.PdfPath);
        if (root == null) return;
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 清理失败不阻断库记录删除
        }
    }

    private async Task<LibraryItem> CreateFromJpegPagesAsync(
        IReadOnlyList<byte[]> jpegPages, string displayName, string source)
    {
        if (jpegPages.Count == 0)
            throw new ArgumentException("没有可写入的页面");

        var itemId = Guid.NewGuid().ToString("N")[..12];
        var dir = ClientDataPaths.GetImportItemDirectory(itemId);
        var pdfPath = Path.Combine(dir, "document.pdf");
        var ocrDir = Path.Combine(dir, "ocr");
        Directory.CreateDirectory(ocrDir);

        var savedPaths = new List<(string Path, int W, int H)>();
        for (var i = 0; i < jpegPages.Count; i++)
        {
            var pagePath = Path.Combine(dir, $"page_{i + 1:D3}.jpg");
            var jpeg = await NormalizeToJpegAsync(jpegPages[i]);
            await File.WriteAllBytesAsync(pagePath, jpeg);

            await using var ms = new MemoryStream(jpeg);
            using var image = await Image.LoadAsync(ms);
            savedPaths.Add((pagePath, image.Width, image.Height));
        }

        // 兼容旧逻辑：首页另存 source.jpg
        File.Copy(savedPaths[0].Path, Path.Combine(dir, "source.jpg"), overwrite: true);

        JpegPdfWriter.Write(savedPaths, pdfPath);

        return new LibraryItem
        {
            Id = itemId,
            Name = string.IsNullOrWhiteSpace(displayName)
                ? $"图片_{DateTime.Now:yyyyMMdd_HHmmss}"
                : displayName.Trim(),
            PdfPath = pdfPath,
            OutputDir = ocrDir,
            TotalPages = savedPaths.Count,
            Source = source,
            OcrDonePages = 0,
        };
    }

    private static List<string> EnumeratePageJpegs(string itemDir)
    {
        var pages = Directory.GetFiles(itemDir, "page_*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pages.Count > 0) return pages;

        var source = Path.Combine(itemDir, "source.jpg");
        if (File.Exists(source))
            return new List<string> { source };

        return new List<string>();
    }

    private static async Task<byte[]> NormalizeToJpegAsync(byte[] imageBytes)
    {
        await using var ms = new MemoryStream(imageBytes);
        using var image = await Image.LoadAsync(ms);
        image.Mutate(x => x.AutoOrient());
        if (image.Width > MaxEdgePx || image.Height > MaxEdgePx)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(MaxEdgePx, MaxEdgePx),
            }));
        }

        // 统一成 RGB，便于 PDF 以 /DeviceRGB + DCTDecode 直嵌
        await using var outMs = new MemoryStream();
        using (var rgb = image.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgb24>())
        {
            await rgb.SaveAsJpegAsync(outMs, new JpegEncoder { Quality = JpegQuality });
        }
        return outMs.ToArray();
    }
}
