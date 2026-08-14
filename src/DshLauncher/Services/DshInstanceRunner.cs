using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class DshInstanceRunner : IAsyncDisposable
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, RunningDshProcess> _running = new(StringComparer.Ordinal);
    private bool _disposed;

    public bool IsRunning(string instanceId)
    {
        lock (_running)
        {
            return _running.TryGetValue(instanceId, out var running)
                && !HasExited(running.Process);
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

            if (!nodeRuntime.IsCompatibleWithDshSource)
            {
                return DshInstanceRunResult.Failure(
                    $"当前 Node.js {nodeRuntime.VersionText} 不满足 Source 要求：22.19+ 的 22.x 或 24+。请切换到兼容版本。");
            }

            if (sourceEntrypoint is null)
            {
                return DshInstanceRunResult.Failure("Source 尚未完成构建，找不到 apps/cli/lib/bin.js 或 dist/bin.js。");
            }
        }
        else if (string.IsNullOrWhiteSpace(instance.DshExecutablePath)
            || !File.Exists(instance.DshExecutablePath))
        {
            return DshInstanceRunResult.Failure("实例的 DSh 可执行入口不存在，请重新检测或重新注册实例。");
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

            RemoveExited(instance.Id);

            InstanceLock? instanceLock = null;
            Process? process = null;
            try
            {
                instanceLock = TryAcquireInstanceLock(instance.DshHome);
                if (instanceLock is null)
                {
                    return DshInstanceRunResult.Failure("此实例已由另一个 Launcher 进程管理，不能同时启动相同 DSH_HOME。请先停止另一处运行实例。");
                }

                var port = AllocateFreePort();
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
                    return DshInstanceRunResult.Failure("DSh 进程无法启动。");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var running = new RunningDshProcess(process, port, webUrl, output, instanceLock);
                lock (_running)
                {
                    _running[instance.Id] = running;
                }
                process = null;
                instanceLock = null;

                try
                {
                    var health = await WaitForHealthAsync(running, cancellationToken);
                    if (!health.IsSuccess)
                    {
                        await StopCoreAsync(instance.Id, running);
                        return DshInstanceRunResult.Failure(health.Error ?? "DSh 健康检查失败。");
                    }

                    return DshInstanceRunResult.Success(running.Process.Id, port, webUrl);
                }
                catch
                {
                    await StopCoreAsync(instance.Id, running);
                    throw;
                }
            }
            finally
            {
                process?.Dispose();
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
                return HealthResult.Failed($"DSh 在健康检查前退出。{GetDiagnosticSuffix(running)}");
            }

            try
            {
                using var response = await client.GetAsync(running.WebUrl, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
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

    private async Task StopCoreAsync(string instanceId, RunningDshProcess running)
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
        }
        catch
        {
            // Cleanup must not mask the original start/stop result.
        }
        finally
        {
            running.InstanceLock.Dispose();
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
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = instance.RootPath,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (instance.Kind == InstanceKind.Source)
        {
            startInfo.FileName = nodeRuntime!.ExecutablePath!;
            startInfo.ArgumentList.Add(sourceEntrypoint!);
            startInfo.ArgumentList.Add("web");
            AddLauncherPatch(startInfo, instance);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            var executablePath = instance.DshExecutablePath!;
            if (Path.GetExtension(executablePath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(executablePath).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                var patchPath = Path.Combine(instance.DshHome, "launcher.patch.yml");
                var patchArgument = IsRegularFile(patchPath)
                    ? $" --patch \"{patchPath}\""
                    : string.Empty;
                var commandArguments = $"web{patchArgument} --host 127.0.0.1 --port "
                    + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                startInfo.Arguments = $"/d /c \"\"{executablePath}\" {commandArguments}\"";
            }
            else
            {
                startInfo.FileName = executablePath;
                startInfo.ArgumentList.Add("web");
                AddLauncherPatch(startInfo, instance);
                startInfo.ArgumentList.Add("--host");
                startInfo.ArgumentList.Add("127.0.0.1");
                startInfo.ArgumentList.Add("--port");
                startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        startInfo.Environment["DSH_HOME"] = instance.DshHome;
        // Keep the second default skill root isolated as well. Without this
        // variable DSh falls back to the user's global %USERPROFILE%\.agents.
        startInfo.Environment["DSH_AGENTS_HOME"] = Path.Combine(instance.DshHome, ".agents");
        return startInfo;
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

    private static InstanceLock? TryAcquireInstanceLock(string dshHome)
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
            return new InstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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

    private sealed record HealthResult(bool IsSuccess, string? Error)
    {
        public static HealthResult Ok() => new(true, null);

        public static HealthResult Failed(string error) => new(false, error);
    }
}
