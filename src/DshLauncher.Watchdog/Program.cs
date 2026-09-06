using System.Diagnostics;
using DshLauncher.Watchdog;

// ============================================================================
// DSH Launcher Watchdog —— 启动入口
//
// 启动方式（由 Launcher 拉起）：
//   DSH Launcher.Watchdog.exe --launcher-pid <pid>
//
// 生命周期契约（与主程序一致）：
//   - 由 Launcher 作为子进程启动；单会话单实例（Mutex + 命名管道互斥）；
//   - Launcher 存活：周期探测实例、识别并转正市场自重启的幽灵实例、清理残留；
//   - Launcher 退出（正常关闭或崩溃）：停止全部受托实例 + 清理残留 → 清台账 → 自退。
// ============================================================================

var launcherPid = ParseLauncherPid(args);
var sessionId = Process.GetCurrentProcess().SessionId;
var stateDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DeepSeek", "launcher");
var log = new WatchdogLog(stateDirectory);

// 单会话单实例：已有 Watchdog 在跑就直接退出（Launcher 会连上旧管道复用）。
var mutexName = $@"Local\DSH-Launcher-Watchdog-SingleInstance-{sessionId}";
using var singleInstance = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
if (!createdNew)
{
    log.Info("another watchdog is already running for this session; exiting");
    return 0;
}

log.Info($"watchdog process starting: launcher-pid={launcherPid}, session={sessionId}");

var core = new WatchdogCore(stateDirectory, launcherPid, log);
var pipeName = WatchdogProtocol.GetLauncherPipeName(sessionId);
await using var pipeServer = new WatchdogPipeServer(pipeName, core, log);
pipeServer.Start();

try
{
    await core.RunLoopAsync(CancellationToken.None);
}
catch (Exception ex)
{
    log.Error($"watchdog run loop terminated unexpectedly: {ex}");
    core.StopAllAndCleanup();
}

log.Info("watchdog exiting");
return 0;

static int ParseLauncherPid(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--launcher-pid" && int.TryParse(args[i + 1], out var pid) && pid > 0)
        {
            return pid;
        }
    }

    return 0;   // 0 = 无法确证 Launcher 存活；RunLoop 立即按"Launcher 消失"收尾退出。
}
