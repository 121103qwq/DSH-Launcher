using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DshLauncher.Services;
using DshLauncher.Models;

namespace DshLauncher;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\DSH-Launcher-SingleInstance";
    private static readonly TimeSpan ActivationRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClosingInstanceWaitTimeout = TimeSpan.FromSeconds(8);
    private Mutex? _singleInstanceMutex;
    private SingleInstanceActivationChannel? _activationChannel;
    private bool _ownsSingleInstanceMutex;
    private bool _startupWindowCreationCompleted;
    private bool _activationPending;
    private LauncherCommand? _startupCommand;
    private LauncherCommand? _pendingActivationCommand;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() == LauncherUpdateService.ApplyModeArgument)
        {
            var source = Environment.ProcessPath;
            var exitCode = source is not null
                && LauncherUpdateService.TryParseApplyArguments(e.Args, out var request)
                && request is not null
                    ? LauncherUpdateService.ApplyUpdateAndRestart(request, source)
                    : 2;
            Environment.Exit(exitCode);
            return;
        }

        _ = LauncherCommandParser.TryParse(e.Args, out _startupCommand);
        var activationPayload = _startupCommand is null
            ? null
            : JsonSerializer.Serialize(_startupCommand);

        // 实例锁是进程级文件句柄：两个 Launcher 同时运行时，第二个只能以只读
        // Attached 连接实例，Stop/Restart 会不可用。因此限制单实例，再次启动时
        // 唤起已有窗口。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            var activation = RequestExistingLauncherActivation(activationPayload);
            var activated = activation == SingleInstanceActivationResult.Accepted
                || (activation == SingleInstanceActivationResult.Unavailable
                    && ActivateExistingLauncherByWindowHandle());
            if (activated || !TryTakeOverClosingInstance())
            {
                ExitSecondaryInstance();
                return;
            }
        }

        _ownsSingleInstanceMutex = true;
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        _activationChannel = new SingleInstanceActivationChannel(
            GetActivationPipeName(),
            ActivateLauncherFromBackgroundThread);
        _activationChannel.Start();
        Dispatcher.BeginInvoke(
            () =>
            {
                _startupWindowCreationCompleted = true;
                var command = _pendingActivationCommand ?? _startupCommand;
                _pendingActivationCommand = null;
                _startupCommand = null;
                if (_activationPending || command is not null)
                {
                    TryActivateMainWindow(command);
                }
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 已经显示主窗口时保留原有降级行为；启动或关闭阶段没有可用窗口时
        // 不能吞掉异常，否则进程会占着单实例锁留在后台，用户再次双击也无法打开。
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeek", "launcher");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响兜底行为。
        }

        var mainWindowLoaded = MainWindow?.IsLoaded == true;
        var shutdownStarted = Dispatcher.HasShutdownStarted
            || Dispatcher.HasShutdownFinished
            || MainWindow is MainWindow { IsShutdownInProgress: true };
        e.Handled = ShouldHandleDispatcherException(mainWindowLoaded, shutdownStarted);
    }

    internal static bool ShouldHandleDispatcherException(bool mainWindowLoaded, bool shutdownStarted) =>
        mainWindowLoaded && !shutdownStarted;

    protected override void OnExit(ExitEventArgs e)
    {
        _activationChannel?.Dispose();
        _activationChannel = null;
        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already leaving; a missing ownership state needs no recovery.
            }
        }

        _ownsSingleInstanceMutex = false;
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static string GetActivationPipeName() =>
        $"DSH-Launcher-Activation-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    private void ExitSecondaryInstance()
    {
        // OnStartup has not called base yet. Application.Shutdown at this point can leave
        // a windowless WPF process behind, so end this uninitialized secondary process directly.
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        Environment.Exit(0);
    }

    private static SingleInstanceActivationResult RequestExistingLauncherActivation(string? payload)
    {
        try
        {
            return SingleInstanceActivationChannel.RequestActivationAsync(
                    GetActivationPipeName(),
                    ActivationRequestTimeout,
                    payload)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return SingleInstanceActivationResult.Unavailable;
        }
    }

    private bool TryTakeOverClosingInstance()
    {
        try
        {
            return _singleInstanceMutex?.WaitOne(ClosingInstanceWaitTimeout) == true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private bool ActivateLauncherFromBackgroundThread(string? payload)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            LauncherCommand? command = null;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    command = JsonSerializer.Deserialize<LauncherCommand>(payload);
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            var operation = Dispatcher.InvokeAsync(
                () => TryActivateMainWindow(command),
                DispatcherPriority.Send);
            return operation.Task.Wait(ActivationRequestTimeout)
                && operation.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TaskCanceledException
                                   or InvalidOperationException
                                   or ObjectDisposedException)
        {
            return false;
        }
    }

    private bool TryActivateMainWindow(LauncherCommand? command = null)
    {
        var window = MainWindow;
        if (window is null)
        {
            if (!_startupWindowCreationCompleted)
            {
                _activationPending = true;
                _pendingActivationCommand = command ?? _pendingActivationCommand;
                return true;
            }

            Shutdown();
            return false;
        }

        if (window is MainWindow { IsShutdownInProgress: true })
        {
            return false;
        }

        try
        {
            _activationPending = false;
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }

            window.Activate();
            window.Focus();
            if (command is not null && window is MainWindow mainWindow)
            {
                mainWindow.HandleLauncherCommand(command);
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            // A closed MainWindow means this process is only a shutdown remnant.
            Shutdown();
            return false;
        }
    }

    private static bool ActivateExistingLauncherByWindowHandle()
    {
        try
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    ShowWindow(process.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
            }
        }
        catch
        {
            // Older Launcher builds do not expose the activation pipe.
        }

        return false;
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
