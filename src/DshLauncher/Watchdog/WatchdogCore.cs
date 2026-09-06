using System.IO;
using System.Text.RegularExpressions;

namespace DshLauncher.Watchdog;

/// <summary>
/// 进程内实例监控核心（与 Launcher 同进程，后台定时循环驱动）：
///  1. 台账管理（register/unregister/snapshot/落盘）；
///  2. 周期探测：登记实例是否会「死而复生」——dshmarket 自重启会杀掉原进程、
///     用同端口拉起新进程（新 PID）。发现「登记的 PID 已死，但端口仍有服务」即
///     判定幽灵：反查新 PID → 校验身份（DSH_HOME 命令行或 dsh web 特征）→
///     销毁旧残留 → 转正新进程（GhostAdopted 事件）；
///  3. 停止判定带宽限期（覆盖 market 重启「杀-等-起」窗口），宽限后转 ZOMBIE
///     观察窗，期间端口复活仍可转正（ZombieRevived 事件）；
///  4. 残留清理：按 DSH_HOME 命令行 + 端口杀桌宠 electron / 市场 helper 等；
///  5. 清账（正常退出）与崩溃恢复：台账落盘，下次启动可据此提示残留实例。
/// </summary>
public sealed class WatchdogCore
{
    /// <summary>PID 死 + 端口沉默持续该时长才判"停止"（覆盖 dshmarket 重启的
    /// "杀进程→等端口释放→拉起新进程"窗口期，该窗口实测最长可达 30s+）。</summary>
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
    private readonly object _gate = new();
    private readonly WatchdogState _state;
    private readonly Dictionary<string, DateTimeOffset> _suspectSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZombieRecord> _zombies = new(StringComparer.Ordinal);

    private sealed record ZombieRecord(WatchdogInstanceDto Instance, DateTimeOffset Since);

    /// <summary>发现非启动器进程接管了实例（market 重启转正完成）：实例 = 转正后的新台账条目。</summary>
    public event Action<WatchdogInstanceDto>? GhostAdopted;

    /// <summary>实例被判定停止（宽限期后端口仍未复活）。</summary>
    public event Action<WatchdogInstanceDto>? InstanceStopped;

    /// <summary>发现未登记进程占用实例端口（孤儿），由 UI 决定清理/接管。</summary>
    public event Action<WatchdogInstanceDto>? OrphanDetected;

    public WatchdogCore(string stateDirectory, WatchdogLog? log = null)
    {
        _store = new WatchdogStateStore(stateDirectory);
        _log = log ?? new WatchdogLog(stateDirectory);
        _state = _store.Load();
        _log.Info($"watchdog core started (tracking {_state.Instances.Count} instance(s) from ledger)");
    }

    // ---------- 台账 API ----------

    public void RegisterInstance(WatchdogInstanceDto instance)
    {
        lock (_gate)
        {
            _state.Instances.RemoveAll(item => item.InstanceId == instance.InstanceId);
            _state.Instances.Add(Clone(instance));
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
            return _state.Instances.Select(Clone).ToArray();
        }
    }

    // ---------- 周期探测（由 WatchdogRuntime 定时调用） ----------

    /// <summary>
    /// 单轮探测：逐实例核对——
    ///  - PID 活：正常；
    ///  - PID 死 + 端口活：幽灵/被替换 → 转正（GhostAdopted）；\n
    ///  - PID 死 + 端口沉默：宽限期（覆盖市场重启窗口）→ 超时判停（InstanceStopped，转 ZOMBIE）；\n
    ///  - ZOMBIE 观察窗内端口复活：重新转正；\n
    ///  - 未登记进程占用实例端口：OrphanDetected。
    /// </summary>
    public void ProbeOnce()
    {
        foreach (var instance in Snapshot())
        {
            if (ProcessQuery.IsAlive(instance.ProcessId))
            {
                ForgetSuspect(instance.InstanceId);
                continue;
            }

            var listenerPid = instance.Port > 0
                ? ProcessQuery.FindPidByListeningPort(instance.Port)
                : 0;
            if (listenerPid > 0 && listenerPid != instance.ProcessId)
            {
                AdoptGhost(instance, listenerPid);
                continue;
            }

            if (TryEnterSuspect(instance.InstanceId))
            {
                continue;
            }

            RemoveAndZombie(instance);
        }

        ReviveZombies();
        DetectOrphans();
    }

