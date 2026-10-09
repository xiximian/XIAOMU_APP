using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

public class TaskCenterViewModel : ViewModelBase
{
    private readonly ITaskSyncService _sync;

    public ObservableCollection<RecognitionTaskItem> FilteredTasks { get; } = new();

    private string? _statusFilter;
    public string? StatusFilter
    {
        get => _statusFilter;
        set => this.RaiseAndSetIfChanged(ref _statusFilter, value);
    }

    private RecognitionTaskItem? _selected;
    public RecognitionTaskItem? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncResultsCommand { get; }
    public ReactiveCommand<string?, Unit> FilterCommand { get; }
    public ReactiveCommand<RecognitionTaskItem, Unit> CancelCommand { get; }

    public TaskCenterViewModel(ITaskSyncService sync)
    {
        _sync = sync;
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        SyncResultsCommand = ReactiveCommand.CreateFromTask(SyncResultsAsync);
        FilterCommand = ReactiveCommand.Create<string?>(f =>
        {
            StatusFilter = f;
            RebuildFilter();
        });
        CancelCommand = ReactiveCommand.CreateFromTask<RecognitionTaskItem>(CancelAsync);

        _sync.TasksChanged += () =>
        {
            var ctx = SynchronizationContext.Current;
            if (ctx != null) ctx.Post(_ => RebuildFilter(), null);
            else RebuildFilter();
        };
        RebuildFilter();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            await _sync.RefreshAsync();
            RebuildFilter();
            StatusMessage = $"共 {_sync.Tasks.Count} 个任务";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncResultsAsync()
    {
        IsBusy = true;
        ClearError();
        StatusMessage = "正在同步结果到本地…";
        try
        {
            StatusMessage = await _sync.SyncLocalResultsAsync();
            RebuildFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CancelAsync(RecognitionTaskItem item)
    {
        if (item == null || !item.CanCancel) return;
        try
        {
            await _sync.CancelAsync(item.BatchUuid);
            StatusMessage = $"已请求取消: {item.DisplayName}";
            RebuildFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public void SelectBatch(string batchUuid)
    {
        Selected = FilteredTasks.FirstOrDefault(t => t.BatchUuid == batchUuid)
                   ?? _sync.Tasks.FirstOrDefault(t => t.BatchUuid == batchUuid);
    }

    private void RebuildFilter()
    {
        FilteredTasks.Clear();
        IEnumerable<RecognitionTaskItem> q = _sync.Tasks;
        if (!string.IsNullOrEmpty(StatusFilter))
        {
            q = StatusFilter switch
            {
                "active" => q.Where(t => t.IsActive),
                "completed" => q.Where(t => t.Status is "completed" or "partial_failed"),
                "cancelled" => q.Where(t => t.Status == "cancelled"),
                "failed" => q.Where(t => t.Status == "failed" || t.FailedPages > 0 && t.Status != "cancelled"),
                _ => q.Where(t => t.Status == StatusFilter),
            };
        }
        foreach (var t in TaskSyncService.OrderByRecent(q))
            FilteredTasks.Add(t);
        IsEmpty = FilteredTasks.Count == 0;
    }
}
