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

    /// <summary>PID 死 + 端口沉默持续该时长才判"停止"（覆盖 dshmarket 重启
    /// 的"杀进程→等端口释放→拉起新进程"窗口期，该窗口实测最长可达 30s+）。</summary>
    private static readonly TimeSpan SuspectGrace = TimeSpan.FromSeconds(15);

    /// <summary>判停止后仍保留"重生观察"窗口：期间端口复活（市场重启晚于宽限）
    /// 立即按幽灵重新转正。</summary>
    private static readonly TimeSpan ZombieWindow = TimeSpan.FromMinutes(5);

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

    /// <summary>疑似停止中的实例（PID 死 + 端口沉默但未过宽限期）：id → 首次疑似时间。</summary>
    private readonly Dictionary<string, DateTimeOffset> _suspectSince = new(StringComparer.Ordinal);

    /// <summary>
    /// 已判停止但仍在重生观察窗口内的实例（保留 DSH_HOME/端口以识别复活）。
    /// market 重启的 helper 可能晚于宽限期才拉起新进程——窗口期内必须接住。
    /// </summary>
    private readonly Dictionary<string, ZombieRecord> _zombies = new(StringComparer.Ordinal);

    private sealed record ZombieRecord(WatchdogInstanceDto Instance, DateTimeOffset Since);

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

        lock (_zombies)
        {
            _suspectSince.Remove(instanceId);
            _zombies.Remove(instanceId);
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
    /// 逐实例核对：
    ///  - PID 活：正常；
    ///  - PID 死 + 端口活：幽灵/被替换 → 转正（新 PID 入账、销毁旧残留）；
    ///  - PID 死 + 端口沉默：进入宽限期（15s，覆盖市场重启的"杀-等-起"窗口）
    ///    ——宽限内端口复活走转正；超时仍沉默才判停止（移入重生观察窗口）；
    ///  - 重生观察窗口内端口复活：立即按幽灵重新转正（市场重启晚于宽限的极端情况）。
    /// </summary>
    private void DetectAndReconcileAll()
    {
        foreach (var instance in Snapshot())
        {
            var registered = instance;
            if (ProcessQuery.IsAlive(registered.ProcessId))
            {
                ForgetSuspect(registered.InstanceId);
                continue;
            }

            var listenerPid = registered.Port > 0
                ? ProcessQuery.FindPidByListeningPort(registered.Port)
                : 0;
            if (listenerPid > 0 && listenerPid != registered.ProcessId)
            {
                // 端口仍有服务 —— 幽灵：市场自重启把主进程换人了。
                AdoptGhost(registered, listenerPid);
                continue;
            }

            // PID 死 + 端口沉默：先宽限，避免把市场重启的等待窗口误判成停止。
            if (TryEnterSuspect(registered.InstanceId))
            {
                continue;
            }

            // 宽限期已过：确认为停止。移除台账，移入重生观察窗口。
            RemoveAndZombie(registered);
        }

        ReviveZombies();
        DetectUnregisteredOrphans();
    }

    /// <summary>首次疑似：记时间并放行；宽限期内重复探测返回 true（继续等待）。</summary>
    /// <summary>
    /// 幽灵转正：端口有服务的替换进程（新 PID）替换登记 PID。
    /// 校验替换进程命令行含实例 DSH_HOME，防误收无关监听；成功后销毁旧残留。
    /// </summary>
    private void AdoptGhost(WatchdogInstanceDto registered, int listenerPid)
    {
        var replacement = ProcessQuery.GetSnapshot()
            .FirstOrDefault(process => process.ProcessId == listenerPid);
        if (replacement is null
            || !replacement.CommandLineUpper.Contains(registered.DshHome.ToUpperInvariant(), StringComparison.Ordinal))
        {
            _log.Warn(
                $"instance {registered.InstanceId}: port {registered.Port} served by pid {listenerPid} " +
                "but its command line does not reference the instance DSH_HOME; NOT adopting (possible unrelated listener)");
            return;
        }

        var oldPid = registered.ProcessId;
        var adopted = new WatchdogInstanceDto
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
            var index = _state.Instances.FindIndex(item => item.InstanceId == adopted.InstanceId);
            if (index >= 0)
            {
                _state.Instances[index] = adopted;
            }
            else
            {
                _state.Instances.Add(adopted);
            }

            _store.Save(_state);
        }

        lock (_zombies)
        {
            _suspectSince.Remove(adopted.InstanceId);
            _zombies.Remove(adopted.InstanceId);
        }

        // 销毁旧的：杀旧 PID 进程树 + DSH_HOME 残留（桌宠/helper/包装链），
        // 保留转正后的新主进程及其子树。
        var keepPids = new HashSet<int>(
            ProcessQuery.GetDescendants(listenerPid).Select(item => item.ProcessId))
        {
            listenerPid
        };
        var cleaned = CleanupDshHomeProcesses(adopted.DshHome, keepPids);
        _log.Warn(
            $"instance {adopted.InstanceId} GHOST adopted: old pid {oldPid} -> new pid {listenerPid} " +
            $"(market self-restart), cleaned {cleaned} stale process(es)");
        PublishEvent(WatchdogProtocol.EventGhostAdopted, instance: adopted);
    }

    /// <summary>首次疑似：记时间并放行；宽限期内重复探测返回 true（继续等待）。</summary>
    private bool TryEnterSuspect(string instanceId)
    {
        lock (_zombies)
        {
            if (_suspectSince.TryGetValue(instanceId, out var since))
            {
                return DateTimeOffset.UtcNow - since < SuspectGrace;
            }

            _suspectSince[instanceId] = DateTimeOffset.UtcNow;
            _log.Info($"instance {instanceId}: process dead and port silent, waiting {SuspectGrace.TotalSeconds:0}s grace period (market self-restart window)");
            return true;
        }
    }

    private void ForgetSuspect(string instanceId)
    {
        lock (_zombies)
        {
            _suspectSince.Remove(instanceId);
        }
    }

    /// <summary>宽限期后确认停止：移出台账 → 记入重生观察窗口（保留 DSH_HOME/端口）。</summary>
    private void RemoveAndZombie(WatchdogInstanceDto instance)
    {
        lock (_zombies)
        {
            _suspectSince.Remove(instance.InstanceId);
        }

        lock (_gate)
        {
            _state.Instances.RemoveAll(item => item.InstanceId == instance.InstanceId);
            _store.Save(_state);
        }

        lock (_zombies)
        {
            _zombies[instance.InstanceId] = new ZombieRecord(Clone(instance), DateTimeOffset.UtcNow);
        }

        _log.Info($"instance {instance.InstanceId} stopped (pid={instance.ProcessId}, port {instance.Port} released)");
        PublishEvent(WatchdogProtocol.EventInstanceStopped, instanceId: instance.InstanceId);
    }

    /// <summary>
    /// 重生检测：停判后 ZombieWindow 内，端口复活且新 PID 命令行含该实例 DSH_HOME
    /// → 按幽灵重新转正（市场 helper 晚于宽限期拉起的极端情况也被接住）。
    /// </summary>
    private void ReviveZombies()
    {
        ZombieRecord[] candidates;
        lock (_zombies)
        {
            SweepZombies();
            candidates = _zombies.Values.ToArray();
        }

        foreach (var zombie in candidates)
        {
            var listenerPid = zombie.Instance.Port > 0
                ? ProcessQuery.FindPidByListeningPort(zombie.Instance.Port)
                : 0;
            if (listenerPid <= 0 || !ProcessQuery.IsAlive(listenerPid))
            {
                continue;
            }

            var replacement = ProcessQuery.GetSnapshot()
                .FirstOrDefault(process => process.ProcessId == listenerPid);
            if (replacement is null
                || !replacement.CommandLineUpper.Contains(zombie.Instance.DshHome.ToUpperInvariant(), StringComparison.Ordinal))
            {
                continue;
            }

            var revived = new WatchdogInstanceDto
            {
                InstanceId = zombie.Instance.InstanceId,
                Name = zombie.Instance.Name,
                DshHome = zombie.Instance.DshHome,
                RootPath = zombie.Instance.RootPath,
                ProcessId = listenerPid,
                Port = zombie.Instance.Port,
                WebUrl = zombie.Instance.WebUrl,
                AuthenticatedWebUrl = TryExtractMarketTokenUrl(zombie.Instance.Port)
                    ?? zombie.Instance.AuthenticatedWebUrl,
                Managed = zombie.Instance.Managed,
                LastTransition = $"zombie revived: pid {zombie.Instance.ProcessId} -> {listenerPid}"
            };

            lock (_gate)
            {
                _state.Instances.RemoveAll(item => item.InstanceId == revived.InstanceId);
                _state.Instances.Add(revived);
                _store.Save(_state);
            }

            lock (_zombies)
            {
                _zombies.Remove(zombie.Instance.InstanceId);
            }

            var keepPids = new HashSet<int>(
                ProcessQuery.GetDescendants(listenerPid).Select(item => item.ProcessId))
            {
                listenerPid
            };
            var cleaned = CleanupDshHomeProcesses(revived.DshHome, keepPids);
            _log.Warn(
                $"instance {revived.InstanceId} ZOMBIE revived: stale pid {zombie.Instance.ProcessId} -> new pid {listenerPid}, cleaned {cleaned} stale process(es)");
            PublishEvent(WatchdogProtocol.EventGhostAdopted, instance: revived);
        }
    }

    /// <summary>清理过期重生记录（超出 ZombieWindow 的才真忘掉；防内存无限增长）。</summary>
    private void SweepZombies()
    {
        var cutoff = DateTimeOffset.UtcNow - ZombieWindow;
        foreach (var entry in _zombies.Where(pair => pair.Value.Since < cutoff).ToArray())
        {
            _zombies.Remove(entry.Key);
            _log.Info($"instance {entry.Key}: zombie observation window expired, forgotten");
        }
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
            lock (_zombies)
            {
                if (_zombies.TryGetValue(instanceId, out var zombie))
                {
                    instance = zombie.Instance;
                }
            }
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

        // 重生观察窗口内的"已停判"实例同样是真实例：收尾一并杀掉。
        lock (_zombies)
        {
            SweepZombies();
            instances = instances
                .Concat(_zombies.Values.Where(z => z.Instance.Managed).Select(z => z.Instance))
                .GroupBy(item => item.InstanceId)
                .Select(group => group.First())
                .ToArray();
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

        lock (_zombies)
        {
            _suspectSince.Clear();
            _zombies.Clear();
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
