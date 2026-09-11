using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Minimal helper that rebinds a stale Installed instance to a freshly
/// detected DSh runtime without creating a new instance or touching a Source
/// instance. Pure logic so the behavior can be regression-tested.
/// </summary>
public static class InstanceRuntimeRebinder
{
    public static ManagerInstance? RebindInstalledInstance(ManagerInstance instance, DshRuntimeInfo detected)
        => RebindInstalledInstance(instance, detected, force: false);

    /// <summary>
    /// 把 Installed 实例重绑定到指定运行时。<paramref name="force"/> = false 时只在当前绑定已
    /// 失效时重绑（历史行为，用于“修复”）；true 时用于显式“更换运行版本”（升级/降级）。
    /// </summary>
    public static ManagerInstance? RebindInstalledInstance(
        ManagerInstance instance,
        DshRuntimeInfo detected,
        bool force)
    {
        if (instance.Kind != InstanceKind.Installed
            || instance.RuntimeStatus == InstanceRuntimeStatus.Running
            || instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached
            || !detected.IsAvailable
            || string.IsNullOrWhiteSpace(detected.PackageRoot)
            || !DshRuntimeCommandFactory.IsUsable(detected.EffectiveLaunchSpec))
        {
            return null;
        }

        if (!force)
        {
            var rootValid = DshRuntimeDetector.TryResolvePackageRoot(instance.RootPath) is not null;
            var exeValid = DshRuntimeCommandFactory.IsUsable(instance.EffectiveDshLaunchSpec);
            if (rootValid && exeValid)
            {
                return null;
            }
        }

        return instance with
        {
            RootPath = detected.PackageRoot,
            DshExecutablePath = detected.ExecutablePath,
            DshLaunchSpec = detected.EffectiveLaunchSpec,
            DetectedVersion = detected.Version,
            PackageManager = "npm",
            RuntimeStatus = InstanceRuntimeStatus.Ready,
            RuntimeOwnership = InstanceRuntimeOwnership.None,
            LastError = null,
            ProcessId = null,
            Port = null,
            WebUrl = null,
            AuthenticatedWebUrl = null
        };
    }
}
