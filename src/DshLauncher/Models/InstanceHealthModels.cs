using DshLauncher.Services;
using DshLauncher.Watchdog;

namespace DshLauncher.Models;

/// <summary>
/// "运行状况"页的数据提供者（由 MainWindow 注入，避免窗口直接依赖 Watchdog/Runner）。
/// </summary>
public sealed record InstanceHealthProviders(
    Func<ManagerInstance, InstanceResourceSnapshot?> CurrentResource,
    Func<ManagerInstance, IReadOnlyList<InstanceResourceSnapshot>> ResourceHistory,
    Func<ManagerInstance, IReadOnlyList<ProcessResourceLine>> Processes,
    Func<ManagerInstance, IReadOnlyList<InstanceLogLine>> Logs,
    Action<ManagerInstance>? ClearLogs = null,
    Func<ManagerInstance, int>? CleanupProcesses = null,
    Func<ManagerInstance, IReadOnlyList<StartupEvidence>>? StartupEvidence = null,
    Func<ManagerInstance, InstanceActivity?>? LastActivity = null,
    Action<ManagerInstance>? ClearStartupEvidence = null,
    Func<ManagerInstance, CrashRecoveryStatus>? CrashStatus = null,
    Func<ManagerInstance, IReadOnlyList<CrashRecord>>? CrashRecords = null,
    Action<ManagerInstance>? ClearCrashCooldown = null,
    Func<ManagerInstance, IReadOnlyList<string>>? ThirdPartyPlugins = null,
    Func<ManagerInstance, bool>? IsInstanceRunning = null,
    Func<ManagerInstance, CancellationToken, IProgress<string>, Task<PluginBisectResult>>? RunPluginBisect = null,
    Func<ManagerInstance, string, Task>? DisablePlugin = null);
