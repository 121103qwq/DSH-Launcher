using System.Text.RegularExpressions;

namespace DshLauncher.Watchdog;

/// <summary>
/// Watchdog 核心：
///  1. 台账管理（register/unregister/snapshot/落盘）；
///  2. 周期探测：登记实例是否会「死而复生」——dshmarket 自重启会杀掉原进程、
///     用同端口拉起新进程（新 PID）。发现「登记的 PID 已死，但端口仍有服务」即
///     判定幽灵：反查新 PID → 校验 DSH_HOME → 销毁旧残留 → 转正新进程；
///  3. 残留清理：按 DSH_HOME 命令行匹配杀掉桌宠 electron / 市场重启 helper 等；
///  4. 收尾自灭：Launcher 退出（正常关机或崩溃）→ 停止全部关联实例 → 清理 → 退出，
///     绝不变成用户关不掉的常驻进程。
/// </summary>
public sealed class WatchdogCore
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);

    /// <summary>market 重启日志里的带 token 地址行：dsh web: http://127.0.0.1:&lt;port&gt;/?token=…</summary>
    private static readonly Regex MarketLogTokenPattern = new(
        @"dsh\s+web:\s*(https?://127\.0\.0\.1:\d+/\?token=[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly WatchdogStateStore _store;
    private readonly WatchdogLog _log;
    private readonly int _launcherPid;
    private readonly object _gate = new();
    private readonly WatchdogState _state;
    private readonly List<Action<WatchdogProtocol.Response>> _eventSinks = new();

    public WatchdogCore(string stateDirectory, int launcherPid, WatchdogLog log)
    {
        _store = new WatchdogStateStore(stateDirectory);
        _log = log;
        _launcherPid = launcherPid;
        _state = _store.Load();
        _state.LauncherPid = launcherPid;
        _state.LauncherStartedAt = DateTimeOffset.UtcNow;
        _store.Save(_state);
        _log.Info($"watchdog started (launcher pid={launcherPid}, tracking {_state.Instances.Count} instance(s))");
    }

    // ---------- 台账 API（Launcher 经管道调用） ----------

    public void RegisterInstance(WatchdogInstanceDto instance)
    {
        lock (_gate)
        {
            _state.Instances.RemoveAll(item => item.InstanceId == instance.InstanceId);
            _state.Instances.Add(instance);
            _store.Save(_state);
        }

        _log.Info($"registered instance {instance.InstanceId} pid={instance.ProcessId} port={instance.Port}");
    }

    public void UnregisterInstance(string instanceId)
    {
        lock (_gate)
        {
            _state.Instances.RemoveAll(item => item.InstanceId == instanceId);
            _store.Save(_state);
        }

        _log.Info($"unregistered instance {instanceId}");
    }

    public IReadOnlyList<WatchdogInstanceDto> Snapshot()
    {
        lock (_gate)
        {
            return _state.Instances
                .Select(item => Clone(item))
                .ToArray();
        }
    }

    public void Subscribe(Action<WatchdogProtocol.Response> sink)
    {
        lock (_eventSinks)
        {
            _eventSinks.Add(sink);
        }
    }

    private void PublishEvent(string eventName, WatchdogInstanceDto? instance = null, string? instanceId = null)
    {
        var payload = new WatchdogProtocol.Response
        {
            T = "event",
            Event = eventName,
            Instance = instance is null ? null : Clone(instance),
            InstanceId = instanceId
        };

        Action<WatchdogProtocol.Response>[] sinks;
        lock (_eventSinks)
        {
            sinks = _eventSinks.ToArray();
        }

        foreach (var sink in sinks)
        {
            try
            {
                sink(payload);
            }
            catch
            {
                // 单个订阅者异常不影响其余。
            }
        }
    }

    // ---------- 周期探测 ----------

    public async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _log.Info("watchdog monitoring loop started");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!ProcessQuery.IsAlive(_launcherPid))
                {
                    _log.Warn($"launcher (pid={_launcherPid}) is gone; watchdog performing final cleanup and exiting");
                    StopAllAndCleanup();
                    return;
                }

                DetectAndReconcileAll();
            }
            catch (Exception ex)
            {
                _log.Error($"probe iteration failed: {ex}");
            }

            try
            {
                await Task.Delay(ProbeInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 逐实例核对：PID 死 + 端口活 = 幽灵 → 转正；PID 死 + 端口死 = 停止 → 出账。
    /// </summary>
    private void DetectAndReconcileAll()
    {
        foreach (var instance in Snapshot())
        {
            var registered = instance;
            if (ProcessQuery.IsAlive(registered.ProcessId))
            {
                continue;
            }

            var listenerPid = registered.Port > 0
                ? ProcessQuery.FindPidByListeningPort(registered.Port)
                : 0;
            if (listenerPid <= 0 || listenerPid == registered.ProcessId)
            {
                // 端口也不在服务：实例确实停了。
                lock (_gate)
                {
                    _state.Instances.RemoveAll(item => item.InstanceId == registered.InstanceId);
                    _store.Save(_state);
                }

                _log.Info($"instance {registered.InstanceId} stopped (pid={registered.ProcessId}, port {registered.Port} released)");
                PublishEvent(WatchdogProtocol.EventInstanceStopped, instanceId: registered.InstanceId);
                continue;
            }

            // 端口仍有服务 —— 幽灵：市场自重启把主进程换人了。
            var replacement = ProcessQuery.GetSnapshot()
                .FirstOrDefault(process => process.ProcessId == listenerPid);
            if (replacement is null
                || !replacement.CommandLineUpper.Contains(registered.DshHome.ToUpperInvariant(), StringComparison.Ordinal))
            {
                _log.Warn(
                    $"instance {registered.InstanceId}: port {registered.Port} served by pid {listenerPid} " +
                    "but its command line does not reference the instance DSH_HOME; NOT adopting (possible unrelated listener)");
                continue;
            }

            var oldPid = registered.ProcessId;
            registered = new WatchdogInstanceDto
            {
                InstanceId = registered.InstanceId,
                Name = registered.Name,
                DshHome = registered.DshHome,
                RootPath = registered.RootPath,
                ProcessId = listenerPid,
                Port = registered.Port,
                WebUrl = registered.WebUrl,
                AuthenticatedWebUrl = TryExtractMarketTokenUrl(registered.Port)
                    ?? registered.AuthenticatedWebUrl,
                Managed = registered.Managed,
                LastTransition = $"ghost adopted: pid {oldPid} -> {listenerPid}"
            };

            lock (_gate)
            {
                var index = _state.Instances.FindIndex(item => item.InstanceId == registered.InstanceId);
                if (index >= 0)
                {
                    _state.Instances[index] = registered;
                }
                else
                {
                    _state.Instances.Add(registered);
                }

                _store.Save(_state);
            }

            // 销毁旧的：杀旧 PID 进程树 + DSH_HOME 残留（桌宠/helper/包装链），
            // 保留转正后的新主进程及其子树。
            var keepPids = new HashSet<int>(
                ProcessQuery.GetDescendants(listenerPid).Select(item => item.ProcessId))
            {
                listenerPid
            };
            var cleaned = CleanupDshHomeProcesses(registered.DshHome, keepPids);
            _log.Warn(
                $"instance {registered.InstanceId} GHOST adopted: old pid {oldPid} -> new pid {listenerPid} " +
                $"(market self-restart), cleaned {cleaned} stale process(es)");
            PublishEvent(WatchdogProtocol.EventGhostAdopted, instance: registered);
        }

        DetectUnregisteredOrphans();
    }

    /// <summary>
    /// 兜底：扫描「命令行包含任一登记 DSH_HOME 且像 dsh web 主进程」但不在台账中的进程
    /// （例如 Launcher 登记前实例已被市场重启、或 Launcher 崩溃瞬间的窗口期），记为孤儿
    /// 事件（不自动转正——需要 Launcher 决定归属；但会留档便于人工清理）。
    /// </summary>
    private void DetectUnregisteredOrphans()
    {
        IReadOnlyList<WatchdogInstanceDto> known;
        lock (_gate)
        {
            known = _state.Instances.ToArray();
        }

        if (known.Count == 0)
        {
            return;
        }

        var snapshot = ProcessQuery.GetSnapshot();
        var registeredPids = known.Select(item => item.ProcessId).ToHashSet();
        foreach (var process in snapshot)
        {
            if (registeredPids.Contains(process.ProcessId)
                || !process.Name.Equals("node", StringComparison.OrdinalIgnoreCase)
                || !process.CommandLineUpper.Contains("BIN.JS", StringComparison.Ordinal))
            {
                continue;
            }

            var home = known.FirstOrDefault(
                item => process.CommandLineUpper.Contains(item.DshHome.ToUpperInvariant(), StringComparison.Ordinal));
            if (home is null)
            {
                continue;
            }

            _log.Warn(
                $"orphan dsh web process pid={process.ProcessId} detected for instance {home.InstanceId} " +
                "while not registered — reported to launcher");
            PublishEvent(WatchdogProtocol.EventGhostOrphan, instance: Clone(home));
        }
    }

    // ---------- 清理 / 收尾 ----------

    /// <summary>
    /// 清理一个实例的残留（保持 keepPids 存活）：
    /// - 登记 PID 的进程树；
    /// - 命令行含该实例 DSH_HOME 的全部进程（桌宠 electron、市场重启包装链等）。
    /// 返回实际发起的清理个数。
    /// </summary>
    public int CleanupInstance(string instanceId, int? keepPid = null)
    {
        WatchdogInstanceDto? instance;
        lock (_gate)
        {
            instance = _state.Instances.FirstOrDefault(item => item.InstanceId == instanceId);
        }

        if (instance is null)
        {
            return 0;
        }

        var keepPids = new HashSet<int>();
        if (keepPid is > 0)
        {
            keepPids.Add(keepPid.Value);
            foreach (var descendant in ProcessQuery.GetDescendants(keepPid.Value))
            {
                keepPids.Add(descendant.ProcessId);
            }
        }

        var cleaned = CleanupDshHomeProcesses(instance.DshHome, keepPids);
        if (cleaned > 0)
        {
            _log.Info($"cleanup for instance {instanceId}: killed {cleaned} process(es)");
        }

        return cleaned;
    }

    /// <summary>
    /// 收尾：按用户决策「Launcher 完全关闭 → 实例强制关闭」，停止台账里全部实例
    /// （Managed 标记的；孤儿不加）并清掉 DSH_HOME 残留，然后清空台账。
    /// </summary>
    public void StopAllAndCleanup()
    {
        IReadOnlyList<WatchdogInstanceDto> instances;
        lock (_gate)
        {
            instances = _state.Instances.Where(item => item.Managed).ToArray();
        }

        foreach (var instance in instances)
        {
            try
            {
                if (ProcessQuery.IsAlive(instance.ProcessId))
                {
                    ProcessQuery.KillTree(instance.ProcessId);
                }

                if (instance.Port > 0)
                {
                    var listenerPid = ProcessQuery.FindPidByListeningPort(instance.Port);
                    if (listenerPid > 0 && listenerPid != instance.ProcessId
                        && ProcessQuery.IsAlive(listenerPid))
                    {
                        // 转正后的进程：台账 PID 可能滞后，按端口补刀。
                        ProcessQuery.KillTree(listenerPid);
                    }
                }

                var cleaned = CleanupDshHomeProcesses(instance.DshHome, new HashSet<int>());
                _log.Info($"final cleanup for instance {instance.InstanceId}: tree killed, {cleaned} stale process(es) removed");
            }
            catch (Exception ex)
            {
                _log.Error($"final cleanup failed for instance {instance.InstanceId}: {ex}");
            }
        }

        lock (_gate)
        {
            _state.Instances.Clear();
            _store.Clear();
        }

        _log.Info("watchdog final cleanup complete; exiting");
    }

    /// <summary>
    /// 按 DSH_HOME 匹配杀进程（除 keepPids 及其子树外全部）。返回处理数目。
    /// 顺序：先杀包装/残留（桌宠 electron、powershell、cmd），最后才轮到主进程类——但
    /// 这里不区分，逐个 KillTree 幂等执行；taskkill /T 会自动带出整树，重复无害。
    /// </summary>
    private int CleanupDshHomeProcesses(string dshHome, IReadOnlyCollection<int> keepPids)
    {
        var keep = new HashSet<int>(keepPids);
        foreach (var descender in keepPids.Select(pid => ProcessQuery.GetDescendants(pid)))
        {
            foreach (var process in descender)
            {
                keep.Add(process.ProcessId);
            }
        }

        var candidates = ProcessQuery.FindProcessesForDshHome(
            string.Empty, dshHome, keepAlivePids: keep, excludePids: null);
        var killed = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                || candidate.Name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                || candidate.Name.Equals("electron", StringComparison.OrdinalIgnoreCase)
                || candidate.Name.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                if (ProcessQuery.IsAlive(candidate.ProcessId) && ProcessQuery.KillTree(candidate.ProcessId))
                {
                    killed++;
                }
            }
        }

        return killed;
    }

    // ---------- 工具 ----------

    /// <summary>
    /// 从 %TEMP%/dsh-market-restart-*.out.log 抓取指定端口最新的带 token 地址
    /// （market 自重启的 stdout 落盘；转正后 Launcher 需要它才能开窗）。
    /// </summary>
    private static string? TryExtractMarketTokenUrl(int port)
    {
        try
        {
            var tempDirectory = Path.GetTempPath();
            var files = Directory.GetFiles(tempDirectory, "dsh-market-restart-*.out.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(3);
            foreach (var file in files)
            {
                try
                {
                    var text = File.ReadAllText(file, System.Text.Encoding.UTF8);
                    foreach (Match match in MarketLogTokenPattern.Matches(text))
                    {
                        if (Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri)
                            && uri.Port == port)
                        {
                            return uri.ToString();
                        }
                    }
                }
                catch
                {
                    // 单个日志不可读就试下一个。
                }
            }
        }
        catch
        {
            // %TEMP% 不可访问时静默。
        }

        return null;
    }

    private static WatchdogInstanceDto Clone(WatchdogInstanceDto source) => new()
    {
        InstanceId = source.InstanceId,
        Name = source.Name,
        DshHome = source.DshHome,
        RootPath = source.RootPath,
        ProcessId = source.ProcessId,
        Port = source.Port,
        WebUrl = source.WebUrl,
        AuthenticatedWebUrl = source.AuthenticatedWebUrl,
        Managed = source.Managed
    };
}
