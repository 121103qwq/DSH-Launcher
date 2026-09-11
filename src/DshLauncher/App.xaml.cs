using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DshLauncher.Services;
using DshLauncher.Watchdog;

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
        // 验证 harness 专用开关：只加载资源与窗口，不执行单实例互斥/代理/激活管道，
        // 避免验证进程被当成第二实例而自杀（生产环境不会设置该变量）。
        if (string.Equals(
                Environment.GetEnvironmentVariable("DSH_LAUNCHER_VERIFY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }
        // 诊断模式：只导出脱敏诊断包后退出，不创建任何窗口（借鉴 Ruler4396 的 --diagnose，MIT）。
        if (e.Args.Any(argument => string.Equals(argument, "--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(DshLauncher.Services.DiagnoseExportService.RunFromCommandLine(e.Args));
            return;
        }

        // 实例锁是进程级文件句柄：两个 Launcher 同时运行时，第二个只能以只读
        // Attached 连接实例，Stop/Restart 会不可用。因此限制单实例，再次启动时
        // 唤起已有窗口。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
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
        ApplyLauncherSettings();
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        _activationChannel = new SingleInstanceActivationChannel(
            GetActivationPipeName(),
            ActivateLauncherFromBackgroundThread);
        _activationChannel.Start();
        Dispatcher.BeginInvoke(
            () =>
            {
                _startupWindowCreationCompleted = true;
                // 主窗口创建失败时绝不能无窗口驻留（否则再次双击只能唤起一个没有 UI 的进程）：
                // 记日志后直接退出。
                if (MainWindow is null)
                {
                    DshLauncher.Services.LauncherLog.Error(
                        "启动后没有主窗口，已退出以避免无窗口驻留。",
                        DshLauncher.Services.ErrorCodes.E9001);
                    Shutdown(1);
                    return;
                }

                if (_activationPending)
                {
                    TryActivateMainWindow();
                }
            },
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>启动时应用 Launcher 级设置（当前为代理），须在 MainWindow 构造之前执行。</summary>
    private static void ApplyLauncherSettings()
    {
        try
        {
            var settings = new DshLauncher.Services.VersionSettingsService().ReadLauncherSettings();
            DshLauncher.Services.ProxyConfigurator.ApplyGlobal(settings);
        }
        catch
        {
            // 设置不可读时保持直连，不影响启动。
        }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        DshLauncher.Services.LauncherLog.Error("UI 线程未处理异常。", DshLauncher.Services.ErrorCodes.E9001,
            new { exception = e.Exception.ToString() });
        WriteCrashLog(e.Exception);

        // 启动阶段（还没有主窗口）的异常不能吞：继续跑就是无窗口驻留、
        // 再次双击也唤不起界面。写日志后退出，让用户重新启动。
        if (MainWindow is null)
        {
            e.Handled = true;
            try
            {
                System.Windows.MessageBox.Show(
                    $"DSH Launcher 启动失败，即将退出。\n\n{e.Exception.Message}\n\n详情见 %LocalAppData%\\DeepSeek\\launcher\\crash.log",
                    "DSH Launcher 启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // 连提示都弹不出时仍然退出。
            }

            Shutdown(1);
            return;
        }

        // 已有窗口时：UI 线程未处理异常不再直接杀死进程，写入崩溃日志后继续运行，
        // 便于事后定位（例如窗口关闭与异步初始化竞态曾导致整个应用崩溃）。
        e.Handled = true;
    }

    /// <summary>崩溃日志单文件上限；超过就轮转成 <c>crash.log.old</c>（只保留一份）。</summary>
    private const long MaxCrashLogBytes = 2_000_000;

    /// <summary>每个进程最多写多少条崩溃；超过后只留一行说明。</summary>
    private const int MaxCrashEntriesPerProcess = 200;

    /// <summary>同一签名的异常在这个窗口内只记第一条，其余累加计数（防止异常循环写满磁盘）。</summary>
    private static readonly TimeSpan CrashThrottleWindow = TimeSpan.FromSeconds(60);

    private static readonly object CrashLogGate = new();
    private static readonly Dictionary<string, CrashSignatureState> CrashSignatures = new(StringComparer.Ordinal);
    private static int _crashEntriesWritten;
    private static bool _crashCapNoticeWritten;

    private sealed class CrashSignatureState
    {
        public DateTimeOffset WindowStart { get; set; }

        public int Suppressed { get; set; }
    }

    /// <summary>
    /// 写崩溃日志。历史教训（work-log/61）：扫描页模板 TwoWay 绑定到只读属性时，
    /// 每次渲染都抛一次异常、异常处理又继续运行，3 毫秒一轮 → 两天写出 1.2 GB。
    /// 因此这里必须同时有**轮转**、**同类异常节流**和**每进程上限**三重保险。
    /// </summary>
    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            // 与主日志同一目录（可被 DSH_LAUNCHER_LOG_ROOT 覆盖）。
            var logDirectory = LauncherLog.LogDirectory;
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, "crash.log");
            lock (CrashLogGate)
            {
                var now = DateTimeOffset.Now;
                if (_crashEntriesWritten >= MaxCrashEntriesPerProcess)
                {
                    if (_crashCapNoticeWritten)
                    {
                        return;
                    }

                    _crashCapNoticeWritten = true;
                    AppendCrashLog(
                        path,
                        $"[{now:O}] 本进程已记录 {MaxCrashEntriesPerProcess} 条崩溃，后续不再写入（避免异常循环写满磁盘；完整细节见下一条实际异常）。{Environment.NewLine}");
                    return;
                }

                var signature = BuildCrashSignature(exception);
                if (CrashSignatures.TryGetValue(signature, out var state))
                {
                    if (now - state.WindowStart < CrashThrottleWindow)
                    {
                        state.Suppressed++;
                        return;
                    }

                    if (state.Suppressed > 0)
                    {
                        AppendCrashLog(
                            path,
                            $"[{now:O}] 上一条同类异常在 {CrashThrottleWindow.TotalSeconds:F0} 秒内共出现 {state.Suppressed + 1} 次，已折叠。{Environment.NewLine}");
                        state.Suppressed = 0;
                    }

                    state.WindowStart = now;
                }
                else
                {
                    CrashSignatures[signature] = new CrashSignatureState { WindowStart = now };
                }

                AppendCrashLog(path, $"[{now:O}] {exception}{Environment.NewLine}");
                _crashEntriesWritten++;
            }
        }
        catch
        {
            // 日志失败不影响兜底行为。
        }
    }

    private static void AppendCrashLog(string path, string content)
    {
        RotateCrashLogIfNeeded(path);
        File.AppendAllText(path, content);
    }

    private static void RotateCrashLogIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxCrashLogBytes)
        {
            return;
        }

        try
        {
            var oldPath = path + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            File.Move(path, oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 轮转失败不能反过来抛异常：崩溃处理必须继续可用。
        }
    }

    /// <summary>同类异常签名：类型 + 消息前 120 字符 + 栈首行（去掉时间与地址差异）。</summary>
    private static string BuildCrashSignature(Exception exception)
    {
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var message = exception.Message.Length > 120 ? exception.Message[..120] : exception.Message;
        var firstFrame = exception.StackTrace?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault() ?? string.Empty;
        return $"{type}: {message} @ {firstFrame}";
    }

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
