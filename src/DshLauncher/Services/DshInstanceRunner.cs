using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class DshInstanceRunner : IAsyncDisposable
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AuthenticatedUrlWaitTimeout = TimeSpan.FromSeconds(5);
    private const int PortStartAttempts = 3;

    /// <summary>
    /// dsh 0.1.2-rc.1 起 web 应用在启动输出打印带一次性 launch token 的地址行：
    /// `dsh web: http://127.0.0.1:&lt;port&gt;/?token=…`。裸地址请求返回 401，
    /// 必须携带 token（或换取 cookie）才能加载页面；该 token 每次进程启动生成。
    /// </summary>
    private static readonly Regex AuthenticatedUrlPattern = new(
        @"dsh\s+web:\s*(https?://\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, RunningDshProcess> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttachedDshService> _attached = new(StringComparer.Ordinal);
    private readonly Func<int> _portAllocator;
    private readonly DshHomeImportService _homeImporter;
    private readonly ExtensionService? _extensionService;
    private readonly Func<ProxySettings?>? _proxySettings;
    private readonly VersionSettingsService _settingsService;
    private readonly InstanceLogBuffer _logs;
    private readonly SafeProfileService _safeProfileService;
    private readonly InstanceIdleTracker _idleTracker;
    private bool _disposed;

    public DshInstanceRunner(
        Func<int>? portAllocator = null,
        DshHomeImportService? homeImporter = null,
        ExtensionService? extensionService = null,
        Func<ProxySettings?>? proxySettings = null,
        VersionSettingsService? settingsService = null,
        InstanceLogBuffer? logs = null,
        SafeProfileService? safeProfileService = null,
        InstanceIdleTracker? idleTracker = null)
    {
        _portAllocator = portAllocator ?? AllocateFreePort;
        _homeImporter = homeImporter ?? new DshHomeImportService();
        _extensionService = extensionService;
        _proxySettings = proxySettings;
        _settingsService = settingsService ?? new VersionSettingsService();
        _logs = logs ?? new InstanceLogBuffer();
        _safeProfileService = safeProfileService ?? new SafeProfileService();
        _idleTracker = idleTracker ?? new InstanceIdleTracker();
    }

    /// <summary>实例活动追踪（空闲自动停止用）。</summary>
    public InstanceIdleTracker IdleTracker => _idleTracker;

    /// <summary>实例运行日志（dsh 输出 + Launcher 生命周期事件）。</summary>
    public IReadOnlyList<InstanceLogLine> GetLogs(string? instanceId) => _logs.Snapshot(instanceId);

    public void ClearLogs(string? instanceId) => _logs.Clear(instanceId);

    public bool IsRunning(string instanceId)
    {
        lock (_running)
        {
            if (_running.TryGetValue(instanceId, out var running)
                && !HasExited(running.Process))
            {
                return true;
            }
        }

        lock (_attached)
        {
            return _attached.ContainsKey(instanceId);
        }
    }

    public bool IsManaged(string instanceId)
    {
        lock (_running)
        {
            return _running.TryGetValue(instanceId, out var running)
                && !HasExited(running.Process);
        }
    }

    /// <summary>
    /// 读取已退出实例的退出码（崩溃恢复判定用）。进程仍活着或未托管时返回 false。
    /// 注意：要在任何 Stop/移除托管记录之前调用。
    /// </summary>
    public bool TryGetExitedCode(string instanceId, out int? exitCode)
    {
        lock (_running)
        {
            if (_running.TryGetValue(instanceId, out var running) && HasExited(running.Process))
            {
                exitCode = TryGetExitCode(running.Process);
                return true;
            }
        }

        exitCode = null;
        return false;
    }

    public bool IsAttached(string instanceId)
    {
        lock (_attached)
        {
            return _attached.ContainsKey(instanceId);
        }
    }

    public async Task<bool> TryAttachAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAttachEndpoint(instance, out var endpoint))
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsManaged(instance.Id))
            {
                return false;
            }

            if (!await ProbeEndpointAsync(endpoint, cancellationToken))
            {
                return false;
            }

            lock (_attached)
            {
                _attached[instance.Id] = new AttachedDshService(endpoint, instance.Port!.Value);
            }

            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// 收编一个由本 Launcher 之前启动、因异常退出而遗留的运行实例：按注册记录中的
    /// ProcessId 重新取得进程句柄并纳入 Managed 管理，使 Stop/Restart/删除恢复可用。
    /// 仅当记录的端口仍在服务且 PID 仍存活（且进程名与启动包装一致）时收编，
    /// 避免 PID 复用误伤无关进程；失败时回退到只读 Attached 语义。
    /// </summary>
    public async Task<bool> TryAdoptRunningProcessAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        if (instance.ProcessId is not > 0 || instance.Port is not > 0 || string.IsNullOrWhiteSpace(instance.DshHome))
        {
            return false;
        }

        if (!TryGetAttachEndpoint(instance, out var endpoint))
        {
            return false;
        }

        if (!await ProbeEndpointAsync(endpoint, cancellationToken))
        {
            return false;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsManaged(instance.Id) || IsAttached(instance.Id))
            {
                return false;
            }

            Process? process = null;
            InstanceLock? instanceLock = null;
            try
            {
                process = Process.GetProcessById(instance.ProcessId.Value);
                // Launcher 只通过 cmd.exe 包装或 node.exe 直接启动实例；进程名
                // 不一致视为 PID 已被复用，拒绝收编。
                if (!string.Equals(process.ProcessName, "cmd", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(process.ProcessName, "node", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (process.HasExited)
                {
                    return false;
                }

                var lockResult = TryAcquireInstanceLock(instance.DshHome);
                instanceLock = lockResult.Lock;
                if (instanceLock is null)
                {
                    return false;
                }

                var webUrl = instance.AuthenticatedWebUrl ?? instance.WebUrl ?? $"http://127.0.0.1:{instance.Port.Value}/";
                lock (_running)
                {
                    _running[instance.Id] = new RunningDshProcess(
                        process,
                        instance.Port.Value,
                        webUrl,
                        new StringBuilder(),
                        instanceLock);
                }

                // 所有权已移交 _running；finally 不能释放仍被管理的句柄。
                process = null;
                instanceLock = null;
                return true;
            }
            catch (ArgumentException)
            {
                // ProcessId 已不存在：实例实际没有在运行。
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            finally
            {
                process?.Dispose();
                instanceLock?.Dispose();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DshInstanceRunResult> StartAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        return await StartAsync(instance, null, cancellationToken);
    }

    public async Task<DshInstanceRunResult> StartAsync(
        ManagerInstance instance,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken = default,
        bool openBrowser = false,
        SafeProfileTier? safeProfile = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 安全模式：先构建隔离 profile（不改用户文件），并在启动前后取哈希做零污染证据。
        IReadOnlyDictionary<string, byte[]>? profilesBefore = null;
        if (safeProfile is { } tier)
        {
            var build = _safeProfileService.Build(instance, tier);
            if (!build.Ok)
            {
                return DshInstanceRunResult.Failure(
                    $"安全模式隔离 profile 生成失败：{build.Error}",
                    safeMode: true);
            }

            profilesBefore = _safeProfileService.CaptureProfilesHash(instance);
            _logs.Append(instance.Id, "launcher", $"安全模式：已生成 {SafeProfileService.SafeProfileName}（{tier}，bundle：{string.Join(", ", build.Bundles)}）");
        }

        var sourceEntrypoint = instance.Kind == InstanceKind.Source
            ? SourceProjectInspector.TryFindBuiltCliEntrypoint(instance.RootPath)
            : null;
        if (instance.Kind == InstanceKind.Source)
        {
            if (nodeRuntime is null || !nodeRuntime.IsAvailable || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath))
            {
                return DshInstanceRunResult.Failure("Source 实例需要可用的 Node.js 才能启动。");
            }

            var nodeEngine = SourceProjectInspector.TryReadNodeEngine(instance.RootPath);
            var nodeCompatibility = nodeRuntime.GetCompatibility(nodeEngine);
            if (nodeCompatibility != NodeRuntimeCompatibility.Compatible)
            {
                return DshInstanceRunResult.Failure(
                    $"当前 Node.js {nodeRuntime.VersionText} 的兼容状态为 {nodeCompatibility}，不满足 Source 的 engines.node 要求：{nodeEngine ?? "未声明"}。请切换到兼容版本。");
            }

            if (sourceEntrypoint is null)
            {
                return DshInstanceRunResult.Failure("Source 尚未完成构建，找不到 apps/cli/lib/bin.js 或 dist/bin.js。");
            }
        }
        else if (!DshRuntimeCommandFactory.IsUsable(instance.EffectiveDshLaunchSpec))
        {
            return DshInstanceRunResult.Failure("实例的 DSh 启动入口不存在或不完整，请重新检测或重新注册实例。");
        }

        if (!Directory.Exists(instance.RootPath))
        {
            return DshInstanceRunResult.Failure("实例目录不存在，无法启动 DSh。");
        }

        if (!Directory.Exists(instance.DshHome))
        {
            try
            {
                Directory.CreateDirectory(instance.DshHome);
            }
            catch (Exception ex)
            {
                return DshInstanceRunResult.Failure($"无法创建实例 DSH_HOME：{ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(instance.ImportedFromDshHome))
        {
            try
            {
                await _homeImporter.RestoreProfilePackagesAsync(
                    instance.ImportedFromDshHome,
                    instance.DshHome,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                return DshInstanceRunResult.Failure(
                    $"恢复导入配置引用的 Plugin 失败：{ex.Message}");
            }

            // 方案 A 自愈：RestoreProfilePackages 只复制插件目录（且跳过 junction），
            // 平铺依赖与 link:/file: 插件都不会随导入复制；声明却在 package.json /
            // cordis.patch.yml 中保留，dsh web 启动会因 bundle 不可解析 fail-loud
            // （“健康检查前退出”）。这里按 lock 文件 pnpm install 恢复完整依赖图。
            // 自愈失败要中止启动并给出可操作错误，而不是让健康检查 30 秒后再报“退出”。
            if (_extensionService is not null)
            {
                try
                {
                    await _extensionService.EnsureProfileDependenciesAsync(
                        instance,
                        nodeRuntime,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or TimeoutException)
                {
                    return DshInstanceRunResult.Failure(
                        $"插件依赖自愈失败：{ex.Message}");
                }
            }
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetRunning(instance.Id, out var existing))
            {
                return DshInstanceRunResult.Success(
                    existing.Process.Id,
                    existing.Port,
                    existing.WebUrl,
                    existing.AuthenticatedWebUrl);
            }

            if (IsAttached(instance.Id))
            {
                return DshInstanceRunResult.Failure(
                    "该实例已经连接到外部 DSh 服务，Launcher 不会再启动第二个进程。请先让外部服务退出，或在实例页清除连接状态。");
            }

            RemoveExited(instance.Id);

            InstanceLock? instanceLock = null;
            try
            {
                var lockResult = TryAcquireInstanceLock(instance.DshHome);
                instanceLock = lockResult.Lock;
                if (instanceLock is null)
                {
                    return DshInstanceRunResult.Failure(lockResult.IsHeld
                        ? "此实例的 DSH_HOME 已被另一个 Launcher 或遗留 DSh 进程锁定，不能重复启动。请先在另一处停止实例；若任务栏已没有对应窗口，请结束残留进程后重试。"
                        : $"无法建立实例启动锁：{lockResult.Error ?? "锁目录不可访问"}。请检查当前用户对 Launcher 数据目录的权限。");
                }

                for (var attempt = 1; attempt <= PortStartAttempts; attempt++)
                {
                    Process? process = null;
                    RunningDshProcess? running = null;
                    try
                    {
                        var port = _portAllocator();
                        var webUrl = $"http://127.0.0.1:{port}/";
                        process = new Process
                        {
                            StartInfo = CreateStartInfo(instance, port, nodeRuntime, sourceEntrypoint, openBrowser, safeProfile),
                            EnableRaisingEvents = true
                        };
                        var output = new StringBuilder();
                        process.OutputDataReceived += (_, args) =>
                        {
                            AppendOutput(output, args.Data);
                            _logs.Append(instance.Id, "dsh", args.Data);
                            _idleTracker.MarkActivity(instance.Id, "dsh 输出");
                            TryCaptureAuthenticatedUrl(instance.Id, args.Data);
                        };
                        process.ErrorDataReceived += (_, args) =>
                        {
                            AppendOutput(output, args.Data);
                            _logs.Append(instance.Id, "stderr", args.Data);
                            _idleTracker.MarkActivity(instance.Id, "dsh 错误输出");
                        };

                        if (!process.Start())
                        {
                            return DshInstanceRunResult.Failure("DSh 进程无法启动。 ");
                        }

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        running = new RunningDshProcess(process, port, webUrl, output, instanceLock);
                        lock (_running)
                        {
                            _running[instance.Id] = running;
                        }

                        _logs.Append(
                            instance.Id,
                            "launcher",
                            $"启动进程 pid={process.Id}，端口 {port}，地址 {webUrl}");
                        process = null;

                        var health = await WaitForHealthAsync(instance, running, cancellationToken);
                        if (health.IsHealthy)
                        {
                            // 0.1.2-rc.1 起 web 页面需要 launch token；地址行在 Loader
                            // 树落定后才打印（可能晚于健康检查通过），再等一小段窗口。
                            await WaitForAuthenticatedUrlAsync(running, cancellationToken);
                            _logs.Append(instance.Id, "launcher", $"健康检查通过：{webUrl}");
                            instanceLock = null;
                            if (safeProfile is { } usedTier)
                            {
                                var untouched = _safeProfileService.ProfilesUntouched(
                                    instance, profilesBefore!, out var changed);
                                _logs.Append(
                                    instance.Id,
                                    "launcher",
                                    untouched
                                        ? "安全模式启动成功；用户 profile 零污染校验通过。"
                                        : $"安全模式启动成功，但零污染校验发现 {changed.Count} 个文件被改动：{string.Join(", ", changed.Take(3))}");
                                if (!untouched)
                                {
                                    LauncherLog.Warn("安全模式零污染校验失败。", ErrorCodes.E1014,
                                        new { instance = instance.Name, changed = changed.Take(5).ToArray() });
                                }

                                return DshInstanceRunResult.Success(
                                    running.Process.Id,
                                    port,
                                    webUrl,
                                    running.AuthenticatedWebUrl,
                                    safeMode: true,
                                    zeroPollution: untouched,
                                    evidence: health.Evidence);
                            }

                            // 正常启动成功：清理上次安全模式遗留的隔离 profile。
                            _safeProfileService.Cleanup(instance);
                            return DshInstanceRunResult.Success(
                                running.Process.Id,
                                port,
                                webUrl,
                                running.AuthenticatedWebUrl,
                                evidence: health.Evidence);
                        }

                        var retryPort = attempt < PortStartAttempts && IsPortConflict(health.Summary);
                        if (!await StopCoreAsync(instance.Id, running, releaseInstanceLock: !retryPort))
                        {
                            // The running entry still owns the lock and process.
                            // Do not let the outer finally release it while the
                            // process may still be writing this DSH_HOME.
                            instanceLock = null;
                            return DshInstanceRunResult.Failure(
                                $"DSh 启动失败后无法终止残留进程，请先结束进程 {running.Process.Id} 再重试。{GetDiagnosticSuffix(running)}");
                        }
                        running = null;
                        if (retryPort)
                        {
                            continue;
                        }

                        instanceLock = null;
                        return DshInstanceRunResult.Failure(
                            health.Summary,
                            safeMode: safeProfile is not null,
                            evidence: health.Evidence);
                    }
                    catch
                    {
                        if (running is not null)
                        {
                            if (!await StopCoreAsync(instance.Id, running, releaseInstanceLock: false))
                            {
                                instanceLock = null;
                            }
                        }

                        throw;
                    }
                    finally
                    {
                        process?.Dispose();
                    }
                }

                return DshInstanceRunResult.Failure("连续 3 次分配端口都发生冲突，未启动 DSh。请关闭占用本机临时端口的程序后重试。 ");
            }
            finally
            {
                instanceLock?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DshInstanceRunResult.Failure($"启动 DSh 失败：{ex.Message}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DshInstanceRunResult> StopAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryGetRunning(instanceId, out var running))
            {
                if (IsAttached(instanceId))
                {
                    return DshInstanceRunResult.Failure(
                        "该实例由外部 DSh 服务提供，Launcher 不会停止外部进程。");
                }

                // 陈旧条目（进程已死但实例锁仍被句柄占用，如 market 自重启后）：
                // 清账即视为已停止，让界面收敛为 Stopped 而不是报错。
                if (TryDropExitedProcess(instanceId))
                {
                    return DshInstanceRunResult.Success(0, 0, string.Empty);
                }

                return DshInstanceRunResult.Failure("实例当前没有由 Launcher 管理的运行进程。");
            }

            if (!await StopCoreAsync(instanceId, running))
            {
                return DshInstanceRunResult.Failure(
                    $"无法终止 DSh 进程 {running.Process.Id}；实例仍按运行中保留，未释放实例锁。");
            }
            return DshInstanceRunResult.Success(0, running.Port, running.WebUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DshInstanceRunResult.Failure($"停止 DSh 失败：{ex.Message}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DshInstanceRunResult> RestartAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        return await RestartAsync(instance, null, cancellationToken);
    }

    public async Task<DshInstanceRunResult> RestartAsync(
        ManagerInstance instance,
        NodeRuntimeInfo? nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsAttached(instance.Id))
        {
            return DshInstanceRunResult.Failure(
                "该实例由外部 DSh 服务提供，Launcher 不会停止或重启外部进程。");
        }

        await StopIfRunningAsync(instance.Id, cancellationToken);
        return await StartAsync(instance, nodeRuntime, cancellationToken);
    }

    public async Task StopAllAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _operationGate.WaitAsync();
        try
        {
            RunningDshProcess[] processes;
            lock (_running)
            {
                processes = _running.ToArray()
                    .Select(pair => pair.Value)
                    .ToArray();
            }

            foreach (var running in processes)
            {
                var instanceId = FindInstanceId(running);
                if (instanceId is not null)
                {
                    await StopCoreAsync(instanceId, running);
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAllAsync();
        _disposed = true;
        _operationGate.Dispose();
    }

    private async Task StopIfRunningAsync(string instanceId, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetRunning(instanceId, out var running))
            {
                if (!await StopCoreAsync(instanceId, running))
                {
                    throw new InvalidOperationException(
                        $"无法终止 DSh 进程 {running.Process.Id}；已取消后续重启。 ");
                }
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// 启动健康检查（四层证据的进程/日志/HTTP 三层，页面层由 ChatWindow 上报）：
    ///  - 进程层：提前退出 → 判死（附 exitCode 与最后输出）；
    ///  - 日志层：只扫新增行，命中启动失败签名 → 判死（比等满 30 秒快）；
    ///  - HTTP 层：探测通过 → 健康；探针自身异常只记证据，不单独判死；
    ///  - 超时 → 判死（附最后一次 HTTP 错误与输出尾巴）。
    /// </summary>
    private async Task<StartupVerdict> WaitForHealthAsync(
        ManagerInstance instance,
        RunningDshProcess running,
        CancellationToken cancellationToken)
    {
        var evidence = new List<StartupEvidence>();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = HealthRequestTimeout
        };
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;
        string? lastError = null;
        var consecutiveMisses = 0;
        var scannedLines = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited(running.Process))
            {
                await DrainExitedProcessOutputAsync(running.Process);
                var exitCode = TryGetExitCode(running.Process);
                evidence.Add(new StartupEvidence(
                    BootLayer.Process,
                    "DSh 进程在健康检查通过前退出",
                    $"exitCode={exitCode?.ToString() ?? "?"}"));
                return StartupVerdict.Failed(
                    $"DSh 在健康检查前退出（exitCode={exitCode?.ToString() ?? "?"}）。{GetDiagnosticSuffix(running)}",
                    evidence);
            }

            // 日志层：只看监控起点之后的新增行。
            var logs = _logs.Snapshot(instance.Id);
            if (logs.Count > scannedLines)
            {
                var newLines = logs.Skip(scannedLines).ToArray();
                scannedLines = logs.Count;
                if (StartupLogClassifier.FindFailure(newLines) is { } failure)
                {
                    evidence.Add(new StartupEvidence(BootLayer.Log, failure.Description, failure.Line.Text));
                    return StartupVerdict.Failed(
                        $"DSh 启动日志出现失败签名：{failure.Description}。{failure.Line.Text}",
                        evidence);
                }
            }

            try
            {
                using var response = await client.GetAsync(running.WebUrl, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    // A port can be stolen after allocation. Do not accept a
                    // successful response from that unrelated listener while
                    // our own DSh process is already exiting with EADDRINUSE.
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                    if (HasExited(running.Process))
                    {
                        await DrainExitedProcessOutputAsync(running.Process);
                        var exitCode = TryGetExitCode(running.Process);
                        evidence.Add(new StartupEvidence(
                            BootLayer.Process,
                            "DSh 进程在健康检查通过前退出",
                            $"exitCode={exitCode?.ToString() ?? "?"}"));
                        return StartupVerdict.Failed(
                            $"DSh 在健康检查前退出（exitCode={exitCode?.ToString() ?? "?"}）。{GetDiagnosticSuffix(running)}",
                            evidence);
                    }

                    evidence.Add(new StartupEvidence(BootLayer.Http, $"HTTP {(int)response.StatusCode} 健康检查通过", running.WebUrl));
                    return StartupVerdict.Healthy(evidence);
                }

                consecutiveMisses++;
                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                consecutiveMisses++;
                lastError = "HTTP 请求超时";
            }
            catch (HttpRequestException ex)
            {
                // 探针自身异常只记证据，不单独判死（等待其它层或超时）。
                consecutiveMisses++;
                lastError = ex.Message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        evidence.Add(new StartupEvidence(
            BootLayer.Http,
            "健康检查超时（30 秒）",
            $"连续 {consecutiveMisses} 次未通过；最后错误：{lastError ?? "无"}"));
        return StartupVerdict.Failed(
            $"DSh 健康检查超时（30 秒）。{lastError ?? string.Empty}{GetDiagnosticSuffix(running)}",
            evidence);
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 dsh 的启动输出解析带 launch token 的 Web 地址（0.1.2-rc.1 新增）。
    /// 输出行形如 `dsh web: http://127.0.0.1:&lt;port&gt;/?token=…`；打印时机在
    /// Loader 树落定之后，可能晚于健康检查通过，因此健康检查成功后再等待一小段
    /// 窗口：先扫输出缓冲（兜底），再轮询事件捕获结果。超时静默——不影响启动，
    /// 只是 Chat 窗口可能退化为需要用户粘贴带 token 地址。
    /// </summary>
    private static async Task WaitForAuthenticatedUrlAsync(
        RunningDshProcess running,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + AuthenticatedUrlWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(running.AuthenticatedWebUrl))
            {
                return;
            }

            if (TryScanOutputForAuthenticatedUrl(running))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static bool TryScanOutputForAuthenticatedUrl(RunningDshProcess running)
    {
        string? snapshot;
        lock (running.Output)
        {
            if (running.Output.Length == 0)
            {
                return false;
            }

            snapshot = running.Output.ToString();
        }

        if (TryExtractAuthenticatedUrl(snapshot, out var url))
        {
            running.AuthenticatedWebUrl = url;
            return true;
        }

        return false;
    }

    private static bool TryExtractAuthenticatedUrl(string? text, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = AuthenticatedUrlPattern.Match(text);
        if (!match.Success || !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var parsed)
            || !parsed.IsLoopback || parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        url = parsed.ToString();
        return true;
    }

    /// <summary>
    /// 输出事件快速路径：从 `dsh web: …` 行捕获带 token 地址。捕获到一次后不再处理；
    /// 与输出缓冲兜底（{@link WaitForAuthenticatedUrlAsync}）互补，覆盖事件早于
    /// 运行条目入表时的间隙。
    /// </summary>
    private void TryCaptureAuthenticatedUrl(string instanceId, string? line)
    {
        if (!TryExtractAuthenticatedUrl(line, out var url))
        {
            return;
        }

        lock (_running)
        {
            if (_running.TryGetValue(instanceId, out var current)
                && string.IsNullOrWhiteSpace(current.AuthenticatedWebUrl))
            {
                current.AuthenticatedWebUrl = url;
            }
        }
    }

    private static async Task<bool> ProbeEndpointAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = HealthRequestTimeout
        };

        try
        {
            using var response = await client.GetAsync(endpoint, cancellationToken);
            return (int)response.StatusCode < 500;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<bool> StopCoreAsync(
        string instanceId,
        RunningDshProcess running,
        bool releaseInstanceLock = true)
    {
        try
        {
            if (!HasExited(running.Process))
            {
                running.Process.Kill(entireProcessTree: true);
                var waitTask = running.Process.WaitForExitAsync();
                var completed = await Task.WhenAny(waitTask, Task.Delay(StopTimeout));
                if (completed != waitTask && !HasExited(running.Process))
                {
                    running.Process.Kill(entireProcessTree: true);
                    await Task.WhenAny(running.Process.WaitForExitAsync(), Task.Delay(StopTimeout));
                }
            }

            if (!HasExited(running.Process))
            {
                return false;
            }

            await DrainExitedProcessOutputAsync(running.Process);
        }
        catch
        {
            if (!HasExited(running.Process))
            {
                return false;
            }
        }

        lock (_running)
        {
            if (_running.TryGetValue(instanceId, out var current)
                && ReferenceEquals(current, running))
            {
                _running.Remove(instanceId);
            }
        }

        _logs.Append(instanceId, "launcher", $"已停止进程 pid={running.Process.Id}");

        if (releaseInstanceLock)
        {
            running.InstanceLock.Dispose();
        }

        running.Process.Dispose();
        return true;
    }

    private bool TryGetRunning(string instanceId, out RunningDshProcess running)
    {
        lock (_running)
        {
            if (_running.TryGetValue(instanceId, out running!)
                && !HasExited(running.Process))
            {
                return true;
            }

            running = null!;
            return false;
        }
    }

    private static bool TryGetAttachEndpoint(ManagerInstance instance, out Uri endpoint)
    {
        endpoint = null!;
        if (instance.Port is not > 0
            || string.IsNullOrWhiteSpace(instance.WebUrl)
            || !Uri.TryCreate(instance.WebUrl, UriKind.Absolute, out var parsed)
            || !parsed.IsLoopback
            || parsed.Scheme is not ("http" or "https")
            || parsed.Port != instance.Port.Value)
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private void RemoveExited(string instanceId)
    {
        lock (_running)
        {
            if (!_running.TryGetValue(instanceId, out var running)
                || !HasExited(running.Process))
            {
                return;
            }

            _running.Remove(instanceId);
            running.InstanceLock.Dispose();
            running.Process.Dispose();
        }
    }

    /// <summary>
    /// 清理已退出的陈旧运行条目并释放其实例锁。
    /// 场景：dshmarket 自重启杀死旧进程后，条目仍留在 _running 里并**继续持有
    /// 实例锁**（文件句柄在 Launcher 进程内、不会因子进程死亡自动释放），导致
    /// 后续 TryAdoptRunningProcessAsync 取锁失败、新进程只能退化为只读 Attached
    /// （无法停止/重启）。转正/停止前必须先清账。
    /// </summary>
    public bool TryDropExitedProcess(string instanceId)
    {
        lock (_running)
        {
            if (!_running.TryGetValue(instanceId, out var running)
                || !HasExited(running.Process))
            {
                return false;
            }

            _running.Remove(instanceId);
            running.InstanceLock.Dispose();
            running.Process.Dispose();
            return true;
        }
    }

    private string? FindInstanceId(RunningDshProcess running)
    {
        lock (_running)
        {
            return _running.FirstOrDefault(pair => ReferenceEquals(pair.Value, running)).Key;
        }
    }

    private ProcessStartInfo CreateStartInfo(
        ManagerInstance instance,
        int port,
        NodeRuntimeInfo? nodeRuntime,
        string? sourceEntrypoint,
        bool openBrowser,
        SafeProfileTier? safeProfile)
    {
        var spec = instance.Kind == InstanceKind.Source
            ? new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.NodeScript,
                nodeRuntime!.ExecutablePath!,
                sourceEntrypoint,
                NodeExecutablePath: nodeRuntime.ExecutablePath)
            : DshRuntimeCommandFactory.Resolve(instance)
                ?? throw new InvalidOperationException("实例没有可用的 DSh 启动描述。");
        var patchPath = Path.Combine(instance.DshHome, "launcher.patch.yml");
        var arguments = BuildStartArguments(
            safeMode: safeProfile is not null,
            supportsNoOpen: SupportsNoOpen(instance.DetectedVersion),
            patchPath: IsRegularFile(patchPath) ? patchPath : null,
            port: port);
        var startInfo = DshRuntimeCommandFactory.Create(
            spec,
            arguments,
            instance.RootPath,
            instance.DshHome,
            Path.Combine(instance.DshHome, ".agents"),
            nodeRuntime?.ExecutablePath,
            environmentOverrides: ResolveInstanceEnvironment(instance));
        // Launcher 级代理：显式注入实例环境（在 dsh 的 npm 代理回退之前）。
        try
        {
            _proxySettings?.Invoke()?.ApplyTo(startInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LauncherLog.Warn("代理注入实例环境失败（按继承环境运行）。", ErrorCodes.E3001,
                new { instance = instance.Name, error = ex.Message });
        }

        return startInfo;
    }

    /// <summary>
    /// 构造 dsh 启动参数（可测）：安全模式用 <c>--profile .dsh-safe</c>，正常模式用 <c>web</c>。
    /// dsh 0.1.2 的浏览器交接（open@11 的 Windows 实现）实测不弹浏览器：其 PowerShell
    /// Start 调用静默失败（exit 0 且无新窗口），而直接 Start-Process / UseShellExecute 正常。
    /// Launcher 改为全部托管打开：总是传 --no-open（0.1.0-rc.8+ 支持），启动成功后由
    /// Launcher 用系统默认方式打开；旧版 dsh 不传，保持其原生行为。
    /// </summary>
    internal static List<string> BuildStartArguments(
        bool safeMode,
        bool supportsNoOpen,
        string? patchPath,
        int port)
    {
        var arguments = safeMode
            ? new List<string> { "--profile", SafeProfileService.SafeProfileName }
            : new List<string> { "web" };
        if (!string.IsNullOrWhiteSpace(patchPath))
        {
            arguments.Add("--patch");
            arguments.Add(patchPath);
        }

        if (supportsNoOpen)
        {
            arguments.Add("--no-open");
        }

        arguments.Add("--host");
        arguments.Add("127.0.0.1");
        arguments.Add("--port");
        arguments.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return arguments;
    }

    /// <summary>读取该实例的环境变量设置（解密后的明文）；失败时按无变量运行。</summary>
    private IReadOnlyDictionary<string, string>? ResolveInstanceEnvironment(ManagerInstance instance)
    {
        try
        {
            return _settingsService.Read(instance).EnvironmentVariables;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LauncherLog.Warn("读取实例环境变量失败（按继承环境运行）。", ErrorCodes.E1013,
                new { instance = instance.Name, error = ex.Message });
            return null;
        }
    }

    /// <summary>
    /// dsh 是否支持 --no-open（0.1.0-rc.8 引入）。支持条件 = 版本 ≥ 0.1.0-rc.8：
    /// 主版本段大于 0.1.0 的一切版本（如 0.1.1-rc.2、0.2.x、1.x）都满足，
    /// 预发布后缀只在主段恰好等于 0.1.0 时参与比较（rc.N 需 ≥ 8）；
    /// 解析失败（未知/旧版）时按不支持处理——不传开关，保持旧版能正常启动。
    /// </summary>
    internal static bool SupportsNoOpen(string? version)
    {
        var trimmed = version?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var coreAndPre = trimmed.Split('-');
        var core = coreAndPre[0].Split('.')
            .Select(part => int.TryParse(part, out var number) ? number : -1)
            .ToArray();
        if (core.Length < 2 || core[0] < 0 || core[1] < 0)
        {
            return false;
        }

        var patch = core.Length > 2 && core[2] >= 0 ? core[2] : 0;
        if (core[0] > 0
            || (core[0] == 0 && (core[1] > 1 || (core[1] == 1 && patch > 0))))
        {
            return true;   // 主版本段 > 0.1.0：0.1.1-rc.2 / 0.2.x / 1.x 等均支持
        }

        if (core[0] == 0 && core[1] == 1 && patch == 0)
        {
            var pre = coreAndPre.Length > 1 ? coreAndPre[1] : null;
            if (string.IsNullOrWhiteSpace(pre))
            {
                return true;   // 0.1.0 正式版 > 0.1.0-rc.8
            }

            // 仅识别 rc 预发布段；其他预发布（beta 等）按非正式处理
            return pre.StartsWith("rc.", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pre.AsSpan(3), out var rc)
                && rc >= 8;
        }

        return false;   // 0.0.x 及更低：不支持
    }

    internal static string BuildPathWithNodeDirectory(string? nodeExecutablePath, string currentPath)
    {
        if (string.IsNullOrWhiteSpace(nodeExecutablePath))
        {
            return currentPath;
        }

        var nodeDirectory = Path.GetDirectoryName(Path.GetFullPath(nodeExecutablePath));
        if (string.IsNullOrWhiteSpace(nodeDirectory))
        {
            return currentPath;
        }

        var entries = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static entry => entry.Trim().Trim('"'))
            .Where(static entry => entry.Length > 0)
            .ToList();
        if (entries.Contains(nodeDirectory, StringComparer.OrdinalIgnoreCase))
        {
            return currentPath;
        }

        return nodeDirectory + Path.PathSeparator + string.Join(Path.PathSeparator, entries);
    }

    private static void AddLauncherPatch(ProcessStartInfo startInfo, ManagerInstance instance)
    {
        var patchPath = Path.Combine(instance.DshHome, "launcher.patch.yml");
        if (IsRegularFile(patchPath))
        {
            startInfo.ArgumentList.Add("--patch");
            startInfo.ArgumentList.Add(patchPath);
        }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            return File.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int AllocateFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static InstanceLockResult TryAcquireInstanceLock(string dshHome)
    {
        var normalizedHome = Path.GetFullPath(dshHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var lockDirectory = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Path.GetTempPath(), "DSH Launcher", "locks")
            : Path.Combine(localAppData, "DeepSeek", "launcher", "locks");
        var lockPath = Path.Combine(
            lockDirectory,
            $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedHome)))}.lock");
        try
        {
            Directory.CreateDirectory(lockDirectory);
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return InstanceLockResult.Acquired(new InstanceLock(stream));
        }
        catch (IOException ex)
        {
            return IsLockContention(ex)
                ? InstanceLockResult.Held(ex.Message)
                : InstanceLockResult.Unavailable(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return InstanceLockResult.Unavailable(ex.Message);
        }
    }

    private static bool IsPortConflict(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || message.Contains("端口已被占用", StringComparison.OrdinalIgnoreCase)
            || message.Contains("地址已在使用", StringComparison.OrdinalIgnoreCase));

    private static async Task DrainExitedProcessOutputAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            // WaitForExitAsync observes process termination. The parameterless
            // wait additionally drains asynchronous redirected output handlers.
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // The process may already have been disposed by concurrent cleanup.
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        var windowsError = exception.HResult & 0xFFFF;
        return windowsError is 32 or 33;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static void AppendOutput(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (output)
        {
            if (output.Length > 6000)
            {
                output.Remove(0, output.Length - 4000);
            }

            output.AppendLine(line.Trim());
        }
    }

    private static string GetDiagnosticSuffix(RunningDshProcess running)
    {
        lock (running.Output)
        {
            return running.Output.Length == 0
                ? string.Empty
                : $" 输出：{running.Output.ToString().Trim()}";
        }
    }

    private sealed class RunningDshProcess
    {
        public RunningDshProcess(
            Process process,
            int port,
            string webUrl,
            StringBuilder output,
            InstanceLock instanceLock)
        {
            Process = process;
            Port = port;
            WebUrl = webUrl;
            Output = output;
            InstanceLock = instanceLock;
        }

        public Process Process { get; }

        public int Port { get; }

        public string WebUrl { get; }

        public StringBuilder Output { get; }

        public InstanceLock InstanceLock { get; }

        /// <summary>
        /// dsh 0.1.2-rc.1 起启动输出里的带 launch token 地址（
        /// `http://127.0.0.1:&lt;port&gt;/?token=…`），仅供 Chat 窗口导航；
        /// 未捕获到时为 null，调用方回退裸地址。
        /// </summary>
        public string? AuthenticatedWebUrl { get; set; }
    }

    private sealed record AttachedDshService(Uri Endpoint, int Port);

    private sealed class InstanceLock : IDisposable
    {
        private readonly FileStream _stream;

        public InstanceLock(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }

    private sealed record InstanceLockResult(
        InstanceLock? Lock,
        bool IsHeld,
        string? Error)
    {
        public static InstanceLockResult Acquired(InstanceLock instanceLock) =>
            new(instanceLock, false, null);

        public static InstanceLockResult Held(string error) =>
            new(null, true, error);

        public static InstanceLockResult Unavailable(string error) =>
            new(null, false, error);
    }

    private sealed record HealthResult(bool IsSuccess, string? Error)
    {
        public static HealthResult Ok() => new(true, null);

        public static HealthResult Failed(string error) => new(false, error);
    }
}