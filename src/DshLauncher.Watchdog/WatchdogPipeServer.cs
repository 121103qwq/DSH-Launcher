using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Watchdog;

/// <summary>
/// 命名管道服务端：接受 Launcher 的连接，JSON 行协议。
/// 每条请求一行，一行应答；事件帧在连接存续期间随时可被推送（广播给全部活动连接）。
/// shutdown 请求处理完毕后由 Core 完成收尾并退出进程。
/// </summary>
public sealed class WatchdogPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly WatchdogCore _core;
    private readonly WatchdogLog _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<NamedPipeServerStream> _activeConnections = new();
    private readonly object _connectionsLock = new();

    public WatchdogPipeServer(string pipeName, WatchdogCore core, WatchdogLog log)
    {
        _pipeName = pipeName;
        _core = core;
        _log = log;
    }

    public void Start()
    {
        _core.Subscribe(EventSink);
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cancellation.Token);
                RegisterConnection(server);
                // 所有权移交给连接任务（由它负责 Dispose）；本循环不再释放。
                // 闭包陷阱：lambda 捕获变量而非值，必须先拷贝到局部再启动。
                var accepted = server;
                _ = Task.Run(() => ServeConnectionAsync(accepted));
                server = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // maxNumberOfServerInstances=1 时：已有活动连接期间再建实例会立即
                // 抛“所有管道实例均忙”。绝不能忙循环重试（曾导致 watchdog 占满
                // 一个核）——退避半秒再等下一个连接。
                try
                {
                    await Task.Delay(500, _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _coreLog(ex.ToString());
                try
                {
                    await Task.Delay(500, _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private void RegisterConnection(NamedPipeServerStream server)
    {
        lock (_connectionsLock)
        {
            _activeConnections.Add(server);
        }
    }

    private void UnregisterConnection(NamedPipeServerStream server)
    {
        lock (_connectionsLock)
        {
            _activeConnections.Remove(server);
        }
    }

    private void EventSink(WatchdogProtocol.Response payload)
    {
        lock (_connectionsLock)
        {
            foreach (var connection in _activeConnections.ToArray())
            {
                _ = WriteResponseBestEffortAsync(connection, payload);
            }
        }
    }

    private static async Task WriteResponseBestEffortAsync(
        NamedPipeServerStream connection,
        WatchdogProtocol.Response response)
    {
        try
        {
            var line = JsonSerializer.Serialize(response, WatchdogProtocol.Json) + "\n";
            await connection.WriteAsync(Encoding.UTF8.GetBytes(line));
            await connection.FlushAsync();
        }
        catch
        {
            // 事件丢失可接受：Launcher 通过周期 snapshot 收敛。
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream connection)
    {
        try
        {
            using var reader = new StreamReader(connection, Encoding.UTF8);
            while (!_cancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cancellation.Token);
                if (line is null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WatchdogProtocol.Request? request;
                try
                {
                    request = JsonSerializer.Deserialize<WatchdogProtocol.Request>(line, WatchdogProtocol.Json);
                }
                catch (JsonException)
                {
                    await WriteResponseAsync(connection, new WatchdogProtocol.Response
                    {
                        T = "error",
                        Message = "malformed request"
                    });
                    continue;
                }

                if (request is null)
                {
                    continue;
                }

                var response = Handle(request);
                await WriteResponseAsync(connection, response);
                if (request.T == WatchdogProtocol.TaskShutdown)
                {
                    // 收尾在请求应答后再执行（应答先落定，Launcher 能收到确认）。
                    try
                    {
                        _core.StopAllAndCleanup();
                        _cancellation.Cancel();
                        Environment.Exit(0);
                    }
                    catch
                    {
                        Environment.Exit(0);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"pipe: serving connection failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            UnregisterConnection(connection);
            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // 忽略清理异常。
            }
        }
    }

    private WatchdogProtocol.Response Handle(WatchdogProtocol.Request request)
    {
        switch (request.T)
        {
            case WatchdogProtocol.TaskPing:
                return new WatchdogProtocol.Response { T = "ok", Message = "pong" };

            case WatchdogProtocol.TaskRegister:
                if (request.Instance is null)
                {
                    return new WatchdogProtocol.Response { T = "error", Message = "register requires instance" };
                }

                _core.RegisterInstance(request.Instance);
                return new WatchdogProtocol.Response { T = "ok" };

            case WatchdogProtocol.TaskUnregister:
                if (string.IsNullOrWhiteSpace(request.InstanceId))
                {
                    return new WatchdogProtocol.Response { T = "error", Message = "unregister requires instanceId" };
                }

                _core.UnregisterInstance(request.InstanceId);
                return new WatchdogProtocol.Response { T = "ok" };

            case WatchdogProtocol.TaskSnapshot:
                return new WatchdogProtocol.Response
                {
                    T = "snapshot",
                    Instances = _core.Snapshot().ToList()
                };

            case WatchdogProtocol.TaskCleanup:
                if (string.IsNullOrWhiteSpace(request.InstanceId))
                {
                    return new WatchdogProtocol.Response { T = "error", Message = "cleanup requires instanceId" };
                }

                return new WatchdogProtocol.Response
                {
                    T = "ok",
                    Cleaned = _core.CleanupInstance(request.InstanceId)
                };

            case WatchdogProtocol.TaskShutdown:
                return new WatchdogProtocol.Response { T = "ok", Message = "shutdown confirmed" };

            default:
                return new WatchdogProtocol.Response { T = "error", Message = $"unknown task: {request.T}" };
        }
    }

    private static async Task WriteResponseAsync(
        NamedPipeServerStream connection,
        WatchdogProtocol.Response response)
    {
        var line = JsonSerializer.Serialize(response, WatchdogProtocol.Json) + "\n";
        await connection.WriteAsync(Encoding.UTF8.GetBytes(line));
        await connection.FlushAsync();
    }

    private void _coreLog(string message)
    {
        // Core 之外仅有连接层错误需要留痕；复用全局日志由 Program 注入的 Core 自带。
        Console.Error.WriteLine($"[watchdog-pipe] {message}");
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        lock (_connectionsLock)
        {
            foreach (var connection in _activeConnections)
            {
                try
                {
                    connection.Dispose();
                }
                catch
                {
                    // 逐个断开。
                }
            }

            _activeConnections.Clear();
        }

        _cancellation.Dispose();
        await Task.CompletedTask;
    }
}
