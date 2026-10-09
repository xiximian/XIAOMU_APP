namespace Xiaomuocr.Core.Models;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>
/// PDF 预处理元数据：记录 OCR 前图片压缩的比例，
/// 用于将 OCR bbox 坐标从压缩图片空间映射回原始 PDF 空间。
/// </summary>
public class PreprocessInfo
{
    /// <summary>压缩比例 (resized / original)，≤ 1.0</summary>
    public double ResizeScale { get; set; } = 1.0;

    /// <summary>原始图片尺寸 (width, height) — 2x DPI 渲染后</summary>
    public int[]? OriginalSize { get; set; }

    /// <summary>压缩后图片尺寸 (width, height) — 实际上传给 OCR 的</summary>
    public int[]? ResizedSize { get; set; }

    /// <summary>
    /// 文档方向分类角度（doc_preprocessor_res.angle：0/90/180/270）。
    /// bbox 落在摆正后的坐标系，映射回原图时需做逆变换。
    /// </summary>
    public int OrientationAngle { get; set; }

    /// <summary>摆正后 OCR 结果图宽（prunedResult.width）</summary>
    public int OrientedWidth { get; set; }

    /// <summary>摆正后 OCR 结果图高（prunedResult.height）</summary>
    public int OrientedHeight { get; set; }
}

/// <summary>文献库中的文献条目</summary>
public class LibraryItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string PdfPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string FolderId { get; set; } = "root";

    private int _totalPages;
    public int TotalPages
    {
        get => _totalPages;
        set => SetField(ref _totalPages, value);
    }

    private int _ocrDonePages;
    public int OcrDonePages
    {
        get => _ocrDonePages;
        set => SetField(ref _ocrDonePages, value);
    }

    public string AddedTime { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string? Notes { get; set; }
    public string? OutputDir { get; set; }

    /// <summary>来源说明，如「图片导入」「截图导入」或用户自定义。</summary>
    public string? Source { get; set; }

    /// <summary>列表多选（不入库）：用于合并受管图片/截图文献。</summary>
    private bool _isSelectedForMerge;
    public bool IsSelectedForMerge
    {
        get => _isSelectedForMerge;
        set => SetField(ref _isSelectedForMerge, value);
    }

    /// <summary>是否为 imports 受管文献（可勾选合并）。</summary>
    public bool CanMerge
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PdfPath)) return false;
            return Xiaomuocr.Core.Services.ClientDataPaths.IsManagedImportPath(PdfPath);
        }
    }
}

/// <summary>文献库文件夹</summary>
public class LibraryFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
}

/// <summary>文件夹树节点（UI）。</summary>
public class FolderTreeNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryFolder Folder { get; }
    public string Id => Folder.Id;
    public string Name => Folder.Name;
    public ObservableCollection<FolderTreeNode> Children { get; } = new();

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public FolderTreeNode(LibraryFolder folder) => Folder = folder;

    public void NotifyNameChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
}

/// <summary>PDF 页面渲染结果</summary>
public class PdfPageImage
{
    /// <summary>渲染后的图片字节</summary>
    public byte[] ImageBytes { get; init; } = Array.Empty<byte>();

    /// <summary>图片宽度 (像素)</summary>
    public int Width { get; init; }

    /// <summary>图片高度 (像素)</summary>
    public int Height { get; init; }

    /// <summary>压缩比例：resized / original</summary>
    public double ResizeScale { get; init; } = 1.0;

    /// <summary>原始 2x 渲染尺寸 (width, height)</summary>
    public int[] OriginalSize { get; init; } = new[] { 0, 0 };

    /// <summary>实际发送给 OCR 的尺寸 (width, height)</summary>
    public int[] SentSize { get; init; } = new[] { 0, 0 };

    /// <summary>生成的 preprocess 元数据，供后端注入 OCR 结果</summary>
    public PreprocessInfo PreprocessInfo => new()
    {
        ResizeScale = ResizeScale,
        OriginalSize = OriginalSize,
        ResizedSize = SentSize,
    };
}
