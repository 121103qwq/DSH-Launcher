namespace DshLauncher.Services;

public sealed record HttpHealthObservation(
    bool Alive,
    int ConsecutiveFailures,
    bool Degraded);

/// <summary>
/// 就绪后的 HTTP 持续健康监控（单实例）：
///  - 任何 HTTP 响应（含 401/403，dsh 页面需要 launch token）都算"服务活着"；
///  - 只有连接失败/超时才计一次 miss；连续 <see cref="FailureThreshold"/> 次才报"降级"；
///  - 探针自身异常按 miss 计但不抛；一次成功即清零。
/// </summary>
public sealed class HttpHealthMonitor
{
    public const int FailureThreshold = 3;

    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.Ordinal);

    public HttpHealthMonitor(Func<string, CancellationToken, Task<bool>> probe)
    {
        _probe = probe;
    }

    /// <summary>探测一次并更新该实例的连续失败计数。</summary>
    public async Task<HttpHealthObservation> ObserveAsync(
        string instanceId,
        string url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(url))
        {
            return new HttpHealthObservation(true, 0, false);
        }

        var alive = false;
        try
        {
            alive = await _probe(url, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            alive = false;
        }

        lock (_gate)
        {
            if (alive)
            {
                _consecutiveFailures.Remove(instanceId);
                return new HttpHealthObservation(true, 0, false);
            }

            var failures = _consecutiveFailures.TryGetValue(instanceId, out var current) ? current + 1 : 1;
            _consecutiveFailures[instanceId] = failures;
            return new HttpHealthObservation(false, failures, failures >= FailureThreshold);
        }
    }

    public int GetConsecutiveFailures(string instanceId)
    {
        lock (_gate)
        {
            return _consecutiveFailures.TryGetValue(instanceId, out var failures) ? failures : 0;
        }
    }

    public void Forget(string instanceId)
    {
        lock (_gate)
        {
            _consecutiveFailures.Remove(instanceId);
        }
    }
}
