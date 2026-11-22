namespace Xiaomuocr.Core.Services;

/// <summary>网络连接状态检测服务。</summary>
public interface IConnectivityService
{
    /// <summary>当前是否处于离线模式。</summary>
    bool IsOffline { get; }

    /// <summary>连接状态变化事件（true=离线，false=在线）。</summary>
    event Action<bool>? ConnectivityChanged;

    /// <summary>尝试连接服务器恢复在线模式。返回 true 表示恢复成功。</summary>
    Task<bool> TryGoOnlineAsync();

    /// <summary>主动进入离线模式。</summary>
    void SetOffline();

    /// <summary>后台周期性连接检测（每 30 秒）。</summary>
    void StartBackgroundCheck(Func<Task<bool>> checkFunc);
}

public class ConnectivityService : IConnectivityService, IDisposable
{
    private bool _isOffline;
    private CancellationTokenSource? _bgCts;
    private Func<Task<bool>>? _checkFunc;

    public bool IsOffline => _isOffline;
    public event Action<bool>? ConnectivityChanged;

    /// <summary>
    /// 尝试连接服务器。成功 → 切换到在线；失败 → 保持离线。
    /// </summary>
    public async Task<bool> TryGoOnlineAsync()
    {
        if (_checkFunc == null) return !_isOffline;
        try
        {
            var ok = await _checkFunc();
            if (ok && _isOffline)
            {
                _isOffline = false;
                ConnectivityChanged?.Invoke(false);
            }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>主动进入离线模式（在线 → 离线时触发事件）。</summary>
    public void SetOffline()
    {
        if (!_isOffline)
        {
            _isOffline = true;
            ConnectivityChanged?.Invoke(true);
        }
    }

    /// <summary>
    /// 启动后台周期性连接检测（每 30 秒）。
    /// checkFunc 返回 true 表示服务器可达。
    /// </summary>
    public void StartBackgroundCheck(Func<Task<bool>> checkFunc)
    {
        _checkFunc = checkFunc;
        _bgCts?.Cancel();
        _bgCts = new CancellationTokenSource();
        _ = BackgroundLoopAsync(_bgCts.Token);
    }

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30_000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;

            // 只有离线时才尝试恢复
            if (_isOffline)
            {
                try { await TryGoOnlineAsync(); }
                catch { /* ignore background check errors */ }
            }
        }
    }

    public void Dispose()
    {
        _bgCts?.Cancel();
        _bgCts?.Dispose();
    }
}
