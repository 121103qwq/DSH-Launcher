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
    {
        if (instance.Kind != InstanceKind.Installed
            || !detected.IsAvailable
            || string.IsNullOrWhiteSpace(detected.PackageRoot)
            || string.IsNullOrWhiteSpace(detected.ExecutablePath))
        {
            return null;
        }

        var rootValid = DshRuntimeDetector.TryResolvePackageRoot(instance.RootPath) is not null;
        var exeValid = !string.IsNullOrWhiteSpace(instance.DshExecutablePath)
            && File.Exists(instance.DshExecutablePath);
        if (rootValid && exeValid)
        {
            return null;
        }

        return instance with
        {
            RootPath = detected.PackageRoot,
            DshExecutablePath = detected.ExecutablePath,
            DetectedVersion = detected.Version,
            RuntimeStatus = InstanceRuntimeStatus.Ready,
            LastError = null
        };
    }
}
