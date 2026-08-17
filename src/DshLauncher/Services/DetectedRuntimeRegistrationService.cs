using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record DetectedRuntimeRegistrationResult(
    IReadOnlyList<ManagerInstance> AddedInstances,
    IReadOnlyList<string> Errors);

public sealed class DetectedRuntimeRegistrationService
{
    private readonly InstanceRegistry _registry;

    public DetectedRuntimeRegistrationService(InstanceRegistry registry)
    {
        _registry = registry;
    }

    public DetectedRuntimeRegistrationResult RegisterMissing(
        IReadOnlyCollection<ManagerInstance> existingInstances,
        IReadOnlyCollection<DshRuntimeInfo> detectedRuntimes)
    {
        var added = new List<ManagerInstance>();
        var errors = new List<string>();
        var registeredRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(
            existingInstances.Select(static instance => instance.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var instance in existingInstances.Where(static instance => instance.Kind == InstanceKind.Installed))
        {
            var root = TryNormalizeRuntimeRoot(instance.RootPath);
            if (root is not null)
            {
                registeredRoots.Add(root);
            }
        }

        foreach (var runtime in detectedRuntimes.Where(static runtime => runtime.IsAvailable))
        {
            var packageRoot = TryNormalizeRuntimeRoot(runtime.PackageRoot);
            if (packageRoot is null
                || !DshRuntimeCommandFactory.IsUsable(runtime.EffectiveLaunchSpec))
            {
                errors.Add($"{runtime.DisplayVersionText} 的安装目录或启动文件无效，未自动添加。");
                continue;
            }

            if (!registeredRoots.Add(packageRoot))
            {
                continue;
            }

            var name = CreateUniqueName(runtime.SuggestedInstanceName, usedNames);
            try
            {
                var instance = _registry.Register(
                    name,
                    packageRoot,
                    InstanceKind.Installed,
                    runtime.ExecutablePath,
                    runtime.Version,
                    "npm",
                    dshLaunchSpec: runtime.EffectiveLaunchSpec);
                added.Add(instance);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                registeredRoots.Remove(packageRoot);
                errors.Add($"{runtime.DisplayVersionText} 自动添加失败：{ex.Message}");
            }
        }

        return new DetectedRuntimeRegistrationResult(added, errors);
    }

    private static string? TryNormalizeRuntimeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var resolved = DshRuntimeDetector.TryResolvePackageRoot(path) ?? path;
            var normalized = Path.GetFullPath(resolved)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string CreateUniqueName(string baseName, ISet<string> usedNames)
    {
        if (usedNames.Add(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} ({suffix})";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
