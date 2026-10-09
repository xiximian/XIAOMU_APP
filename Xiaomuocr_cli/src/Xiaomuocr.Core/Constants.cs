using System.Reflection;

namespace Xiaomuocr.Core;

public static class Constants
{
    /// <summary>从 .csproj &lt;Version&gt; 属性自动读取，无需手动修改。</summary>
    public static readonly string AppVersion =
        typeof(Constants).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "1.0.0";

    /// <summary>后端 API 版本前缀，前后端字段不兼容时递增。</summary>
    public const string ApiVersion = "v1";

    public static class Endpoints
    {
        public const string Health = "/health";
        public const string ApiPrefix = "/api/v1";

        // Auth
        public const string Register = $"{ApiPrefix}/auth/register";
        public const string Login = $"{ApiPrefix}/auth/login";
        public const string Refresh = $"{ApiPrefix}/auth/refresh";
        public const string Me = $"{ApiPrefix}/auth/me";
        public const string ChangePassword = $"{ApiPrefix}/auth/change-password";
        public const string SendSmsCode = $"{ApiPrefix}/auth/send-sms-code";
        public const string ResetPassword = $"{ApiPrefix}/auth/reset-password";
        public const string WxQrStart = $"{ApiPrefix}/auth/wx/qrcode/start";
        public static string WxQrPoll(string sessionId) => $"{ApiPrefix}/auth/wx/qrcode/poll/{sessionId}";
        public const string UpdateProfile = $"{ApiPrefix}/auth/profile";

        // Feedback
        public const string FeedbackAnnouncements = $"{ApiPrefix}/feedback/announcements";
        public const string FeedbackMessages = $"{ApiPrefix}/feedback/messages";
        public static string FeedbackMessageLike(int id) => $"{ApiPrefix}/feedback/messages/{id}/like";
        public static string FeedbackMessageReplies(int id) => $"{ApiPrefix}/feedback/messages/{id}/replies";

        // Balance
        public const string Balance = $"{ApiPrefix}/balance";
        public const string BalanceRecords = $"{ApiPrefix}/balance/records";
        public const string Recharge = $"{ApiPrefix}/balance/recharge";

        // Payment
        public const string PaymentSync = $"{ApiPrefix}/payment/sync";

        // Pricing
        public const string PricingConfig = $"{ApiPrefix}/pricing/config";
        public const string PricingAssets = $"{ApiPrefix}/pricing/assets";
        public const string PricingPurchase = $"{ApiPrefix}/pricing/purchase";
        public const string PricingTransactions = $"{ApiPrefix}/pricing/transactions";

        // OSS
        public const string OssToken = $"{ApiPrefix}/oss/token";
        public const string OssTokensBatch = $"{ApiPrefix}/oss/tokens/batch";
        public const string OssUpload = $"{ApiPrefix}/oss/upload";

        // OCR
        public const string OcrRecognize = $"{ApiPrefix}/ocr/recognize";
        public const string OcrCapabilities = $"{ApiPrefix}/ocr/capabilities";
        public const string OcrTasks = $"{ApiPrefix}/ocr/tasks";
        public const string OcrSearch = $"{ApiPrefix}/ocr/search";

        // Version
        public const string VersionCheck = $"{ApiPrefix}/version/check";

        // Batch / Queue Pool
        public const string BatchSubmit = $"{ApiPrefix}/ocr/batch/submit";
        public static string BatchStatus(string batchUuid) => $"{ApiPrefix}/ocr/batch/{batchUuid}/status";
        public const string BatchResume = $"{ApiPrefix}/ocr/batch/resume";
        public const string BatchList = $"{ApiPrefix}/ocr/batch/list";
        public static string BatchCancel(string batchUuid) => $"{ApiPrefix}/ocr/batch/{batchUuid}/cancel";

        // Punctuate (自动句读)
        public const string PunctuatePage = $"{ApiPrefix}/punctuate/page";
    }
}
