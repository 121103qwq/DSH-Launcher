using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DshLauncher.Watchdog;

namespace DshLauncher.Services;

/// <summary>
/// Launcher ↔ Watchdog 的客户端（后者是独立的 DSH Launcher.Watchdog.exe）。
///
/// 职责：
///  - 确保 Watchdog 存在（连接旧实例；或作为子进程拉起）；
///  - 实例登记/注销/快照/清理/关机（JSON 行请求-应答）；
///  - 接收 Watchdog 事件（转正/孤儿/停止）转发给 UI。
///
/// 降级策略：Watchdog exe 缺失或连接失败 → 全部操作 no-op，Launcher 行为与
/// 未引入 Watchdog 时完全一致（不阻塞任何现有功能）。
/// </summary>
public sealed class WatchdogSupervisor : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(8);

    private readonly int _launcherPid;
    private readonly string _pipeName;
    private readonly object _gate = new();
    private NamedPipeClientStream? _connection;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readerLoop;
    private readonly Queue<TaskCompletionSource<WatchdogProtocol.Response>> _pending = new();
    private Process? _process;
    private bool _disposed;

    /// <summary>Watchdog 事件（ghost-adopted / ghost-orphan / instance-stopped）。</summary>
    public event Action<WatchdogProtocol.Response>? EventReceived;

    public bool Connected
    {
        get
        {
            lock (_gate)
            {
                return _connection is { IsConnected: true };
            }
        }
    }

    public WatchdogSupervisor(int launcherPid)
    {
        _launcherPid = launcherPid;
        _pipeName = WatchdogProtocol.GetLauncherPipeName(Process.GetCurrentProcess().SessionId);
    }

    /// <summary>
    /// 确保 Watchdog 在线：先尝试连接已有实例（Launcher 重启场景），
    /// 失败则从同目录拉起新进程并等待管道就绪。找不到 exe → 静默降级。
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            if (ChildProcessExistsAndRunning())
            {
                if (await TryConnectAsync(cancellationToken))
                {
                    return true;
                }
            }

            if (!await TryConnectAsync(cancellationToken))
            {
                if (!TrySpawnProcess())
                {
                    return false;
                }
            }

            // 新进程需要一点时间注册管道；轮询连接直到成功或超时。
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (!Connected && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(200, cancellationToken);
                if (!await TryConnectAsync(cancellationToken))
                {
                    continue;
                }
            }

            return Connected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool ChildProcessExistsAndRunning()
    {
        try
        {
            if (_process is null)
            {
                return false;
            }

            _process.Refresh();
            return !_process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySpawnProcess()
    {
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "DSH Launcher.Watchdog.exe");
            if (!File.Exists(executable))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--launcher-pid");
            startInfo.ArgumentList.Add(_launcherPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = new Process { StartInfo = startInfo };
            _ = process.Start();
            // 守护进程 stdout/stderr 是诊断备用通道（主体日志在 watchdog.log）。
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            _process = process;
            process.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_connection, null))
                {
                    client.Dispose();
                    return true; // 已有活动连接
                }

                _connection = client;
                _reader = new StreamReader(client, Encoding.UTF8);
                _writer = new StreamWriter(client, Encoding.UTF8)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                _readerLoop = Task.Run(() => ReadLoopAsync(client), CancellationToken.None);
            }

            _ = await SendAsync(WatchdogProtocol.TaskPing, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream client)
    {
        try
        {
            while (true)
            {
                var reader = _reader;
                if (reader is null)
                {
                    break;
                }

                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WatchdogProtocol.Response? response;
                try
                {
                    response = JsonSerializer.Deserialize<WatchdogProtocol.Response>(
                        line,
                        WatchdogProtocol.Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (response is null)
                {
                    continue;
                }

                if (response.T == "event")
                {
                    EventReceived?.Invoke(response);
                    continue;
                }

                TaskCompletionSource<WatchdogProtocol.Response>? pending = null;
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        pending = _pending.Dequeue();
                    }
                }

                pending?.TrySetResult(response);
            }
        }
        catch
        {
            // 连接断开：由下一轮 EnsureStarted/重连恢复。
        }
        finally
        {
            lock (_gate)
            {
                _connection = null;
                _reader = null;
                _writer = null;
            }

            try
            {
                client.Dispose();
            }
            catch
            {
                // 忽略清理异常。
            }
        }
    }

    private async Task<WatchdogProtocol.Response?> SendAsync(
        string task,
        CancellationToken cancellationToken,
        WatchdogProtocol.Request? payload = null)
    {
        NamedPipeClientStream? connection;
        StreamWriter? writer;
        TaskCompletionSource<WatchdogProtocol.Response> pending;
        lock (_gate)
        {
            connection = _connection;
            writer = _writer;
            if (connection is not { IsConnected: true } || writer is null)
            {
                return null;
            }

            if (_pending.Count >= 32)
            {
                return null;   // 防御：异常堆积的响应序列视为坏链
            }

            pending = new TaskCompletionSource<WatchdogProtocol.Response>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(pending);
        }

        try
        {
            var request = payload ?? new WatchdogProtocol.Request { T = task };
            request.T = task;
            var line = JsonSerializer.Serialize(request, WatchdogProtocol.Json);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(10));
            return await pending.Task.WaitAsync(timeoutCancellation.Token);
        }
        catch
        {
            return null;
        }
    }

    // ---------- 台账操作（全部 no-op 降级） ----------

    public async Task<bool> RegisterAsync(
        string instanceId,
        string name,
        string dshHome,
        string? rootPath,
        int processId,
        int port,
        string webUrl,
        string? authenticatedWebUrl,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            WatchdogProtocol.TaskRegister,
            cancellationToken,
            new WatchdogProtocol.Request
            {
                T = WatchdogProtocol.TaskRegister,
                Instance = new WatchdogInstanceDto
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
                }
            });
        return response?.T == "ok";
    }

    public async Task<bool> UnregisterAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            WatchdogProtocol.TaskUnregister,
            cancellationToken,
            new WatchdogProtocol.Request
            {
                T = WatchdogProtocol.TaskUnregister,
                InstanceId = instanceId
            });
        return response?.T == "ok";
    }

    public async Task<IReadOnlyList<WatchdogInstanceDto>> SnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(WatchdogProtocol.TaskSnapshot, cancellationToken);
        return response?.Instances is null
            ? Array.Empty<WatchdogInstanceDto>()
            : response.Instances;
    }

    public async Task<int> CleanupAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            WatchdogProtocol.TaskCleanup,
            cancellationToken,
            new WatchdogProtocol.Request
            {
                T = WatchdogProtocol.TaskCleanup,
                InstanceId = instanceId
            });
        return response?.Cleaned ?? 0;
    }

    /// <summary>
    /// 关闭 Watchdog：发 shutdown（它自检收尾后退出）→ 等进程退出 → 超时兜底强杀。
    /// 保证永远不会留下不受 Launcher 控制的常驻进程。
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await SendAsync(WatchdogProtocol.TaskShutdown, cancellationToken);
        }
        catch
        {
            // 管道可能已断：进程侧会自行检测 Launcher 消失并收尾。
        }

        try
        {
            var process = _process;
            if (process is not null)
            {
                var completed = await Task.WhenAny(
                    process.WaitForExitAsync(cancellationToken),
                    Task.Delay(ShutdownWaitTimeout, cancellationToken));
                if (completed != process.WaitForExitAsync(cancellationToken))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // 已退出。
                    }
                }
            }
        }
        catch
        {
            // 超时/取消都不阻塞 Launcher 退出。
        }

        lock (_gate)
        {
            _connection = null;
            _reader = null;
            _writer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
            _process?.Dispose();
            _process = null;
        }
    }
}
