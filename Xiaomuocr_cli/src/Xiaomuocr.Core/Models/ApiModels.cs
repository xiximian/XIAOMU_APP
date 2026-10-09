using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Xiaomuocr.Core.Models;

// ==================== Auth ====================

public class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "bearer";

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }
}

public class UserInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("is_admin")]
    public bool IsAdmin { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("last_login_at")]
    public DateTime? LastLoginAt { get; set; }

    [JsonPropertyName("auth_source")]
    public string? AuthSource { get; set; }

    [JsonPropertyName("wx_nickname")]
    public string? WxNickname { get; set; }

    [JsonPropertyName("wx_avatar_url")]
    public string? WxAvatarUrl { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

public class LoginRequest
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class RegisterRequest
{
    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [JsonPropertyName("verify_code")]
    public string VerifyCode { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class SmsCodeRequest
{
    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [JsonPropertyName("template_type")]
    public string TemplateType { get; set; } = "register";
}

public class ResetPasswordRequest
{
    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [JsonPropertyName("verify_code")]
    public string VerifyCode { get; set; } = "";

    [JsonPropertyName("new_password")]
    public string NewPassword { get; set; } = "";
}

public class RefreshRequest
{
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";
}

public class WxQrStartResponse
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("qr_content")]
    public string QrContent { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

public class WxQrPollResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "bearer";

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }
}

public class ProfileUpdateRequest
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

// ==================== Feedback ====================

public class AnnouncementItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }
}

public class BoardReplyItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("author_display")]
    public string AuthorDisplay { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("is_mine")]
    public bool IsMine { get; set; }

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        "pending" => "待审核",
        "rejected" => "未通过",
        "hidden" => "已隐藏",
        _ => "",
    };

    [JsonIgnore]
    public bool ShowStatusBadge => Status is "pending" or "rejected" or "hidden";
}

public class BoardMessageItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("author_display")]
    public string AuthorDisplay { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("admin_reply")]
    public string? AdminReply { get; set; }

    [JsonPropertyName("admin_replied_at")]
    public DateTime? AdminRepliedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("is_mine")]
    public bool IsMine { get; set; }

    [JsonPropertyName("like_count")]
    public int LikeCount { get; set; }

    [JsonPropertyName("liked_by_me")]
    public bool LikedByMe { get; set; }

    [JsonPropertyName("reply_count")]
    public int ReplyCount { get; set; }

    [JsonPropertyName("latest_reply")]
    public BoardReplyItem? LatestReply { get; set; }

    [JsonPropertyName("can_reply")]
    public bool CanReply { get; set; }

    [JsonIgnore]
    public bool HasAdminReply => !string.IsNullOrWhiteSpace(AdminReply);

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        "pending" => "待审核",
        "rejected" => "未通过",
        "hidden" => "已隐藏",
        "approved" => "",
        _ => Status,
    };

    [JsonIgnore]
    public bool ShowStatusBadge => Status is "pending" or "rejected" or "hidden";

    [JsonIgnore]
    public string LikeButtonText => LikedByMe ? $"已赞 {LikeCount}" : $"点赞 {LikeCount}";

    [JsonIgnore]
    public bool HasLatestReply => LatestReply != null && !RepliesExpanded;

    [JsonIgnore]
    public bool ShowMoreReplies => ReplyCount > 0;

    [JsonIgnore]
    public bool RepliesExpanded { get; set; }

    [JsonIgnore]
    public string MoreRepliesLabel => RepliesExpanded ? "收起回复" : $"更多回复({Math.Min(Math.Max(ReplyCount, 0), 6)})";

    [JsonIgnore]
    public ObservableCollection<BoardReplyItem> ExpandedReplies { get; } = new();

    [JsonIgnore]
    public string ReplyDraft { get; set; } = "";

    [JsonIgnore]
    public bool ReplyBoxVisible { get; set; }

    [JsonIgnore]
    public bool CanLike => Status == "approved";
}

public class BoardMessageListResponse
{
    [JsonPropertyName("items")]
    public List<BoardMessageItem> Items { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("sort")]
    public string Sort { get; set; } = "newest";
}

public class BoardLikeResponse
{
    [JsonPropertyName("message_id")]
    public int MessageId { get; set; }

    [JsonPropertyName("like_count")]
    public int LikeCount { get; set; }

