using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Xiaomuocr.Core.Services;

public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<T?> GetAsync<T>(string key) where T : class;
    Task SetAsync<T>(string key, T value) where T : class;
    Task<string?> GetServerUrlAsync();
    Task SetServerUrlAsync(string url);
    Task<bool> IsServerConfiguredAsync();
}

public class SettingsService : ISettingsService, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly JsonSerializerOptions _jsonOptions;

    // 与 deploy_frontend.py _OBFUSCATION_KEY 一致
    private const string ObfuscationKey = "XiaomuOCR_2026_ServerDefault";

    /// <summary>最近一次 GetServerUrlAsync 的决策路径（供诊断日志使用）。</summary>
    public string LastServerUrlDecision { get; private set; } = "";

    public SettingsService(string dbPath)
    {
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        cmd.ExecuteNonQuery();

        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    }

    public async Task<string?> GetAsync(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task SetAsync(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var json = await GetAsync(key);
        if (json == null) return null;
        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    public async Task SetAsync<T>(string key, T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, _jsonOptions);
        await SetAsync(key, json);
    }

    public async Task<string?> GetServerUrlAsync()
    {
        // 本地联调：dev_run_client.bat 设置此变量，强制走本机，不被 SQLite 里的生产地址带跑
        var forceUrl = Environment.GetEnvironmentVariable("XIAOMU_FORCE_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(forceUrl))
        {
            var forced = forceUrl.Trim().TrimEnd('/');
            LastServerUrlDecision = $"env XIAOMU_FORCE_SERVER_URL={forced} → 强制使用（忽略 SQLite/server_default）";
            return forced;
        }

        var saved = await GetAsync("server_url");
        if (!string.IsNullOrEmpty(saved))
        {
            // 如果 SQLite 中保存的是 localhost，优先使用部署工具写入的生产地址
            if (IsLocalhostUrl(saved))
            {
                var defaultUrl = TryLoadDefaultServerUrl();
                if (!string.IsNullOrEmpty(defaultUrl) && !IsLocalhostUrl(defaultUrl))
                {
                    // server_default.txt 有生产地址 → 覆盖 SQLite 中的 localhost
                    LastServerUrlDecision = $"SQLite={saved}(localhost) → server_default.txt={defaultUrl}(生产) → 覆盖";
                    await SetServerUrlAsync(defaultUrl);
                    return defaultUrl;
                }

                // server_default.txt 不存在或也是 localhost →
                // 这是部署构建（server_default.txt 存在）时不应连接 localhost，
                // 清除旧值，触发 ServerSetupView 让用户重新输入正确地址
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, "server_default.txt")))
                {
                    LastServerUrlDecision = $"SQLite={saved}(localhost) + server_default.txt存在但也是localhost → 清除,触发引导";
                    await SetAsync("server_url", "");
                    return null;
                }

                // 开发构建（无 server_default.txt）→ 保留用户选择的 localhost
                LastServerUrlDecision = $"SQLite={saved}(localhost) + 无server_default.txt(开发构建) → 保留localhost";
            }
            else
            {
                LastServerUrlDecision = $"SQLite={saved}(非localhost) → 直接使用";
            }
            return saved;
        }

        // 首次启动：尝试读取部署工具嵌入的加密默认地址
        var defaultUrl2 = TryLoadDefaultServerUrl();
        if (!string.IsNullOrEmpty(defaultUrl2))
        {
            // 如果 server_default.txt 也是 localhost，不自动保存，触发 ServerSetupView
            if (IsLocalhostUrl(defaultUrl2))
            {
                LastServerUrlDecision = $"SQLite空 + server_default.txt={defaultUrl2}(localhost) → 触发引导";
                return null;
            }

            // 自动保存到 SQLite，后续启动直接使用
            LastServerUrlDecision = $"SQLite空 + server_default.txt={defaultUrl2}(生产) → 保存并使用";
            await SetServerUrlAsync(defaultUrl2);
            return defaultUrl2;
        }

        LastServerUrlDecision = "SQLite空 + 无server_default.txt → 触发引导(开发构建)";
        return null; // 开发构建，触发 ServerSetupView
    }

    /// <summary>判断 URL 是否为 localhost 地址。</summary>
    private static bool IsLocalhostUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("127.0.0.1") || url.Contains("localhost");
    }

    /// <summary>读取部署工具写入的加密 server_default.txt（XOR + Base64）。</summary>
    private string? TryLoadDefaultServerUrl()
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var path = Path.Combine(appDir, "server_default.txt");
            if (!File.Exists(path))
                return null;

            var encoded = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(encoded))
                return null;

            // Base64 解码
            var data = Convert.FromBase64String(encoded);
            // XOR 解密
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(ObfuscationKey);
            var result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ keyBytes[i % keyBytes.Length]);

            return System.Text.Encoding.UTF8.GetString(result).TrimEnd('/');
        }
        catch
        {
            return null; // 解密失败 → 当作无默认配置
        }
    }

    public async Task SetServerUrlAsync(string url)
    {
        await SetAsync("server_url", url.TrimEnd('/'));
    }

    public async Task<bool> IsServerConfiguredAsync()
    {
        var url = await GetAsync("server_url");
        return !string.IsNullOrEmpty(url);
    }

    public void Dispose() => _db?.Dispose();
}
