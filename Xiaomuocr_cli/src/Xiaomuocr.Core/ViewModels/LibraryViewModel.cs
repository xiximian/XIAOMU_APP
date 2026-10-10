using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class LibraryViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IPricingService? _pricing;
    private readonly IImageDocumentService? _imageDocs;
    private readonly ITaskSyncService? _taskSync;

    public IImageDocumentService? ImageDocs => _imageDocs;

    // ---- 文件夹 ----
    public ObservableCollection<LibraryFolder> Folders { get; } = new();
    public ObservableCollection<FolderTreeNode> FolderTree { get; } = new();

    private LibraryFolder? _selectedFolder;
    public LibraryFolder? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFolder, value);
            _ = LoadItemsAsync();
        }
    }

    private FolderTreeNode? _selectedFolderNode;
    public FolderTreeNode? SelectedFolderNode
    {
        get => _selectedFolderNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFolderNode, value);
            SelectedFolder = value?.Folder;
        }
    }

    // ---- 文献列表 ----
    public ObservableCollection<LibraryItem> Items { get; } = new();

    private LibraryItem? _selectedItem;
    public LibraryItem? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    private string _searchKeyword = "";
    public string SearchKeyword
    {
        get => _searchKeyword;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchKeyword, value);
            _ = FilterItemsAsync();
        }
    }

    private string _newFolderName = "";
    public string NewFolderName
    {
        get => _newFolderName;
        set => this.RaiseAndSetIfChanged(ref _newFolderName, value);
    }

    // ---- 命令 ----
    public ReactiveCommand<Unit, Unit> AddFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportPdfCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteItemCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> MergeSelectedCommand { get; }

    /// <summary>打开文献事件，由 View 层处理文件对话框</summary>
    public event Action? ImportPdfRequested;
    public event Action? ScreenshotImportRequested;
    public event Action? RenameFolderRequested;
    public event Action? MergeSelectedRequested;
    public event Action<LibraryItem>? EditItemRequested;
    public event Action<LibraryItem>? OpenItemRequested;
    public event Action<LibraryItem>? StartOcrRequested;

    public LibraryViewModel(
        ILibraryService library,
        IPricingService? pricing = null,
        IImageDocumentService? imageDocs = null,
        ITaskSyncService? taskSync = null)
    {
        _library = library;
        _pricing = pricing;
        _imageDocs = imageDocs;
        _taskSync = taskSync;

        AddFolderCommand = ReactiveCommand.CreateFromTask(AddFolderAsync);
        RenameFolderCommand = ReactiveCommand.Create(() => RenameFolderRequested?.Invoke());
        DeleteFolderCommand = ReactiveCommand.CreateFromTask(DeleteFolderAsync);
        ImportPdfCommand = ReactiveCommand.Create(() => ImportPdfRequested?.Invoke());
        DeleteItemCommand = ReactiveCommand.CreateFromTask(DeleteItemAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadDataAsync);
        MergeSelectedCommand = ReactiveCommand.Create(() => MergeSelectedRequested?.Invoke());

        _ = LoadDataAsync();
    }

    /// <summary>
    /// 静默把磁盘校准后的 OCR 页数补丁应用到当前列表（不 IsBusy、不清列表）。
    /// 大批量时分片更新，避免一次 Notify 过多卡 UI。
    /// </summary>
    public void ApplyOcrCountPatches(IReadOnlyList<(string Id, int OcrDonePages)> patches)
    {
        if (patches == null || patches.Count == 0) return;

        var map = new Dictionary<string, int>(patches.Count);
        foreach (var (id, count) in patches)
            map[id] = count;

        foreach (var item in Items)
        {
            if (map.TryGetValue(item.Id, out var count) && item.OcrDonePages != count)
                item.OcrDonePages = count;
        }
    }

    /// <summary>拉取用户资产（供文献列表 OCR 确认框）。失败返回 null。</summary>
    public async Task<UserAssets?> TryGetAssetsAsync()
    {
        if (_pricing == null) return null;
        try
        {
            return await _pricing.GetAssetsAsync();
        }
        catch
        {
            return null;
        }
    }

    public async Task LoadDataAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            var folders = await _library.GetFoldersAsync();
            NormalizeFolderParents(folders);
            Folders.Clear();
            foreach (var f in folders) Folders.Add(f);

            var keepId = SelectedFolder?.Id ?? SelectedFolderNode?.Id ?? "root";
            RebuildFolderTree(folders);
            SelectedFolderNode = FindNode(FolderTree, keepId) ?? FolderTree.FirstOrDefault();

            await LoadItemsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载文献库失败: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>将无父级的非根文件夹挂到 root 下，保证树完整。</summary>
    private static void NormalizeFolderParents(List<LibraryFolder> folders)
    {
        var ids = folders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var f in folders)
        {
            if (f.Id == "root")
            {
                f.ParentId = null;
                continue;
            }

            if (string.IsNullOrWhiteSpace(f.ParentId) || !ids.Contains(f.ParentId) || f.ParentId == f.Id)
                f.ParentId = "root";
        }
    }

    private void RebuildFolderTree(IReadOnlyList<LibraryFolder> folders)
    {
        FolderTree.Clear();
        var root = folders.FirstOrDefault(f => f.Id == "root");
        if (root == null)
        {
            root = new LibraryFolder { Id = "root", Name = "全部文献", ParentId = null };
        }

        var byParent = folders
            .Where(f => f.Id != "root")
            .GroupBy(f => f.ParentId ?? "root")
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var rootNode = BuildNode(root, byParent, new HashSet<string>(StringComparer.Ordinal));
        FolderTree.Add(rootNode);
    }

    private static FolderTreeNode BuildNode(
        LibraryFolder folder,
        Dictionary<string, List<LibraryFolder>> byParent,
        HashSet<string> ancestors)
    {
        var node = new FolderTreeNode(folder) { IsExpanded = true };
        if (!ancestors.Add(folder.Id))
            return node;

        if (byParent.TryGetValue(folder.Id, out var kids))
        {
            foreach (var child in kids)
                node.Children.Add(BuildNode(child, byParent, ancestors));
        }

        ancestors.Remove(folder.Id);
        return node;
    }

    private static FolderTreeNode? FindNode(IEnumerable<FolderTreeNode> nodes, string id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            var hit = FindNode(n.Children, id);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>生成「父 / 子 / …」路径，供移动对话框展示。</summary>
    public string GetFolderPath(LibraryFolder folder)
    {
        var parts = new List<string>();
        var cur = folder;
        var guard = 0;
        while (cur != null && guard++ < 64)
        {
            parts.Add(cur.Name);
            if (string.IsNullOrEmpty(cur.ParentId) || cur.Id == "root")
                break;
            cur = Folders.FirstOrDefault(f => f.Id == cur.ParentId);
        }
        parts.Reverse();
        return string.Join(" / ", parts);
    }

    private async Task LoadItemsAsync()
    {
        var folderId = SelectedFolder?.Id;
        var items = await _library.GetItemsAsync(folderId);
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        await FilterItemsAsync();
    }

    private async Task FilterItemsAsync()
    {
        var folderId = SelectedFolder?.Id;
        var allItems = await _library.GetItemsAsync(folderId);
        Items.Clear();
        var kw = SearchKeyword?.Trim().ToLower() ?? "";
        foreach (var item in allItems)
        {
            if (string.IsNullOrEmpty(kw) || item.Name.ToLower().Contains(kw))
                Items.Add(item);
        }
    }

    public async Task AddItemAsync(LibraryItem item)
    {
        item.FolderId = SelectedFolder?.Id ?? "root";
        await _library.AddItemAsync(item);
        await LoadItemsAsync();
        StatusMessage = $"已添加: {item.Name}";
    }

    private async Task AddFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(NewFolderName))
        {
            ErrorMessage = "请输入文件夹名称";
            return;
        }

        // 挂在当前选中目录下；未选或选中根 → 作为 root 的子文件夹
        var parentId = SelectedFolder == null || SelectedFolder.Id == "root"
            ? "root"
            : SelectedFolder.Id;

        await _library.AddFolderAsync(NewFolderName.Trim(), parentId);
        NewFolderName = "";
        ClearError();
        await LoadDataAsync();
        StatusMessage = $"已在「{GetFolderPath(Folders.First(f => f.Id == parentId))}」下新建文件夹";
    }

    public async Task RenameFolderCoreAsync(string newName)
    {
        var folder = SelectedFolder;
        if (folder == null || folder.Id == "root")
        {
            ErrorMessage = "不能重命名根文件夹";
            return;
        }

        await _library.RenameFolderAsync(folder.Id, newName);
        folder.Name = newName.Trim();
        SelectedFolderNode?.NotifyNameChanged();
        await LoadDataAsync();
        StatusMessage = $"已重命名文件夹为「{folder.Name}」";
    }

    private async Task DeleteFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder == null || folder.Id == "root")
        {
            ErrorMessage = "不能删除根文件夹";
            return;
        }
        await _library.DeleteFolderAsync(folder.Id);
        SelectedFolder = null;
        await LoadDataAsync();
    }

    public async Task UpdateItemPropertiesAsync(LibraryItem item, string name, string? notes, string? source)
    {
        ClearError();
        var n = (name ?? "").Trim();
        if (string.IsNullOrEmpty(n))
        {
            ErrorMessage = "文献名称不能为空";
            return;
        }

        item.Name = n;
        item.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        item.Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        await _library.UpdateItemAsync(item);
        await LoadItemsAsync();
        StatusMessage = $"已更新属性: {item.Name}";
    }

    public async Task MergeSelectedManagedAsync(string? displayName = null)
    {
        ClearError();
        if (_imageDocs == null)
        {
            ErrorMessage = "合并服务未就绪";
            return;
        }

        var selected = Items
            .Where(i => i.IsSelectedForMerge)
            .ToList();
        if (selected.Count < 2)
        {
            ErrorMessage = "请勾选至少两篇图片/截图文献再合并";
            return;
        }

        if (selected.Any(i => !ClientDataPaths.IsManagedImportPath(i.PdfPath)))
        {
            ErrorMessage = "只能合并图片导入或截图导入的文献";
            return;
        }

        var merged = await _imageDocs.MergeManagedItemsAsync(selected, displayName);
        merged.FolderId = SelectedFolder?.Id ?? selected[0].FolderId;
        await _library.AddItemAsync(merged);

        foreach (var old in selected)
            await DeleteItemCoreAsync(old);

        await LoadItemsAsync();
        StatusMessage = $"已合并为「{merged.Name}」（{merged.TotalPages} 页）";
    }

    private async Task DeleteItemAsync()
    {
        var item = SelectedItem;
        if (item == null)
        {
            ErrorMessage = "请先在列表中选中要删除的文献";
            return;
        }
        await DeleteItemCoreAsync(item);
    }

    /// <summary>删除指定文献（由行内按钮调用）。</summary>
    public async Task DeleteItemCoreAsync(LibraryItem item)
    {
        ClearError();
        var outputDir = item.OutputDir;
        await _library.DeleteItemAsync(item.Id);
        _taskSync?.AbandonOutputDir(outputDir);
        _imageDocs?.TryDeleteManagedFiles(item);
        if (SelectedItem?.Id == item.Id)
            SelectedItem = null;
        Items.Remove(item);
        StatusMessage = $"已删除: {item.Name}";
    }

    public void RequestScreenshotImport() => ScreenshotImportRequested?.Invoke();
    public void RequestEditItem(LibraryItem item) => EditItemRequested?.Invoke(item);

    /// <summary>将文献移动到目标文件夹。</summary>
    public async Task MoveItemToFolderAsync(LibraryItem item, string folderId)
    {
        ClearError();
        var targetId = string.IsNullOrWhiteSpace(folderId) ? "root" : folderId;
        if (string.Equals(item.FolderId, targetId, StringComparison.Ordinal))
        {
            StatusMessage = "文献已在该文件夹中";
            return;
        }

        var folderName = Folders.FirstOrDefault(f => f.Id == targetId)?.Name ?? targetId;
        await _library.MoveItemToFolderAsync(item.Id, targetId);
        item.FolderId = targetId;
        await LoadItemsAsync();
        StatusMessage = $"已将「{item.Name}」移至「{folderName}」";
    }

    public void OpenItem(LibraryItem item)
    {
        System.Diagnostics.Debug.WriteLine($"[LibraryVM] OpenItem: {item.Name}");
        OpenItemRequested?.Invoke(item);
    }
    public void StartOcr(LibraryItem item) => StartOcrRequested?.Invoke(item);
}
