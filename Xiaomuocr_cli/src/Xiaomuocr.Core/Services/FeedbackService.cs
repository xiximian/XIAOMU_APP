using Xiaomuocr.Core.Models;

namespace Xiaomuocr.Core.Services;

public interface IFeedbackService
{
    Task<List<AnnouncementItem>> GetAnnouncementsAsync();
    Task<BoardMessageListResponse> GetMessagesAsync(int page, string sort);
    Task<BoardMessageItem> SubmitMessageAsync(string content);
    Task<BoardLikeResponse> ToggleLikeAsync(int messageId);
    Task<List<BoardReplyItem>> GetRepliesAsync(int messageId);
    Task<BoardReplyItem> SubmitReplyAsync(int messageId, string content);
}

public class FeedbackService : IFeedbackService
{
    private readonly IApiService _api;

    public FeedbackService(IApiService api)
    {
        _api = api;
    }

    public Task<List<AnnouncementItem>> GetAnnouncementsAsync()
        => _api.GetAsync<List<AnnouncementItem>>(Constants.Endpoints.FeedbackAnnouncements);

    public Task<BoardMessageListResponse> GetMessagesAsync(int page, string sort)
    {
        var s = Uri.EscapeDataString(sort);
        return _api.GetAsync<BoardMessageListResponse>(
            $"{Constants.Endpoints.FeedbackMessages}?page={page}&page_size=20&sort={s}");
    }

    public Task<BoardMessageItem> SubmitMessageAsync(string content)
        => _api.PostAsync<BoardMessageItem>(
            Constants.Endpoints.FeedbackMessages,
            new BoardMessageCreateRequest { Content = content });

    public Task<BoardLikeResponse> ToggleLikeAsync(int messageId)
        => _api.PostAsync<BoardLikeResponse>(Constants.Endpoints.FeedbackMessageLike(messageId));

    public Task<List<BoardReplyItem>> GetRepliesAsync(int messageId)
        => _api.GetAsync<List<BoardReplyItem>>(Constants.Endpoints.FeedbackMessageReplies(messageId));

    public Task<BoardReplyItem> SubmitReplyAsync(int messageId, string content)
        => _api.PostAsync<BoardReplyItem>(
            Constants.Endpoints.FeedbackMessageReplies(messageId),
            new BoardMessageCreateRequest { Content = content });
}