    [JsonPropertyName("liked_by_me")]
    public bool LikedByMe { get; set; }
}

public class BoardMessageCreateRequest
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

// ==================== Balance ====================

public class BalanceInfo
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("frozen_amount")]
    public decimal FrozenAmount { get; set; }

    [JsonPropertyName("available")]
    public decimal Available { get; set; }

    [JsonPropertyName("total_recharged")]
    public decimal TotalRecharged { get; set; }

    [JsonPropertyName("total_consumed")]
    public decimal TotalConsumed { get; set; }
}

// ==================== Pricing ====================

public class PricingPlan
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("plan_type")]
    public string PlanType { get; set; } = "";

    [JsonPropertyName("page_count")]
    public int PageCount { get; set; }

    [JsonPropertyName("total_price")]
    public decimal TotalPrice { get; set; }

    [JsonPropertyName("unit_price_in")]
    public decimal UnitPriceIn { get; set; }

    [JsonPropertyName("unit_price_out")]
    public decimal UnitPriceOut { get; set; }

    [JsonPropertyName("validity_days")]
    public int ValidityDays { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; }

    [JsonIgnore]
    public bool IsPackage => string.Equals(PlanType, "package", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string PriceDisplay => IsPackage
        ? $"¥{TotalPrice:F2}"
        : $"¥{UnitPriceOut:F2}/页";

    [JsonIgnore]
    public string UnitPriceDisplay => IsPackage
        ? $"折合 ¥{UnitPriceIn:F2}/页"
        : $"按量计费";

    [JsonIgnore]
    public string PagesDisplay => IsPackage ? $"{PageCount} 页识别" : "用多少付多少";

    [JsonIgnore]
    public string ValidityDisplay => ValidityDays > 0 ? $"有效期 {ValidityDays} 天" : "长期有效";
}

public class PricingConfig
{
    [JsonPropertyName("payg_unit_price")]
    public decimal PaygUnitPrice { get; set; }

    [JsonPropertyName("plans")]
    public List<PricingPlan> Plans { get; set; } = new();
}

public class UserPackageInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("plan_id")]
    public int PlanId { get; set; }

    [JsonPropertyName("plan_name")]
    public string? PlanName { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("used_count")]
    public int UsedCount { get; set; }

    [JsonPropertyName("frozen_count")]
    public int FrozenCount { get; set; }

    [JsonPropertyName("remaining_count")]
    public int RemainingCount { get; set; }

    [JsonPropertyName("available_count")]
    public int AvailableCount { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("purchased_at")]
    public DateTime? PurchasedAt { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(PlanName) ? $"套餐 #{Id}" : PlanName!;

    [JsonIgnore]
    public string RemainingDisplay => $"剩余 {AvailableCount} / {TotalCount} 页";

    [JsonIgnore]
    public string ExpiresDisplay => ExpiresAt.HasValue
        ? $"到期 {ExpiresAt:yyyy-MM-dd}"
        : "长期有效";
}

public class UserAssets
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("frozen_amount")]
    public decimal FrozenAmount { get; set; }

    [JsonPropertyName("available_balance")]
    public decimal AvailableBalance { get; set; }

    [JsonPropertyName("total_recharged")]
    public decimal TotalRecharged { get; set; }

    [JsonPropertyName("total_consumed")]
    public decimal TotalConsumed { get; set; }

    [JsonPropertyName("package_remaining")]
    public int PackageRemaining { get; set; }

    [JsonPropertyName("packages")]
    public List<UserPackageInfo> Packages { get; set; } = new();

    [JsonPropertyName("payg_unit_price")]
    public decimal PaygUnitPrice { get; set; }
}

public class PurchasePackageRequest
{
    [JsonPropertyName("plan_id")]
    public int PlanId { get; set; }

    [JsonPropertyName("payment_method")]
    public string PaymentMethod { get; set; } = "mock";
}

public class PurchasePackageResult
{
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = "";

    [JsonPropertyName("package")]
    public UserPackageInfo Package { get; set; } = new();

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";
}

