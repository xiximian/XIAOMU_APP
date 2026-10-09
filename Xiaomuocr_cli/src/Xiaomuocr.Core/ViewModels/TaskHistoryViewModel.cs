using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class TaskHistoryViewModel : ViewModelBase
{
    private readonly IOcrService _ocr;

    public ObservableCollection<OcrTask> Tasks { get; } = new();

    private OcrTask? _selectedTask;
    public OcrTask? SelectedTask
    {
        get => _selectedTask;
        set => this.RaiseAndSetIfChanged(ref _selectedTask, value);
    }

    private string? _statusFilter;
    public string? StatusFilter
    {
        get => _statusFilter;
        set => this.RaiseAndSetIfChanged(ref _statusFilter, value);
    }

    private string _searchKeyword = "";
    public string SearchKeyword
    {
        get => _searchKeyword;
        set => this.RaiseAndSetIfChanged(ref _searchKeyword, value);
    }

    private int _page = 1;
    public int Page
    {
        get => _page;
        set => this.RaiseAndSetIfChanged(ref _page, value);
    }

    private int _totalPages;
    public int TotalPages
    {
        get => _totalPages;
        set => this.RaiseAndSetIfChanged(ref _totalPages, value);
    }

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
    }

    public ReactiveCommand<Unit, Unit> LoadTasksCommand { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }
    public ReactiveCommand<string?, Unit> FilterCommand { get; }

    public TaskHistoryViewModel(IOcrService ocr)
    {
        _ocr = ocr;

        LoadTasksCommand = ReactiveCommand.CreateFromTask(LoadTasksAsync);
        NextPageCommand = ReactiveCommand.CreateFromTask(NextPageAsync,
            this.WhenAnyValue(x => x.Page, x => x.TotalPages, (p, t) => p < t));
        PrevPageCommand = ReactiveCommand.CreateFromTask(PrevPageAsync,
            this.WhenAnyValue(x => x.Page, p => p > 1));
        FilterCommand = ReactiveCommand.Create<string?>(s =>
        {
            StatusFilter = s;
            Page = 1;
            _ = LoadTasksAsync();
        });

        _ = LoadTasksAsync();
    }

    private async Task LoadTasksAsync()
    {
        IsBusy = true;
        ClearError();
        StatusMessage = "正在加载...";
        try
        {
            var result = await _ocr.GetTasksAsync(Page, 20, StatusFilter);
            StatusMessage = $"API返回: Total={result.Total}, Items.Count={result.Items.Count}";
            Tasks.Clear();
            foreach (var t in result.Items) Tasks.Add(t);
            TotalPages = result.TotalPages;
            IsEmpty = Tasks.Count == 0;
            StatusMessage = IsEmpty ? "加载完成，但数据为空" : $"加载完成，共 {Tasks.Count} 条";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败: {ex.GetType().Name}: {ex.Message}";
            StatusMessage = null;
            IsEmpty = true;
        }
        finally { IsBusy = false; }
    }

    private async Task NextPageAsync()
    {
        Page++;
        await LoadTasksAsync();
    }

    private async Task PrevPageAsync()
    {
        if (Page > 1) Page--;
        await LoadTasksAsync();
    }
}
