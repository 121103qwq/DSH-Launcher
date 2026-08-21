using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        // Register the fail-closed startup handler before WPF materializes StartupUri.
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        // The instance lock is process-wide. A second Launcher must activate the
        // existing window instead of managing the same DSH_HOME concurrently.
        bool createdNew;
        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                SingleInstanceMutexName,
                out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // A higher-integrity Launcher may own the mutex. Do not request
            // elevation and do not start a second manager; try the existing
            // activation paths, then explain the conflict if they are blocked.
            var activated = RequestExistingLauncherActivation()
                == SingleInstanceActivationResult.Accepted
                || ActivateExistingLauncherByWindowHandle();
            if (!activated)
            {
                MessageBox.Show(
                    "检测到另一个可能以管理员权限运行的 DSH Launcher。\n\n"
                    + "请先关闭已有 Launcher，再以普通方式重新打开。",
                    "DSH Launcher 已在运行",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            ExitSecondaryInstance();
            return;
        }

        if (!createdNew)
        {
            var activation = RequestExistingLauncherActivation();
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
        _activationChannel = new SingleInstanceActivationChannel(
            GetActivationPipeName(),
            ActivateLauncherFromBackgroundThread);
        _activationChannel.Start();
        Dispatcher.BeginInvoke(
            () =>
            {
                _startupWindowCreationCompleted = MainWindow is DshLauncher.MainWindow;
                if (!_startupWindowCreationCompleted)
                {
                    Shutdown(-1);
                    return;
                }

                if (_activationPending)
                {
                    TryActivateMainWindow();
                }
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);

        if (!_startupWindowCreationCompleted)
        {
            // Startup must fail closed. Continuing after a MainWindow constructor
            // or Loaded failure can expose a half-initialized visual tree whose
            // bindings and visibility state never became valid.
            e.Handled = true;
            try
            {
                MainWindow?.Close();
            }
            catch
            {
                // The window may already be in a failed construction state.
            }

            Shutdown(-1);
            return;
        }

        // Preserve the existing post-startup resilience for non-fatal UI races.
        e.Handled = true;
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeek",
                "launcher");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never replace the original failure path.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
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
        // OnStartup has not necessarily called base yet. Application.Shutdown at
        // this point can leave a windowless WPF process behind, so end this
        // uninitialized secondary process directly.
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        Environment.Exit(0);
    }

    private static SingleInstanceActivationResult RequestExistingLauncherActivation()
    {
        try
        {
            return SingleInstanceActivationChannel.RequestActivationAsync(
                    GetActivationPipeName(),
                    ActivationRequestTimeout)
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
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool ActivateLauncherFromBackgroundThread()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            var operation = Dispatcher.InvokeAsync(TryActivateMainWindow, DispatcherPriority.Send);
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

    private bool TryActivateMainWindow()
    {
        var window = MainWindow;
        if (window is null)
        {
            if (!_startupWindowCreationCompleted)
            {
                _activationPending = true;
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