public class ConsumptionRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>后端返回的中文类型；缺失时由客户端推导。</summary>
    [JsonPropertyName("type_label")]
    public string? TypeLabelRaw { get; set; }

    [JsonPropertyName("txn_type")]
    public string? TxnType { get; set; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>计费流水接口用 total_amount</summary>
    [JsonPropertyName("total_amount")]
    public decimal? TotalAmount { get; set; }

    [JsonPropertyName("balance_after")]
    public decimal? BalanceAfter { get; set; }

    [JsonPropertyName("package_remaining_after")]
    public int? PackageRemainingAfter { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("reference_id")]
    public string? ReferenceId { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>列表展示：中文类型</summary>
    [JsonIgnore]
    public string TypeLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(TypeLabelRaw))
                return TypeLabelRaw!;
            var key = !string.IsNullOrWhiteSpace(TxnType) ? TxnType! : Type;
            return key switch
            {
                "package_deduct" => "套餐扣费",
                "balance_deduct" => "余额扣费",
                "package_purchase" => "购买套餐",
                "recharge" => "余额充值",
                "refund" => "退款/退回",
                "admin_grant" => "管理员赠送",
                "admin_package" => "管理员发放套餐",
                "unfreeze" => "预扣退回",
                "ocr_deduct" => (Description?.Contains("购买套餐") == true) ? "购买套餐" : "OCR扣费",
                _ => string.IsNullOrWhiteSpace(key) ? "其他" : key,
            };
        }
    }

    /// <summary>列表展示金额（兼容 balance/records 与 pricing/transactions）</summary>
    [JsonIgnore]
    public decimal DisplayAmount => TotalAmount ?? Amount;

    /// <summary>列表展示说明；空时给兜底文案</summary>
    [JsonIgnore]
    public string DisplayDescription
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Description))
                return Description!;
            if (Quantity is > 0)
                return $"{TypeLabel} {Quantity} 页";
            return TypeLabel;
        }
    }

    /// <summary>扣费后资产快照文案</summary>
    [JsonIgnore]
    public string AfterSnapshotText
    {
        get
        {
            var parts = new List<string>();
            if (BalanceAfter.HasValue)
                parts.Add($"余额 ¥{BalanceAfter.Value:0.00}");
            if (PackageRemainingAfter.HasValue)
                parts.Add($"套餐剩余 {PackageRemainingAfter.Value} 页");
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public bool HasAfterSnapshot => !string.IsNullOrEmpty(AfterSnapshotText);
}

public class RechargeRequest
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("payment_method")]
    public string PaymentMethod { get; set; } = "mock";
}

public class RechargeResult
{
    /// <summary>mock 模式下返回余额信息</summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("frozen_amount")]
    public decimal FrozenAmount { get; set; }

    [JsonPropertyName("available")]
    public decimal Available { get; set; }

    [JsonPropertyName("total_recharged")]
    public decimal TotalRecharged { get; set; }

    [JsonPropertyName("total_consumed")]
    public decimal TotalConsumed { get; set; }

    /// <summary>zpay 模式标识</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    /// <summary>zpay 支付跳转 URL（页面模式）</summary>
    [JsonPropertyName("pay_url")]
    public string PayUrl { get; set; } = "";

    /// <summary>zpay API 支付 URL</summary>
    [JsonPropertyName("payurl")]
    public string PayUrlApi { get; set; } = "";

    /// <summary>zpay 支付二维码内容字符串</summary>
    [JsonPropertyName("qrcode")]
    public string QrCode { get; set; } = "";

    /// <summary>zpay 二维码图片 URL</summary>
    [JsonPropertyName("img_url")]
    public string ImgUrl { get; set; } = "";

    /// <summary>商户订单号</summary>
    [JsonPropertyName("out_trade_no")]
    public string OutTradeNo { get; set; } = "";

    /// <summary>提示信息</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>支付渠道：alipay / wxpay</summary>
    [JsonPropertyName("payment_type")]
    public string PaymentType { get; set; } = "";

    /// <summary>是否需要展示二维码支付（ZPay 支付宝或 YunGouOS 微信）</summary>
    public bool IsQrPayMode =>
        (Mode is "zpay" or "yungouos")
        && (!string.IsNullOrEmpty(QrCode) || !string.IsNullOrEmpty(PayUrlApi));

    /// <summary>兼容旧调用名</summary>
    public bool IsZpayMode => IsQrPayMode;
}

// ==================== OSS ====================

public class UploadToken
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("upload_url")]
    public string UploadUrl { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("max_size")]
    public long MaxSize { get; set; }

    [JsonPropertyName("allowed_types")]
    public List<string> AllowedTypes { get; set; } = new();
}

public class UploadResult
{
    /// <summary>存储 key（local/jdcloud/qiniu）；不再返回 file_url。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }
}

