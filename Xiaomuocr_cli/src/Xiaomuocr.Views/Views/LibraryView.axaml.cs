using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.IO;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;
using Xiaomuocr.Core.ViewModels;

namespace Xiaomuocr.Views.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ScreenshotImportBtn != null)
                ScreenshotImportBtn.IsVisible = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        };
    }

    private async void OnImportPdfClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 PDF 文献",
            AllowMultiple = true,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("PDF 文件") { Patterns = new[] { "*.pdf" } },
            }
        });

        foreach (var file in files)
        {
            var pdfPath = file.Path.LocalPath;
            var pdfName = System.IO.Path.GetFileNameWithoutExtension(pdfPath);
            var pdfDir = System.IO.Path.GetDirectoryName(pdfPath) ?? "";
            var outputDir = System.IO.Path.Combine(pdfDir, pdfName);

            var item = new LibraryItem
            {
                PdfPath = pdfPath,
                Name = pdfName,
                OutputDir = outputDir,  // OCR 结果存放目录
            };

            try
            {
                using var srv = new PdfRenderService();
                var info = await srv.OpenPdfAsync(pdfPath);
                item.TotalPages = info.TotalPages;
            }
            catch { /* ignore */ }

            await vm.AddItemAsync(item);
        }
    }

    private async void OnImportImagesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var imageDocs = vm.ImageDocs;
        if (imageDocs == null)
        {
            vm.ErrorMessage = "图片导入服务未就绪";
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片",
            AllowMultiple = true,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("图片文件")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff" },
                },
            }
        });

        var ok = 0;
        foreach (var file in files)
        {
            try
            {
                var localPath = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                {
                    // 部分选择器只给 URI，回退读流
                    await using var stream = await file.OpenReadAsync();
                    await using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    var name = Path.GetFileNameWithoutExtension(file.Name);
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"图片_{DateTime.Now:yyyyMMdd_HHmmss}";
                    var item = await imageDocs.CreateFromImageBytesAsync(ms.ToArray(), name, "图片导入");
                    await vm.AddItemAsync(item);
                }
                else
                {
                    var item = await imageDocs.CreateFromImageFileAsync(localPath);
                    await vm.AddItemAsync(item);
                }
                ok++;
            }
            catch (Exception ex)
            {
                vm.ErrorMessage = $"导入失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (ok > 0)
            vm.StatusMessage = $"已导入 {ok} 张图片";
        else if (files.Count > 0 && string.IsNullOrWhiteSpace(vm.ErrorMessage))
            vm.ErrorMessage = "导入未成功，请重试或换一张图片";
    }

    private void OnScreenshotImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            vm.ErrorMessage = "截图导入仅支持 Windows / macOS";
            return;
        }
        vm.RequestScreenshotImport();
    }

    private async void OnRenameFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        var folder = vm.SelectedFolder;
        if (folder == null || folder.Id == "root")
        {
            vm.ErrorMessage = "请选择可重命名的文件夹（不能改「全部文献」）";
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        var name = await ShowTextPromptAsync(owner, "重命名文件夹", "文件夹名称", folder.Name);
        if (name == null) return;
        try
        {
            await vm.RenameFolderCoreAsync(name);
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"重命名失败: {ex.Message}";
        }
    }

    private async void OnEditItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LibraryItem item) return;
        if (DataContext is not LibraryViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        var result = await ShowEditItemDialogAsync(owner, item);
        if (result == null) return;
        try
        {
            await vm.UpdateItemPropertiesAsync(item, result.Value.Name, result.Value.Notes, result.Value.Source);
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"保存属性失败: {ex.Message}";
        }
    }

    private async void OnMergeSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var selected = vm.Items.Where(i => i.IsSelectedForMerge).ToList();
        if (selected.Count < 2)
        {
            vm.ErrorMessage = "请勾选至少两篇图片/截图文献再合并";
            return;
        }

        if (owner != null)
        {
            var ok = await ShowConfirmAsync(
                owner,
                "合并文献",
                $"将合并选中的 {selected.Count} 篇图片/截图文献为一篇多页文献。\n\n原文献记录与本地文件将删除（OCR 结果也会清除，需重新识别）。",
                confirmText: "合并",
                confirmColor: "#9B722E");
            if (!ok) return;

            var name = await ShowTextPromptAsync(
                owner, "合并文献", "合并后的名称",
                $"合并_{DateTime.Now:yyyyMMdd_HHmmss}");
            if (name == null) return;

            try
            {
                await vm.MergeSelectedManagedAsync(name);
            }
            catch (Exception ex)
            {
                vm.ErrorMessage = $"合并失败: {ex.Message}";
            }
        }
        else
        {
            try { await vm.MergeSelectedManagedAsync(null); }
            catch (Exception ex) { vm.ErrorMessage = $"合并失败: {ex.Message}"; }
        }
    }

    private void OnOpenItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryItem item
            && DataContext is LibraryViewModel vm)
        {
            vm.OpenItem(item);
        }
    }

    private async void OnMoveItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LibraryItem item) return;
        if (DataContext is not LibraryViewModel vm) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            vm.ErrorMessage = "无法打开移动对话框";
            return;
        }

        if (vm.Folders.Count == 0)
        {
            vm.ErrorMessage = "暂无文件夹，请先在左侧新建";
            return;
        }

        var target = await ShowMoveFolderPickerAsync(owner, item, vm);

        if (target == null) return;

        try
        {
            await vm.MoveItemToFolderAsync(item, target.Id);
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"移动失败: {ex.Message}";
        }
    }

    private async void OnStartOcrClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LibraryItem item) return;
        if (DataContext is not LibraryViewModel vm) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            vm.StartOcr(item);
            return;
        }

        var pending = OcrBillingConfirm.CountPendingPages(item.OutputDir, Math.Max(item.TotalPages, 0));
        if (pending <= 0)
        {
            await ShowConfirmAsync(
                owner,
                OcrBillingConfirm.LibraryTitle,
                item.TotalPages > 0
                    ? $"「{item.Name}」本地已全部识别完成，无需再次提交。"
                    : $"「{item.Name}」页数为 0，无法识别。",
                confirmText: "知道了",
                confirmColor: "#4A7C59");
            return;
        }

        var assets = await vm.TryGetAssetsAsync();
        var message = assets != null
            ? OcrBillingConfirm.BuildMessage(pending, assets, item.Name)
            : OcrBillingConfirm.BuildFetchFailedMessage(pending, item.Name);

        var ok = await ShowConfirmAsync(
            owner,
            OcrBillingConfirm.LibraryTitle,
            message);

        if (ok)
            vm.StartOcr(item);
    }

    private async void OnDeleteItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LibraryItem item) return;
        if (DataContext is not LibraryViewModel vm) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            var managed = ClientDataPaths.IsManagedImportPath(item.PdfPath);
            var body = managed
                ? $"确定从文献库删除「{item.Name}」吗？\n\n将同时删除本地导入文件与 OCR 结果。"
                : $"确定从文献库删除「{item.Name}」吗？\n\n仅移除库中记录，不会删除本地 PDF 与 OCR 结果文件。";
            var ok = await ShowConfirmAsync(
                owner,
                "确认删除",
                body,
                confirmText: "删除",
                confirmColor: "#B8443C");
            if (!ok) return;
        }

        try
        {
            await vm.DeleteItemCoreAsync(item);
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"删除失败: {ex.Message}";
        }
    }

    private static async Task<LibraryFolder?> ShowMoveFolderPickerAsync(
        Window owner, LibraryItem item, LibraryViewModel vm)
    {
        var folders = vm.Folders.ToList();
        var tcs = new TaskCompletionSource<LibraryFolder?>();
        LibraryFolder? selected = folders.FirstOrDefault(f => f.Id == item.FolderId) ?? folders.FirstOrDefault();

        var list = new ListBox
        {
            Height = 260,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // 按树路径排序展示：根 → 子 → 孙
        var ordered = OrderFoldersForDisplay(folders);
        foreach (var f in ordered)
        {
            var path = vm.GetFolderPath(f);
            var mark = f.Id == item.FolderId ? "（当前）" : "";
            list.Items.Add(new ListBoxItem
            {
                Content = $"{path}{mark}",
                Tag = f,
                IsSelected = selected?.Id == f.Id,
            });
        }
        if (list.ItemCount > 0 && list.SelectedIndex < 0)
            list.SelectedIndex = 0;

        var okBtn = new Button
        {
            Content = "移动到此文件夹",
            Background = Brush.Parse("#4A7C59"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 120,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };
        var cancelBtn = new Button
        {
            Content = "取消",
            Background = Brush.Parse("#D4E5D9"),
            Foreground = Brush.Parse("#3D4A3E"),
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };

        var dlg = new Window
        {
            Title = $"移动「{item.Name}」",
            Width = 420,
            Height = 400,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new Grid
                {
                    RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "选择目标文件夹（含子文件夹）",
                            FontSize = 13,
                            Foreground = Brush.Parse("#5C5344"),
                            [Grid.RowProperty] = 0,
                        },
                        list,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 12, 0, 0),
                            [Grid.RowProperty] = 2,
                            Children = { cancelBtn, okBtn },
                        },
                    },
                },
            },
        };
        Grid.SetRow(list, 1);

        okBtn.Click += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem li && li.Tag is LibraryFolder f)
                tcs.TrySetResult(f);
            else
                tcs.TrySetResult(null);
            dlg.Close();
        };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(null);

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    private static List<LibraryFolder> OrderFoldersForDisplay(IReadOnlyList<LibraryFolder> folders)
    {
        var result = new List<LibraryFolder>();
        var byParent = folders
            .GroupBy(f => f.Id == "root" ? "" : (f.ParentId ?? "root"))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList());

        void Walk(string parentKey)
        {
            if (!byParent.TryGetValue(parentKey, out var kids)) return;
            foreach (var f in kids)
            {
                result.Add(f);
                Walk(f.Id);
            }
        }

        var root = folders.FirstOrDefault(f => f.Id == "root");
        if (root != null)
        {
            result.Add(root);
            Walk("root");
        }
        else
        {
            Walk("");
        }

        // 补上未挂上的
        foreach (var f in folders)
        {
            if (result.All(x => x.Id != f.Id))
                result.Add(f);
        }
        return result;
    }

    private static async Task<bool> ShowConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "开始识别",
        string confirmColor = "#9B722E")
    {
        var tcs = new TaskCompletionSource<bool>();

        var okBtn = new Button
        {
            Content = confirmText,
            Background = Brush.Parse(confirmColor),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 100,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };
        var cancelBtn = new Button
        {
            Content = "取消",
            Background = Brush.Parse("#D4E5D9"),
            Foreground = Brush.Parse("#3D4A3E"),
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };

        var dlg = new Window
        {
            Title = title,
            Width = 460,
            Height = 300,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new Grid
                {
                    RowDefinitions = RowDefinitions.Parse("*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 14,
                            Foreground = Brush.Parse("#2D3A2C"),
                            [Grid.RowProperty] = 0,
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 16, 0, 0),
                            [Grid.RowProperty] = 1,
                            Children = { cancelBtn, okBtn },
                        },
                    },
                },
            },
        };

        okBtn.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false);

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    private static async Task<string?> ShowTextPromptAsync(
        Window owner, string title, string label, string initial)
    {
        var tcs = new TaskCompletionSource<string?>();
        var box = new TextBox
        {
            Text = initial,
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var okBtn = new Button
        {
            Content = "确定",
            Background = Brush.Parse("#4A7C59"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };
        var cancelBtn = new Button
        {
            Content = "取消",
            Background = Brush.Parse("#D4E5D9"),
            Foreground = Brush.Parse("#3D4A3E"),
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };

        var dlg = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new Grid
                {
                    RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = label,
                            FontSize = 13,
                            Foreground = Brush.Parse("#5C5344"),
                            [Grid.RowProperty] = 0,
                        },
                        box,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            [Grid.RowProperty] = 3,
                            Children = { cancelBtn, okBtn },
                        },
                    },
                },
            },
        };
        Grid.SetRow(box, 1);

        okBtn.Click += (_, _) =>
        {
            var t = box.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(t)) return;
            tcs.TrySetResult(t);
            dlg.Close();
        };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(null);
        dlg.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    private static async Task<(string Name, string? Notes, string? Source)?> ShowEditItemDialogAsync(
        Window owner, LibraryItem item)
    {
        var tcs = new TaskCompletionSource<(string, string?, string?)?>();
        var nameBox = new TextBox { Text = item.Name, FontSize = 14 };
        var sourceBox = new TextBox { Text = item.Source ?? "", FontSize = 14, Watermark = "如：图片导入 / 截图 / 自拟" };
        var notesBox = new TextBox
        {
            Text = item.Notes ?? "",
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            Watermark = "备注（可选）",
        };

        var okBtn = new Button
        {
            Content = "保存",
            Background = Brush.Parse("#4A7C59"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };
        var cancelBtn = new Button
        {
            Content = "取消",
            Background = Brush.Parse("#D4E5D9"),
            Foreground = Brush.Parse("#3D4A3E"),
            MinWidth = 88,
            MinHeight = 36,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(6),
        };

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock { Text = "名称", FontSize = 12, Foreground = Brush.Parse("#5C5344") });
        form.Children.Add(nameBox);
        form.Children.Add(new TextBlock { Text = "来源", FontSize = 12, Foreground = Brush.Parse("#5C5344") });
        form.Children.Add(sourceBox);
        form.Children.Add(new TextBlock { Text = "备注", FontSize = 12, Foreground = Brush.Parse("#5C5344") });
        form.Children.Add(notesBox);
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { cancelBtn, okBtn },
        });

        var dlg = new Window
        {
            Title = "修改文献属性",
            Width = 440,
            Height = 360,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(20), Child = form },
        };

        okBtn.Click += (_, _) =>
        {
            var n = nameBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(n)) return;
            tcs.TrySetResult((n, notesBox.Text, sourceBox.Text));
            dlg.Close();
        };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(null);

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }
}
