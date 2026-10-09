using Microsoft.Data.Sqlite;

namespace Xiaomuocr.Core.Services;

public interface ITokenStorage
{
    Task SaveTokensAsync(string accessToken, string refreshToken, DateTime expiresAt);
    Task<(string? accessToken, string? refreshToken, DateTime? expiresAt)> LoadTokensAsync();
    Task ClearTokensAsync();
    Task SaveUsernameAsync(string username);
    Task<string?> LoadUsernameAsync();
    Task SaveRememberedLoginAsync(string loginId);
    Task<string?> LoadRememberedLoginAsync();
}

public class TokenStorage : ITokenStorage, IDisposable
{
    private readonly SqliteConnection _db;

    public TokenStorage(string dbPath)
    {
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS auth_tokens (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                access_token TEXT NOT NULL,
                refresh_token TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                cached_username TEXT,
                remembered_login_id TEXT
            )";
        cmd.ExecuteNonQuery();

        // 兼容旧数据库：如果缺少 remembered_login_id 列，自动添加
        try
        {
            using var alterCmd = _db.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE auth_tokens ADD COLUMN remembered_login_id TEXT";
            alterCmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* 列已存在，忽略 */ }
    }

    public async Task SaveTokensAsync(string accessToken, string refreshToken, DateTime expiresAt)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO auth_tokens (id, access_token, refresh_token, expires_at)
            VALUES (1, @access, @refresh, @expires)";
        cmd.Parameters.AddWithValue("@access", accessToken);
        cmd.Parameters.AddWithValue("@refresh", refreshToken);
        cmd.Parameters.AddWithValue("@expires", expiresAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(string? accessToken, string? refreshToken, DateTime? expiresAt)> LoadTokensAsync()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT access_token, refresh_token, expires_at FROM auth_tokens WHERE id = 1";
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var access = reader.GetString(0);
            var refresh = reader.GetString(1);
            var expiresStr = reader.GetString(2);
            DateTime? expires = DateTime.TryParse(expiresStr, out var dt) ? dt : null;
            return (access, refresh, expires);
        }
        return (null, null, null);
    }

    public async Task ClearTokensAsync()
    {
        // 只清除 token，保留 remembered_login_id（记住账号功能）
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"UPDATE auth_tokens SET
            access_token = '',
            refresh_token = '',
            expires_at = '',
            cached_username = NULL
            WHERE id = 1";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveUsernameAsync(string username)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE auth_tokens SET cached_username = @u WHERE id = 1";
        cmd.Parameters.AddWithValue("@u", username);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> LoadUsernameAsync()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT cached_username FROM auth_tokens WHERE id = 1";
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task SaveRememberedLoginAsync(string loginId)
    {
        // 确保行存在
        using var ensure = _db.CreateCommand();
        ensure.CommandText = "INSERT OR IGNORE INTO auth_tokens (id, access_token, refresh_token, expires_at) VALUES (1, '', '', '')";
        await ensure.ExecuteNonQueryAsync();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE auth_tokens SET remembered_login_id = @l WHERE id = 1";
        cmd.Parameters.AddWithValue("@l", loginId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> LoadRememberedLoginAsync()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT remembered_login_id FROM auth_tokens WHERE id = 1";
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public void Dispose() => _db?.Dispose();
}