// ==================== 批量上传凭证 ====================

public class BatchTokenFileItem
{
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "image/png";
}

public class BatchTokenRequest
{
    [JsonPropertyName("files")]
    public List<BatchTokenFileItem> Files { get; set; } = new();
}

public class BatchTokenItem
{
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("upload_url")]
    public string UploadUrl { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

public class BatchTokenResponse
{
    [JsonPropertyName("tokens")]
    public List<BatchTokenItem> Tokens { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

// ==================== OCR ====================

public class OcrRequest
{
    [JsonPropertyName("file_key")]
    public string FileKey { get; set; } = "";

    [JsonPropertyName("page_count")]
    public int PageCount { get; set; } = 1;

    [JsonPropertyName("options")]
    public Dictionary<string, object>? Options { get; set; }

    [JsonPropertyName("preprocess")]
    public Dictionary<string, object>? Preprocess { get; set; }

    /// <summary>文献名称（历史记录展示）</summary>
    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }

    /// <summary>页码 1-based（历史记录展示）</summary>
    [JsonPropertyName("page_number")]
    public int? PageNumber { get; set; }
}

public class OcrTask
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("task_uuid")]
    public string TaskUuid { get; set; } = "";

    [JsonPropertyName("image_url")]
    public string ImageUrl { get; set; } = "";

    [JsonPropertyName("page_count")]
    public int PageCount { get; set; }

    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("page_number")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("cost")]
    public decimal Cost { get; set; }

    [JsonPropertyName("result_json")]
    public string? ResultJson { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("external_job_id")]
    public string? ExternalJobId { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>历史列表展示：文献名</summary>
    [JsonIgnore]
    public string DocumentDisplay =>
        string.IsNullOrWhiteSpace(DocumentName) ? "（未知文献）" : DocumentName!;

    /// <summary>历史列表展示：页码</summary>
    [JsonIgnore]
    public string PageDisplay =>
        PageNumber is > 0 ? $"第 {PageNumber} 页" : "页码未知";
}

public class OcrTaskListResult
{
    [JsonPropertyName("items")]
    public List<OcrTask> Items { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

public class OcrResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("task_id")]
    public int TaskId { get; set; }

    [JsonPropertyName("page_index")]
    public int PageIndex { get; set; }

    [JsonPropertyName("block_index")]
    public int BlockIndex { get; set; }

    [JsonPropertyName("text_content")]
    public string? TextContent { get; set; }

    [JsonPropertyName("block_label")]
    public string? BlockLabel { get; set; }

    [JsonPropertyName("bbox_json")]
    public string? BboxJson { get; set; }
}

public class OcrSearchRequest
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = "";

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; } = 20;
}

