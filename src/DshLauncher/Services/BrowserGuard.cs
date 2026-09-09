using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

/// <summary>
/// 浏览器守卫（借鉴 MarcoG-h/DSH-Launcher 的 browser-guard.ts，思路借鉴）。
///
/// 背景：Launcher 一律传 --no-open 并自行打开窗口；但对不支持该开关的旧版 dsh，
/// dsh 仍可能弹出系统浏览器。守卫在实例就绪后的一小段窗口内高频检查浏览器进程，
/// 只结束命令行里带该实例端口（127.0.0.1:port / localhost:port）的进程；
/// 用户手动打开的浏览器（端口相同但不在守卫窗口内打开）不会被误杀，
/// 因为守卫只在启动后 30 秒内工作。
/// </summary>
public sealed class BrowserGuard : IDisposable
{
    private static readonly Regex BrowserList = new(
        @"^(msedge|chrome|firefox|brave|vivaldi|opera|chromium)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly List<CancellationTokenSource> _watches = new();
    private bool _disposed;

    /// <summary>对指定端口开启守卫窗口；返回可提前取消的句柄。</summary>
    public IDisposable Watch(int port, TimeSpan? duration = null, Action<string>? trace = null)
    {
        var cancellation = new CancellationTokenSource();
        if (_disposed)
        {
            cancellation.Dispose();
            return new EmptyHandle();
        }

        lock (_watches)
        {
            _watches.Add(cancellation);
        }

        var window = duration ?? TimeSpan.FromSeconds(30);
        _ = Task.Run(() => RunAsync(port, window, cancellation, trace));
        return new WatchHandle(this, cancellation);
    }

    private async Task RunAsync(int port, TimeSpan window, CancellationTokenSource cancellation, Action<string>? trace)
    {
        var deadline = DateTimeOffset.UtcNow + window;
        var reportedFailure = false;
        try
        {
            while (!cancellation.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var killed = await SweepAsync(port, cancellation.Token);
                    if (killed > 0)
                    {
                        LauncherLog.Info("浏览器守卫结束了打开实例端口的浏览器进程。", ErrorCodes.E4001,
                            new { port, killed });
                        trace?.Invoke($"已关闭 {killed} 个自动弹出的浏览器进程（端口 {port}）。");
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                {
                    if (!reportedFailure)
                    {
                        reportedFailure = true;
                        LauncherLog.Warn("浏览器守卫执行失败（不影响实例运行）。", ErrorCodes.E4001,
                            new { port, error = ex.Message });
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            lock (_watches)
            {
                _watches.Remove(cancellation);
            }

            cancellation.Dispose();
        }
    }

    private static async Task<int> SweepAsync(int port, CancellationToken cancellationToken)
    {
        const string script =
            "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe' or Name='chrome.exe' or Name='firefox.exe' or Name='brave.exe' or Name='vivaldi.exe' or Name='opera.exe' or Name='chromium.exe'\" | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 0;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return 0;
        }

        var portPattern = new Regex($@"(?:127\.0\.0\.1|localhost|\[::1\]):{port}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var killed = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var separator = line.IndexOf('\t');
            if (separator <= 0)
            {
                continue;
            }

            var pidText = line[..separator].Trim();
            var commandLine = line[(separator + 1)..];
            if (!int.TryParse(pidText, out var pid)
                || pid <= 0
                || pid == Environment.ProcessId
                || !portPattern.IsMatch(commandLine))
            {
                continue;
            }

            if (TryKill(pid))
            {
                killed++;
            }
        }

        return killed;
    }

    private static bool TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName + ".exe";
            if (!BrowserList.IsMatch(name))
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_watches)
        {
            foreach (var watch in _watches)
            {
                try
                {
                    watch.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 已结束的守卫无需处理。
                }
            }

            _watches.Clear();
        }
    }

    private void Remove(CancellationTokenSource cancellation)
    {
        lock (_watches)
        {
            _watches.Remove(cancellation);
        }

        cancellation.Dispose();
    }

    private sealed class WatchHandle : IDisposable
    {
        private readonly BrowserGuard _owner;
        private readonly CancellationTokenSource _cancellation;

        public WatchHandle(BrowserGuard owner, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
        }

        public void Dispose() => _owner.Remove(_cancellation);
    }

    private sealed class EmptyHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
