using System.IO;

namespace DshLauncher.Watchdog;

/// <summary>
/// 进程内实例监控协调器：Launcher 同一个进程里的后台循环（无子进程、无管道）。
///
/// 职责：
///  - 每 5s 驱动一次 WatchdogCore.ProbeOnce（幽灵识别/转正/停止收敛/孤儿报告）；
///  - 把核心事件转成托管事件（GhostAdopted / InstanceStopped / OrphanDetected），
///    由 UI 层（MainWindow）分发到界面线程；
///  - 生命周期与 Launcher 完全一致：Launcher 存活即监控，Launcher 退出即停止
///    （正常退出前调用 TerminateAllAndClear 清账收尾）。
///
/// 与旧独立 watchdog 进程的取舍：Launcher 崩溃（异常/强杀）时不再有跨进程收尾，
/// 由「下次启动时的台账恢复 + 孤儿检测提示」兜底（台账已落盘 watchdog-state.json）。
/// </summary>
public sealed class WatchdogRuntime : IDisposable
{
    private readonly WatchdogCore _core;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private TimeSpan _probeInterval = TimeSpan.FromSeconds(5);
    private readonly object _intervalLock = new();

    public event Action<WatchdogInstanceDto>? GhostAdopted;
    public event Action<WatchdogInstanceDto>? InstanceStopped;
    public event Action<WatchdogInstanceDto>? OrphanDetected;

    /// <summary>每轮探测后触发（资源快照已更新）：UI 层据此刷新 CPU/内存/运行时长。</summary>
    public event Action? ResourcesUpdated;

    /// <param name="probeSeconds">监控轮询间隔（秒，2–120 范围内截断，默认 5）。</param>
    public WatchdogRuntime(int probeSeconds = 5)
    {
        _probeInterval = TimeSpan.FromSeconds(Math.Clamp(probeSeconds, 2, 120));
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeek", "launcher");
        _core = new WatchdogCore(stateDirectory);
        _core.GhostAdopted += instance => GhostAdopted?.Invoke(instance);
        _core.InstanceStopped += instance => InstanceStopped?.Invoke(instance);
        _core.OrphanDetected += instance => OrphanDetected?.Invoke(instance);
    }

    /// <summary>设置页保存后即时生效（2–120 秒，越界截断）。</summary>
    public void SetProbeInterval(int probeSeconds)
    {
        lock (_intervalLock)
        {
            _probeInterval = TimeSpan.FromSeconds(Math.Clamp(probeSeconds, 2, 120));
        }
    }

    /// <summary>启动后台监控循环（幂等）。</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => LoopAsync(_cancellation.Token));
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _core.ProbeOnce();
                ResourcesUpdated?.Invoke();
            }
            catch (Exception)
            {
                // 单轮探测异常不终止循环（与 UI 隔离）。
            }

            try
            {
                await Task.Delay(GetProbeInterval(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan GetProbeInterval()
    {
        lock (_intervalLock)
        {
            return _probeInterval;
        }
    }

    // ---------- 台账操作（进程内直连） ----------

    public void Register(
        string instanceId,
        string name,
        string dshHome,
        string? rootPath,
        int processId,
        int port,
        string webUrl,
        string? authenticatedWebUrl)
    {
        _core.RegisterInstance(new WatchdogInstanceDto
        {
            InstanceId = instanceId,
            Name = name,
            DshHome = dshHome,
            RootPath = rootPath,
            ProcessId = processId,
            Port = port,
            WebUrl = webUrl,
            AuthenticatedWebUrl = authenticatedWebUrl,
            Managed = true
        });
    }

    public void Unregister(string instanceId) => _core.UnregisterInstance(instanceId);

    public int Cleanup(string instanceId, int? keepPid = null) => _core.CleanupInstance(instanceId, keepPid);

    public IReadOnlyList<WatchdogInstanceDto> Snapshot() => _core.Snapshot();

    /// <summary>实例最近一次资源快照（未运行/未登记时为 null）。</summary>
    public InstanceResourceSnapshot? GetResource(string instanceId) => _core.GetResource(instanceId);

    /// <summary>实例资源历史（折线图数据源）。</summary>
    public IReadOnlyList<InstanceResourceSnapshot> GetResourceHistory(string instanceId) =>
        _core.GetResourceHistory(instanceId);

    /// <summary>台账里登记的实例根 PID（未登记时为 0）。</summary>
    public int GetProcessId(string instanceId) =>
        _core.Snapshot().FirstOrDefault(item => item.InstanceId == instanceId)?.ProcessId ?? 0;

    /// <summary>启动时（Reconcile 前）拉台账：恢复崩溃前记录的实例（用于提示残留）。</summary>
    public IReadOnlyList<WatchdogInstanceDto> LoadLedger() => _core.Snapshot();

    /// <summary>
    /// 正常退出前的全量收尾：杀净全部受管实例相关进程（幂等）+ 清空台账。
    /// 调用时机：MainWindow.OnClosing 停止实例之后。
    /// </summary>
    public void TerminateAllAndClear() => _core.CleanupAllAndClear();

    /// <summary>停止监控循环（不清理台账——进程退出时由调用方决定）。</summary>
    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
