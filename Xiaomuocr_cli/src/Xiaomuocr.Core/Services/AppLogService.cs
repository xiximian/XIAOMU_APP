using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Xiaomuocr.Core.Services;

/// <summary>前端应用日志服务：记录关键运行信息和错误，自动过滤敏感信息。</summary>
public interface IAppLogService
{
    /// <summary>日志文件完整路径（与 exe 同目录的 xiaomuocr_client.log）</summary>
    string LogFilePath { get; }
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Debug(string message);
    List<LogEntry> GetRecentLogs(int count = 200);
    event Action<LogEntry>? OnNewLog;
    void Clear();
}

public enum LogLevel { Debug, Info, Warn, Error }

public class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = "";
}

public class AppLogService : IAppLogService
{
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private const int MaxLogs = 2000;
    private readonly string _filePath;
    private readonly object _fileLock = new();
    public event Action<LogEntry>? OnNewLog;
    public string LogFilePath => _filePath;

    public AppLogService() : this(null) { }

    public AppLogService(string? dbPath)
    {
        // 日志文件放在应用目录下（与 Xiaomuocr.App.exe 同目录）
        var dir = string.IsNullOrEmpty(dbPath)
            ? AppDomain.CurrentDomain.BaseDirectory
            : System.IO.Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        _filePath = System.IO.Path.Combine(dir, "xiaomuocr_client.log");

        // 启动时清空旧日志
        try { System.IO.File.WriteAllText(_filePath, ""); } catch { }
    }

    // 敏感信息过滤规则
    private static readonly Regex[] SensitivePatterns =
    {
        new(@"bearer\s+[a-zA-Z0-9_-]{20,}", RegexOptions.IgnoreCase),
        new(@"token[=:]\s*[""]?[a-zA-Z0-9_-]{20,}[""]?", RegexOptions.IgnoreCase),
        new(@"password[=:]\s*[""]?[^""\s,}]+[""]?", RegexOptions.IgnoreCase),
        new(@"[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.Compiled),  // base64-like tokens
        new(@"sk-[a-zA-Z0-9]{20,}", RegexOptions.IgnoreCase),
        new(@"api[_-]?key[=:]\s*[""]?[a-zA-Z0-9_-]{16,}[""]?", RegexOptions.IgnoreCase),
        new(@"secret[=:]\s*[""]?[^""\s,}]+[""]?", RegexOptions.IgnoreCase),
        new(@"https?://[^/]*:[^@]*@", RegexOptions.IgnoreCase),  // URL with credentials
        // OSS 预签名 URL 参数（AWS S3 / 阿里云 OSS / 京东云 OSS）
        new(@"(?:X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token|AWSAccessKeyId|OSSAccessKeyId)=[A-Za-z0-9%/+_-]{20,}", RegexOptions.IgnoreCase),
        new(@"(?:x-oss-credential|x-oss-signature|x-oss-security-token)=[A-Za-z0-9%/+_-]{20,}", RegexOptions.IgnoreCase),
        new(@"[&?](?:Signature|signature|authorization)=[A-Za-z0-9%/+_-]{20,}", RegexOptions.IgnoreCase),
        new(@"[&?]Expires=\d{9,}", RegexOptions.IgnoreCase),  // OSS 预签名过期时间戳
        // 京东云 OSS 预签名 URL 中的签名参数
        new(@"[&?](?:X-Jd-Cloud-Signature|X-Jd-Cloud-Credential)=[A-Za-z0-9%/+_-]{20,}", RegexOptions.IgnoreCase),
        // 预签名 URL 中的完整 URL（含签名参数的 http/https 链接）
        new(@"https?://[^""'\s]+\?(?:[^""'\s]*[&]?Signature=[^""'\s]+|[^""'\s]*[&]?X-Amz-Signature=[^""'\s]+|[^""'\s]*[&]?X-Jd-Cloud-Signature=[^""'\s]+)", RegexOptions.IgnoreCase),
        // phone number (11-digit Chinese mobile)
        new(@"1[3-9]\d{9}", RegexOptions.Compiled),
        // verify_code / sms_code
        new(@"(?:verify_code|sms_code|code)[=:]\s*[""]?\d{4,8}[""]?", RegexOptions.IgnoreCase),
    };

    private static readonly Regex IpPortPattern = new(@"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}):(\d+)", RegexOptions.Compiled);

    public void Info(string message) => AddLog(LogLevel.Info, message);
    public void Warn(string message) => AddLog(LogLevel.Warn, message);
    public void Error(string message) => AddLog(LogLevel.Error, message);
    public void Debug(string message) => AddLog(LogLevel.Debug, message);

    private void AddLog(LogLevel level, string message)
    {
        var sanitized = Sanitize(message);
        var entry = new LogEntry { Time = DateTime.Now, Level = level, Message = sanitized };

        _logs.Enqueue(entry);
        while (_logs.Count > MaxLogs)
            _logs.TryDequeue(out _);

        OnNewLog?.Invoke(entry);

        // 写入文件
        try
        {
            lock (_fileLock)
            {
                var levelStr = level switch
                {
                    LogLevel.Error => "ERR",
                    LogLevel.Warn => "WRN",
                    LogLevel.Info => "INF",
                    _ => "DBG",
                };
                System.IO.File.AppendAllText(_filePath,
                    $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{levelStr}] {sanitized}\n");
            }
        }
        catch { }
    }

    public List<LogEntry> GetRecentLogs(int count = 200)
    {
        return _logs.Reverse().Take(count).Reverse().ToList();
    }

    public void Clear()
    {
        while (_logs.TryDequeue(out _)) { }
    }

    /// <summary>过滤敏感信息</summary>
    private static string Sanitize(string msg)
    {
        foreach (var pattern in SensitivePatterns)
        {
            msg = pattern.Replace(msg, m =>
            {
                var val = m.Value;
                if (val.Length <= 10) return "***";
                return val[..Math.Min(4, val.Length)] + "***" + val[^Math.Min(4, val.Length)..];
            });
        }
        // 替换 IP:Port 为 x.x.x.x:port
        msg = IpPortPattern.Replace(msg, "x.x.x.x:$2");
        return msg;
    }
}
