using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class DshInstanceRunner : IAsyncDisposable
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private const int PortStartAttempts = 3;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, RunningDshProcess> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttachedDshService> _attached = new(StringComparer.Ordinal);
    private readonly Func<int> _portAllocator;
    private readonly DshHomeImportService _homeImporter;
    private bool _disposed;

    public DshInstanceRunner(
        Func<int>? portAllocator = null,
        DshHomeImportService? homeImporter = null)
    {
        _portAllocator = portAllocator ?? AllocateFreePort;
        _homeImporter = homeImporter ?? new DshHomeImportService();
    }

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

                var webUrl = instance.WebUrl ?? $"http://127.0.0.1:{instance.Port.Value}/";
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
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetRunning(instance.Id, out var existing))
            {
                return DshInstanceRunResult.Success(
                    existing.Process.Id,
                    existing.Port,
                    existing.WebUrl);
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
                            StartInfo = CreateStartInfo(instance, port, nodeRuntime, sourceEntrypoint),
                            EnableRaisingEvents = true
                        };
                        var output = new StringBuilder();
                        process.OutputDataReceived += (_, args) => AppendOutput(output, args.Data);
                        process.ErrorDataReceived += (_, args) => AppendOutput(output, args.Data);

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
                        process = null;

                        var health = await WaitForHealthAsync(running, cancellationToken);
                        if (health.IsSuccess)
                        {
                            instanceLock = null;
                            return DshInstanceRunResult.Success(running.Process.Id, port, webUrl);
                        }

                        var retryPort = attempt < PortStartAttempts && IsPortConflict(health.Error);
                        await StopCoreAsync(instance.Id, running, releaseInstanceLock: !retryPort);
                        running = null;
                        if (retryPort)
                        {
                            continue;
                        }

                        instanceLock = null;
                        return DshInstanceRunResult.Failure(health.Error ?? "DSh 健康检查失败。 ");
                    }
                    catch
                    {
                        if (running is not null)
                        {
                            await StopCoreAsync(instance.Id, running, releaseInstanceLock: false);
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

                return DshInstanceRunResult.Failure("实例当前没有由 Launcher 管理的运行进程。");
            }

            await StopCoreAsync(instanceId, running);
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
                await StopCoreAsync(instanceId, running);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<HealthResult> WaitForHealthAsync(
        RunningDshProcess running,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = HealthRequestTimeout
        };
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited(running.Process))
            {
                await DrainExitedProcessOutputAsync(running.Process);
                return HealthResult.Failed($"DSh 在健康检查前退出。{GetDiagnosticSuffix(running)}");
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
                        return HealthResult.Failed($"DSh 在健康检查前退出。{GetDiagnosticSuffix(running)}");
                    }

                    return HealthResult.Ok();
                }

                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "HTTP 请求超时";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return HealthResult.Failed($"DSh 健康检查超时（30 秒）。{lastError ?? string.Empty}{GetDiagnosticSuffix(running)}");
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

    private async Task StopCoreAsync(
        string instanceId,
        RunningDshProcess running,
        bool releaseInstanceLock = true)
    {
        lock (_running)
        {
            if (_running.TryGetValue(instanceId, out var current)
                && ReferenceEquals(current, running))
            {
                _running.Remove(instanceId);
            }
        }

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

            if (HasExited(running.Process))
            {
                await DrainExitedProcessOutputAsync(running.Process);
            }
        }
        catch
        {
            // Cleanup must not mask the original start/stop result.
        }
        finally
        {
            if (releaseInstanceLock)
            {
                running.InstanceLock.Dispose();
            }

            running.Process.Dispose();
        }
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

    private string? FindInstanceId(RunningDshProcess running)
    {
        lock (_running)
        {
            return _running.FirstOrDefault(pair => ReferenceEquals(pair.Value, running)).Key;
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        ManagerInstance instance,
        int port,
        NodeRuntimeInfo? nodeRuntime,
        string? sourceEntrypoint)
    {
        var spec = instance.Kind == InstanceKind.Source
            ? new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.NodeScript,
                nodeRuntime!.ExecutablePath!,
                sourceEntrypoint,
                NodeExecutablePath: nodeRuntime.ExecutablePath)
            : DshRuntimeCommandFactory.Resolve(instance)
                ?? throw new InvalidOperationException("实例没有可用的 DSh 启动描述。");
        var arguments = new List<string> { "web" };
        var patchPath = Path.Combine(instance.DshHome, "launcher.patch.yml");
        if (IsRegularFile(patchPath))
        {
            arguments.Add("--patch");
            arguments.Add(patchPath);
        }

        arguments.Add("--host");
        arguments.Add("127.0.0.1");
        arguments.Add("--port");
        arguments.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return DshRuntimeCommandFactory.Create(
            spec,
            arguments,
            instance.RootPath,
            instance.DshHome,
            Path.Combine(instance.DshHome, ".agents"),
            nodeRuntime?.ExecutablePath);
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

    private sealed record RunningDshProcess(
        Process Process,
        int Port,
        string WebUrl,
        StringBuilder Output,
        InstanceLock InstanceLock);

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