    private void AdoptGhost(WatchdogInstanceDto registered, int listenerPid)
    {
        var replacement = ProcessQuery.GetSnapshot()
            .FirstOrDefault(process => process.ProcessId == listenerPid);
        if (replacement is null || !IsReplacementFor(registered, replacement))
        {
            _log.Warn(
                $"instance {registered.InstanceId}: port {registered.Port} served by pid {listenerPid} " +
                "but it is not a dsh web process for this instance; NOT adopting (possible unrelated listener)");
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
            Managed = true,
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
        var cleaned = CleanupDshHomeProcesses(adopted.DshHome, adopted.Port, keepPids);
        _log.Warn(
            $"instance {adopted.InstanceId} GHOST adopted: old pid {oldPid} -> new pid {listenerPid} " +
            $"(market self-restart), cleaned {cleaned} stale process(es)");
        GhostAdopted?.Invoke(Clone(adopted));
    }

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
        InstanceStopped?.Invoke(Clone(instance));
    }

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
            if (replacement is null || !IsReplacementFor(zombie.Instance, replacement))
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
                Managed = true,
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
            var cleaned = CleanupDshHomeProcesses(revived.DshHome, revived.Port, keepPids);
            _log.Warn(
                $"instance {revived.InstanceId} ZOMBIE revived: stale pid {zombie.Instance.ProcessId} -> new pid {listenerPid}, cleaned {cleaned} stale process(es)");
            GhostAdopted?.Invoke(Clone(revived));
        }
    }

    private void SweepZombies()
    {
        var cutoff = DateTimeOffset.UtcNow - ZombieWindow;
        foreach (var entry in _zombies.Where(pair => pair.Value.Since < cutoff).ToArray())
        {
            _zombies.Remove(entry.Key);
            _log.Info($"instance {entry.Key}: zombie observation window expired, forgotten");
        }
    }

    private void DetectOrphans()
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

        foreach (var instance in known)
        {
            if (instance.Port <= 0)
            {
                continue;
            }

            var listenerPid = ProcessQuery.FindPidByListeningPort(instance.Port);
            if (listenerPid <= 0 || listenerPid == instance.ProcessId)
            {
                continue;
            }

            _log.Warn(
                $"orphan dsh web listener pid={listenerPid} detected on port {instance.Port} for instance {instance.InstanceId} " +
                "while not registered — reported to launcher");
            OrphanDetected?.Invoke(Clone(instance));
        }
    }

    /// <summary>
    /// 替换进程身份校验：命令行含实例 DSH_HOME（Launcher 直启场景），
    /// 或满足 dsh web 主进程特征且监听同端口（market 自重启场景——DSH_HOME
    /// 只作为环境变量传递，命令行里没有！）。
    /// </summary>
    private static bool IsReplacementFor(WatchdogInstanceDto instance, ProcessSnapshot replacement)
    {
        return replacement.CommandLineUpper.Contains(instance.DshHome.ToUpperInvariant(), StringComparison.Ordinal)
               || ProcessQuery.LooksLikeDshWebMain(replacement, instance.Port);
    }

    // ---------- 清理 / 收尾 ----------

    /// <summary>
    /// 清理一个实例的关联进程（保持 keepPids 存活）：
    ///  - 监听该实例端口的主进程（market 重启的无主进程——命令行不含 DSH_HOME，按端口识别）；\n
    ///  - 命令行含该实例 DSH_HOME 的全部进程（桌宠 electron、市场重启包装链等）。\n
    /// 返回实际发起的清理个数。
    /// </summary>
    public int CleanupInstance(string instanceId, int? keepPid = null)
    {
        WatchdogInstanceDto? instance;
        lock (_gate)
        {
            instance = _state.Instances.FirstOrDefault(item => item.InstanceId == instanceId)
                ?? _zombies.Values
                    .FirstOrDefault(zombie => zombie.Instance.InstanceId == instanceId)
                    ?.Instance;
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

        var cleaned = CleanupDshHomeProcesses(instance.DshHome, instance.Port, keepPids);
        if (cleaned > 0)
        {
            _log.Info($"cleanup for instance {instanceId}: killed {cleaned} process(es)");
        }

        return cleaned;
    }

    /// <summary>
    /// 全量收尾清理（Launcher 正常退出前调用）：停掉台账里全部受管实例（按端口/树），
    /// 清掉 DSH_HOME 残留，然后清空台账落盘。幂等。
    /// </summary>
    public void CleanupAllAndClear()
    {
        IReadOnlyList<WatchdogInstanceDto> instances;
        lock (_gate)
        {
            instances = _state.Instances.Where(item => item.Managed).ToArray();
        }

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
                        ProcessQuery.KillTree(listenerPid);
                    }
                }

                var cleaned = CleanupDshHomeProcesses(instance.DshHome, instance.Port, new HashSet<int>());
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

        _log.Info("watchdog final cleanup complete; ledger cleared");
    }

    /// <summary>
    /// 按 DSH_HOME 匹配杀进程 + 按端口杀无主主进程（除 keepPids 及其子树外全部）。
    /// 返回处理数目。逐个 KillTree 幂等执行；taskkill /T 会自动带出整树，重复无害。
    /// </summary>
    private int CleanupDshHomeProcesses(string dshHome, int port, IReadOnlyCollection<int> keepPids)
    {
        var keep = new HashSet<int>(keepPids);
        foreach (var descender in keepPids.Select(pid => ProcessQuery.GetDescendants(pid)))
        {
            foreach (var process in descender)
            {
                keep.Add(process.ProcessId);
            }
        }

        var killed = 0;

        if (port > 0)
        {
            var listenerPid = ProcessQuery.FindPidByListeningPort(port);
            if (listenerPid > 0 && !keep.Contains(listenerPid) && ProcessQuery.IsAlive(listenerPid))
            {
                if (ProcessQuery.KillTree(listenerPid))
                {
                    killed++;
                }
            }
        }

        var candidates = ProcessQuery.FindProcessesForDshHome(
            dshHome, keepAlivePids: keep, excludePids: null);
        foreach (var candidate in candidates)
        {
            if (ProcessQuery.IsProcessName(candidate, "powershell")
                || ProcessQuery.IsProcessName(candidate, "cmd")
                || ProcessQuery.IsProcessName(candidate, "electron")
                || ProcessQuery.IsProcessName(candidate, "node"))
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
