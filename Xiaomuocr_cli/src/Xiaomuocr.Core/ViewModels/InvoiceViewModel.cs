using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class InvoiceViewModel : ViewModelBase
{
    private readonly IInvoiceService _invoice;

    public ObservableCollection<InvoiceListItem> Items { get; } = new();

    private string _notice = "";
    public string Notice
    {
        get => _notice;
        set => this.RaiseAndSetIfChanged(ref _notice, value);
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateCommand { get; }

    /// <summary>由 View 打开新建/详情对话框。</summary>
    public Func<Task>? OpenCreateDialogAsync { get; set; }
    public Func<InvoiceListItem, Task>? OpenDetailDialogAsync { get; set; }

    public InvoiceViewModel(IInvoiceService invoice)
    {
        _invoice = invoice;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        CreateCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (OpenCreateDialogAsync != null)
                await OpenCreateDialogAsync();
        });
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            Notice = await _invoice.GetNoticeAsync();
            this.RaisePropertyChanged(nameof(HasNotice));
            var list = await _invoice.ListAsync();
            Items.Clear();
            foreach (var i in list)
                Items.Add(i);
            StatusMessage = $"共 {Items.Count} 条开票申请";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<List<InvoiceOrderBrief>> LoadEligibleOrdersAsync()
        => _invoice.GetEligibleOrdersAsync();

    public Task<InvoiceDetail> GetDetailAsync(int id) => _invoice.GetAsync(id);

    public async Task<InvoiceDetail> SubmitCreateAsync(InvoiceCreateRequest body)
    {
        var detail = await _invoice.CreateAsync(body);
        await LoadAsync();
        return detail;
    }

    public async Task<InvoiceDetail> SubmitUpdateAsync(int id, InvoiceCreateRequest body)
    {
        var detail = await _invoice.UpdateAsync(id, body);
        await LoadAsync();
        return detail;
    }

    public async Task<InvoiceDetail> SubmitMessageAsync(int id, string content)
    {
        var detail = await _invoice.PostMessageAsync(id, content);
        await LoadAsync();
        return detail;
    }

    public async Task OpenItemAsync(InvoiceListItem item)
    {
        if (OpenDetailDialogAsync != null)
            await OpenDetailDialogAsync(item);
    }
}
