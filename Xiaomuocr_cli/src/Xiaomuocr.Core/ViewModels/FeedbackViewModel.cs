using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Xiaomuocr.Core.Models;
using Xiaomuocr.Core.Services;

namespace Xiaomuocr.Core.ViewModels;

/// <summary>留言卡片：可通知属性变更，便于点赞/展开回复刷新。</summary>
public class BoardMessageCard : ReactiveObject
{
    public int Id { get; init; }
    public string AuthorDisplay { get; init; } = "";
    public string Content { get; init; } = "";
    public string? AdminReply { get; init; }
    public DateTime? CreatedAt { get; init; }
    public string Status { get; init; } = "";
    public bool IsMine { get; init; }
    public bool CanReply { get; init; }
    public bool CanLike => Status == "approved";
    public bool HasAdminReply => !string.IsNullOrWhiteSpace(AdminReply);
    public bool ShowStatusBadge => Status is "pending" or "rejected" or "hidden";
    public string StatusLabel => Status switch
    {
        "pending" => "待审核",
        "rejected" => "未通过",
        "hidden" => "已隐藏",
        _ => "",
    };

    private int _likeCount;
    public int LikeCount
    {
        get => _likeCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _likeCount, value);
            this.RaisePropertyChanged(nameof(LikeButtonText));
        }
    }

    private bool _likedByMe;
    public bool LikedByMe
    {
        get => _likedByMe;
        set
        {
            this.RaiseAndSetIfChanged(ref _likedByMe, value);
            this.RaisePropertyChanged(nameof(LikeButtonText));
        }
    }

    public string LikeButtonText => LikedByMe ? $"已赞 {LikeCount}" : $"点赞 {LikeCount}";

    private int _replyCount;
    public int ReplyCount
    {
        get => _replyCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _replyCount, value);
            this.RaisePropertyChanged(nameof(ShowMoreReplies));
            this.RaisePropertyChanged(nameof(MoreRepliesLabel));
        }
    }

    private BoardReplyItem? _latestReply;
    public BoardReplyItem? LatestReply
    {
        get => _latestReply;
        set
        {
            this.RaiseAndSetIfChanged(ref _latestReply, value);
            this.RaisePropertyChanged(nameof(HasLatestReply));
        }
    }

    private bool _repliesExpanded;
    public bool RepliesExpanded
    {
        get => _repliesExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _repliesExpanded, value);
            this.RaisePropertyChanged(nameof(HasLatestReply));
            this.RaisePropertyChanged(nameof(MoreRepliesLabel));
        }
    }

    public bool HasLatestReply => LatestReply != null && !RepliesExpanded;
    public bool ShowMoreReplies => ReplyCount > 0;
    public string MoreRepliesLabel =>
        RepliesExpanded ? "收起回复" : $"更多回复({Math.Min(Math.Max(ReplyCount, 0), 6)})";

    public ObservableCollection<BoardReplyItem> ExpandedReplies { get; } = new();

    private string _replyDraft = "";
    public string ReplyDraft
    {
        get => _replyDraft;
        set => this.RaiseAndSetIfChanged(ref _replyDraft, value);
    }

    private bool _replyBoxVisible;
    public bool ReplyBoxVisible
    {
        get => _replyBoxVisible;
        set => this.RaiseAndSetIfChanged(ref _replyBoxVisible, value);
    }

    public static BoardMessageCard FromItem(BoardMessageItem m) => new()
    {
        Id = m.Id,
        AuthorDisplay = m.AuthorDisplay,
        Content = m.Content,
        AdminReply = m.AdminReply,
        CreatedAt = m.CreatedAt,
        Status = m.Status,
        IsMine = m.IsMine,
        CanReply = m.CanReply,
        LikeCount = m.LikeCount,
        LikedByMe = m.LikedByMe,
        ReplyCount = m.ReplyCount,
        LatestReply = m.LatestReply,
    };
}

public class FeedbackViewModel : ViewModelBase
{
    private readonly IFeedbackService _feedback;

    public ObservableCollection<AnnouncementItem> Announcements { get; } = new();
    public ObservableCollection<BoardMessageCard> Messages { get; } = new();