public class OcrSearchResult
{
    [JsonPropertyName("items")]
    public List<OcrResult> Items { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

public class OcrOptions
{
    public bool UseDocOrientationClassify { get; set; } = false;
    public bool UseDocUnwarping { get; set; }
    public bool UseChartRecognition { get; set; }
}

// ==================== Version ====================

public class VersionCheckRequest
{
    [JsonPropertyName("current_version")]
    public string CurrentVersion { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "win";
}

public class VersionCheckResult
{
    [JsonPropertyName("has_update")]
    public bool HasUpdate { get; set; }

    [JsonPropertyName("current_version")]
    public string CurrentVersion { get; set; } = "";

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }

    [JsonPropertyName("force_update")]
    public bool ForceUpdate { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("file_md5")]
    public string? FileMd5 { get; set; }

    [JsonPropertyName("file_size")]
    public long? FileSize { get; set; }

    [JsonPropertyName("changelog")]
    public string? Changelog { get; set; }

    [JsonPropertyName("min_app_version")]
    public string? MinAppVersion { get; set; }
}

// ==================== Paginated responses ====================

public class PaginatedResponse<T>
{
    [JsonPropertyName("items")]
    public List<T> Items { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

// ==================== Common ====================

public class ApiError
{
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("code")]
    public int Code { get; set; }
}

public class MessageResponse
{
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";
}

// ==================== Batch / Queue Pool ====================

public class BatchSubmitRequest
{
    [JsonPropertyName("file_keys")]
    public List<string> FileKeys { get; set; } = new();

    [JsonPropertyName("page_numbers")]
    public List<int>? PageNumbers { get; set; }

    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("options")]
    public Dictionary<string, object>? Options { get; set; }
}

public class BatchSubmitResponse
{
    [JsonPropertyName("batch_uuid")]
    public string BatchUuid { get; set; } = "";

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("cost_total")]
    public double CostTotal { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class BatchPageResult
{
    [JsonPropertyName("page_index")]
    public int PageIndex { get; set; }

    [JsonPropertyName("page_number")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("task_uuid")]
    public string TaskUuid { get; set; } = "";

    [JsonPropertyName("result_json")]
    public string? ResultJson { get; set; }
}

public class BatchFailedPage
{
    [JsonPropertyName("page_index")]
    public int PageIndex { get; set; }

    [JsonPropertyName("page_number")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("task_uuid")]
    public string TaskUuid { get; set; } = "";

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }
}

public class BatchStatusResponse
{
    [JsonPropertyName("batch_uuid")]
    public string BatchUuid { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("queued_pages")]
    public int QueuedPages { get; set; }

    [JsonPropertyName("processing_pages")]
    public int ProcessingPages { get; set; }

    [JsonPropertyName("completed_pages")]
    public int CompletedPages { get; set; }

    [JsonPropertyName("failed_pages")]
    public int FailedPages { get; set; }

    [JsonPropertyName("cancelled_pages")]
    public int CancelledPages { get; set; }

    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("cost_total")]
    public double CostTotal { get; set; }

    [JsonPropertyName("newly_completed")]
    public List<BatchPageResult> NewlyCompleted { get; set; } = new();

    [JsonPropertyName("newly_failed")]
    public List<BatchFailedPage> NewlyFailed { get; set; } = new();

    [JsonPropertyName("results_truncated")]
    public bool ResultsTruncated { get; set; }

    [JsonPropertyName("results_omitted")]
    public int ResultsOmitted { get; set; }
}

public class BatchResumeItem
{
    [JsonPropertyName("batch_uuid")]
    public string BatchUuid { get; set; } = "";

    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("received_count")]
    public int ReceivedCount { get; set; }

    [JsonPropertyName("completed_pages")]
    public int CompletedPages { get; set; }

    [JsonPropertyName("failed_pages")]
    public int FailedPages { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}

public class BatchResumeResponse
{
    [JsonPropertyName("batches")]
    public List<BatchResumeItem> Batches { get; set; } = new();
}

public class BatchListItem
{
    [JsonPropertyName("batch_uuid")]
    public string BatchUuid { get; set; } = "";

    [JsonPropertyName("document_name")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("completed_pages")]
    public int CompletedPages { get; set; }

    [JsonPropertyName("failed_pages")]
    public int FailedPages { get; set; }

    [JsonPropertyName("cancelled_pages")]
    public int CancelledPages { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("cost_total")]
    public double CostTotal { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public string? CompletedAt { get; set; }
}

public class BatchListResponse
{
    [JsonPropertyName("batches")]
    public List<BatchListItem> Batches { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public class BatchCancelResponse
{
    [JsonPropertyName("batch_uuid")]
    public string BatchUuid { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("cancelled_pages")]
    public int CancelledPages { get; set; }

    [JsonPropertyName("completed_pages")]
    public int CompletedPages { get; set; }

    [JsonPropertyName("failed_pages")]
    public int FailedPages { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>批量上传进度（边准备边上传时 Prepared/Uploaded 分别推进）。</summary>
public class BatchUploadProgress
{
    /// <summary>兼容旧调用：通常等于已上传成功数。</summary>
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    /// <summary>本地渲染/压缩完成数。</summary>
    public int PreparedPages { get; set; }
    /// <summary>已上传成功数（供 TaskSync 使用）。</summary>
    public int UploadedPages { get; set; }
    public string Status { get; set; } = "";
    public bool IsFinished { get; set; }
    public bool IsError { get; set; }
}

// ==================== 自动句读 ====================

public class PunctuateBlockRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class PunctuatePageRequest
{
    [JsonPropertyName("blocks")]
    public List<PunctuateBlockRequest> Blocks { get; set; } = new();

    [JsonPropertyName("keep_traditional")]
    public bool? KeepTraditional { get; set; }
}

public class PunctuateBlockResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class PunctuatePageResponse
{
    [JsonPropertyName("blocks")]
    public List<PunctuateBlockResponse> Blocks { get; set; } = new();
}
