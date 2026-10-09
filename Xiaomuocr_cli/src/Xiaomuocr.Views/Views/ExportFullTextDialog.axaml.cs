using Avalonia.Controls;
using Avalonia.Interactivity;
using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Views.Views;

public partial class ExportFullTextDialog : Window
{
    private readonly int _totalPages;
    private readonly bool _canPunctuate;
    public FullTextExportOptions? Result { get; private set; }

    public ExportFullTextDialog() : this(1)
    {
    }

    public ExportFullTextDialog(int totalPages, bool canPunctuate = true)
    {
        _totalPages = Math.Max(1, totalPages);
        _canPunctuate = canPunctuate;
        InitializeComponent();
        PageFromBox.Text = "1";
        PageToBox.Text = _totalPages.ToString();
        PageHint.Text = $"（共 {_totalPages} 页）";
        if (!_canPunctuate)
        {
            ApplyPunctuateCheck.IsEnabled = false;
            ToolTip.SetTip(ApplyPunctuateCheck, "需登录且句逗服务可用（当前离线或服务未就绪）");
        }
        OkBtn.Click += OnOk;
        CancelBtn.Click += OnCancel;
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close(null);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        ErrorLabel.IsVisible = false;
        if (!int.TryParse(PageFromBox.Text?.Trim(), out int from)
            || !int.TryParse(PageToBox.Text?.Trim(), out int to))
        {
            ShowError("请输入有效的页码数字");
            return;
        }

        from = Math.Clamp(from, 1, _totalPages);
        to = Math.Clamp(to, 1, _totalPages);
        if (to < from)
            (from, to) = (to, from);

        var applyPunctuate = ApplyPunctuateCheck.IsChecked == true;
        if (applyPunctuate && !_canPunctuate)
        {
            ShowError("句逗导出需要在线且句逗服务可用");
            return;
        }

        var format = FormatMd.IsChecked == true
            ? FullTextExportFormat.Markdown
            : FullTextExportFormat.Txt;

        Result = new FullTextExportOptions
        {
            UseSimplified = ModeSimplified.IsChecked == true,
            ApplyPunctuate = applyPunctuate,
            Format = format,
            PageFrom = from,
            PageTo = to,
        };
        Close(Result);
    }

    private void ShowError(string msg)
    {
        ErrorLabel.Text = msg;
        ErrorLabel.IsVisible = true;
    }
}