    private bool _isAnnouncementsEmpty = true;
    public bool IsAnnouncementsEmpty
    {
        get => _isAnnouncementsEmpty;
        set => this.RaiseAndSetIfChanged(ref _isAnnouncementsEmpty, value);
    }

    private bool _isMessagesEmpty = true;
    public bool IsMessagesEmpty
    {
        get => _isMessagesEmpty;
        set => this.RaiseAndSetIfChanged(ref _isMessagesEmpty, value);
    }

    private string _newMessage = "";
    public string NewMessage
    {
        get => _newMessage;
        set => this.RaiseAndSetIfChanged(ref _newMessage, value);
    }

    private string _submitMessage = "";
    public string SubmitMessage
    {
        get => _submitMessage;
        set => this.RaiseAndSetIfChanged(ref _submitMessage, value);
    }

    private int _page = 1;
    public int Page
    {
        get => _page;
        set
        {
            this.RaiseAndSetIfChanged(ref _page, value);
            this.RaisePropertyChanged(nameof(PageLabel));
            this.RaisePropertyChanged(nameof(CanPrevPage));
            this.RaisePropertyChanged(nameof(CanNextPage));
        }
    }

    private int _totalPages = 1;
    public int TotalPages
    {
        get => _totalPages;
        set
        {
            this.RaiseAndSetIfChanged(ref _totalPages, value);
            this.RaisePropertyChanged(nameof(PageLabel));
            this.RaisePropertyChanged(nameof(CanPrevPage));
            this.RaisePropertyChanged(nameof(CanNextPage));
        }
    }

    public string PageLabel => $"第 {Page} / {TotalPages} 页";
    public bool CanPrevPage => Page > 1 && !IsBusy;
    public bool CanNextPage => Page < TotalPages && !IsBusy;

    private string _sortMode = "newest";
    public string SortMode
    {
        get => _sortMode;
        set => this.RaiseAndSetIfChanged(ref _sortMode, value);
    }

