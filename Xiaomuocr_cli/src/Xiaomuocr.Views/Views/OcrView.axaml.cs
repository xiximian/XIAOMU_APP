using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Xiaomuocr.Core.ViewModels;

namespace Xiaomuocr.Views.Views;

public partial class OcrView : UserControl
{
    public OcrView()
    {
        InitializeComponent();
    }

    private async void OnSelectFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OcrViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var filters = new List<FilePickerFileType>
        {
            new("图片文件")
            {
                Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tiff", "*.tif" }
            },
            new("所有文件") { Patterns = new[] { "*.*" } }
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片文件",
            AllowMultiple = false,
            FileTypeFilter = filters,
        });

        if (files.Count > 0)
        {
            vm.SelectedFilePath = files[0].Path.LocalPath;
        }
    }

    private async void OnCopyResultClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OcrViewModel vm) return;
        if (string.IsNullOrEmpty(vm.ResultText)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
            await topLevel.Clipboard.SetTextAsync(vm.ResultText);
    }

    private async void OnSaveResultClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OcrViewModel vm) return;
        if (string.IsNullOrEmpty(vm.ResultText)) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var choices = new List<FilePickerFileType>
        {
            new("文本文件") { Patterns = new[] { "*.txt" } }
        };

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存识别结果",
            DefaultExtension = ".txt",
            FileTypeChoices = choices,
        });

        if (file != null)
            await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, vm.ResultText);
    }
}