    public bool IsSortNewest => SortMode == "newest";
    public bool IsSortLikes => SortMode == "likes";

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> SubmitCommand { get; }
    public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }
    public ReactiveCommand<Unit, Unit> NextPageCommand { get; }
    public ReactiveCommand<Unit, Unit> SortNewestCommand { get; }
    public ReactiveCommand<Unit, Unit> SortLikesCommand { get; }
    public ReactiveCommand<BoardMessageCard, Unit> LikeCommand { get; }
    public ReactiveCommand<BoardMessageCard, Unit> ToggleMoreRepliesCommand { get; }
    public ReactiveCommand<BoardMessageCard, Unit> ToggleReplyBoxCommand { get; }
    public ReactiveCommand<BoardMessageCard, Unit> SubmitReplyCommand { get; }

    public FeedbackViewModel(IFeedbackService feedback)
    {
        _feedback = feedback;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        var canSubmit = this.WhenAnyValue(
            x => x.NewMessage, x => x.IsBusy,
            (msg, busy) => !busy && !string.IsNullOrWhiteSpace(msg));
        SubmitCommand = ReactiveCommand.CreateFromTask(SubmitAsync, canSubmit);
        PrevPageCommand = ReactiveCommand.CreateFromTask(PrevPageAsync);
        NextPageCommand = ReactiveCommand.CreateFromTask(NextPageAsync);
        SortNewestCommand = ReactiveCommand.CreateFromTask(() => ChangeSortAsync("newest"));
        SortLikesCommand = ReactiveCommand.CreateFromTask(() => ChangeSortAsync("likes"));
        LikeCommand = ReactiveCommand.CreateFromTask<BoardMessageCard>(LikeAsync);
        ToggleMoreRepliesCommand = ReactiveCommand.CreateFromTask<BoardMessageCard>(ToggleMoreRepliesAsync);
        ToggleReplyBoxCommand = ReactiveCommand.Create<BoardMessageCard>(card =>
        {
            card.ReplyBoxVisible = !card.ReplyBoxVisible;
        });
        SubmitReplyCommand = ReactiveCommand.CreateFromTask<BoardMessageCard>(SubmitReplyAsync);
        _ = LoadAsync();
    }

    private async Task ChangeSortAsync(string sort)
    {
        SortMode = sort;
        this.RaisePropertyChanged(nameof(IsSortNewest));
        this.RaisePropertyChanged(nameof(IsSortLikes));
        Page = 1;
        await LoadMessagesAsync();
    }

    private async Task PrevPageAsync()
    {
        if (Page <= 1) return;
        Page--;
        await LoadMessagesAsync();
    }

    private async Task NextPageAsync()
    {
        if (Page >= TotalPages) return;
        Page++;
        await LoadMessagesAsync();
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        ClearError();
        SubmitMessage = "";
        this.RaisePropertyChanged(nameof(CanPrevPage));
        this.RaisePropertyChanged(nameof(CanNextPage));
        try
        {
            var anns = await _feedback.GetAnnouncementsAsync();
            Announcements.Clear();
            foreach (var a in anns) Announcements.Add(a);
            IsAnnouncementsEmpty = Announcements.Count == 0;
            await LoadMessagesAsync(keepBusy: true);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            this.RaisePropertyChanged(nameof(CanPrevPage));
            this.RaisePropertyChanged(nameof(CanNextPage));
        }
    }

    private async Task LoadMessagesAsync(bool keepBusy = false)
    {
        if (!keepBusy)
        {
            IsBusy = true;
            this.RaisePropertyChanged(nameof(CanPrevPage));
            this.RaisePropertyChanged(nameof(CanNextPage));
        }
        try
        {
            var resp = await _feedback.GetMessagesAsync(Page, SortMode);
            Messages.Clear();
            foreach (var m in resp.Items)
                Messages.Add(BoardMessageCard.FromItem(m));
            IsMessagesEmpty = Messages.Count == 0;
            TotalPages = Math.Max(1, resp.TotalPages);
            Page = Math.Min(Math.Max(1, resp.Page), TotalPages);
        }
        finally
        {
            if (!keepBusy)
            {
                IsBusy = false;
                this.RaisePropertyChanged(nameof(CanPrevPage));
                this.RaisePropertyChanged(nameof(CanNextPage));
            }
        }
    }

    private async Task SubmitAsync()
    {
        IsBusy = true;
        ClearError();
        SubmitMessage = "";
        try
        {
            await _feedback.SubmitMessageAsync(NewMessage.Trim());
            NewMessage = "";
            SubmitMessage = "已提交，审核通过后其他人可见";
            Page = 1;
            SortMode = "newest";
            this.RaisePropertyChanged(nameof(IsSortNewest));
            this.RaisePropertyChanged(nameof(IsSortLikes));
            await LoadMessagesAsync(keepBusy: true);
        }
        catch (ApiException ex)
        {
            SubmitMessage = ex.Message;
        }
        catch (Exception ex)
        {
            SubmitMessage = $"提交失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LikeAsync(BoardMessageCard card)
    {
        if (!card.CanLike) return;
        try
        {
            var r = await _feedback.ToggleLikeAsync(card.Id);
            card.LikeCount = r.LikeCount;
            card.LikedByMe = r.LikedByMe;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ToggleMoreRepliesAsync(BoardMessageCard card)
    {
        if (card.RepliesExpanded)
        {
            card.RepliesExpanded = false;
            card.ExpandedReplies.Clear();
            return;
        }
        try
        {
            var list = await _feedback.GetRepliesAsync(card.Id);
            card.ExpandedReplies.Clear();
            foreach (var r in list) card.ExpandedReplies.Add(r);
            card.ReplyCount = Math.Max(card.ReplyCount, list.Count);
            card.RepliesExpanded = true;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task SubmitReplyAsync(BoardMessageCard card)
    {
        if (!card.CanReply || string.IsNullOrWhiteSpace(card.ReplyDraft)) return;
        try
        {
            var reply = await _feedback.SubmitReplyAsync(card.Id, card.ReplyDraft.Trim());
            card.ReplyDraft = "";
            card.ReplyBoxVisible = false;
            card.LatestReply = reply;
            card.ReplyCount += 1;
            if (card.RepliesExpanded)
            {
                card.ExpandedReplies.Insert(0, reply);
                while (card.ExpandedReplies.Count > 6)
                    card.ExpandedReplies.RemoveAt(card.ExpandedReplies.Count - 1);
            }
            SubmitMessage = "回复已提交，审核通过后其他人可见";
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
