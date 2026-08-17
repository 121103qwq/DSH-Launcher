using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record DetectedRuntimeRegistrationResult(
    IReadOnlyList<ManagerInstance> AddedInstances,
    IReadOnlyList<ManagerInstance> ImportedInstances,
    IReadOnlyList<ManagerInstance> UpdatedInstances,
    IReadOnlyList<ManagerInstance> BackfilledInstances,
    IReadOnlyList<string> Errors);

public sealed class DetectedRuntimeRegistrationService
{
    private readonly InstanceRegistry _registry;
    private readonly DshHomeImportService _homeImporter;

    public DetectedRuntimeRegistrationService(
        InstanceRegistry registry,
        DshHomeImportService? homeImporter = null)
    {
        _registry = registry;
        _homeImporter = homeImporter ?? new DshHomeImportService();
    }

    public async Task<DetectedRuntimeRegistrationResult> ImportAsync(
        IReadOnlyCollection<ManagerInstance> existingInstances,
        IReadOnlyCollection<DshRuntimeInfo> detectedRuntimes,
        bool refreshRegisteredRuntimeRoots = false,
        CancellationToken cancellationToken = default)
    {
        var added = new List<ManagerInstance>();
        var imported = new List<ManagerInstance>();
        var updated = new List<ManagerInstance>();
        var backfilled = new List<ManagerInstance>();
        var errors = new List<string>();
        var registeredRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingByRoot = new Dictionary<string, List<ManagerInstance>>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(
            existingInstances.Select(static instance => instance.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var instance in existingInstances.Where(static instance => instance.Kind == InstanceKind.Installed))
        {
            var root = TryNormalizeRuntimeRoot(instance.RootPath);
            if (root is not null)
            {
                registeredRoots.Add(root);
                if (!existingByRoot.TryGetValue(root, out var instances))
                {
                    instances = new List<ManagerInstance>();
                    existingByRoot[root] = instances;
                }

                instances.Add(instance);
            }
        }

        foreach (var runtime in detectedRuntimes.Where(static runtime => runtime.IsAvailable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageRoot = TryNormalizeRuntimeRoot(runtime.PackageRoot);
            if (packageRoot is null
                || !DshRuntimeCommandFactory.IsUsable(runtime.EffectiveLaunchSpec))
            {
                errors.Add($"{runtime.DisplayVersionText} 的安装目录或启动文件无效，未导入。");
                continue;
            }

            if (existingByRoot.TryGetValue(packageRoot, out var matchingInstances))
            {
                var detectedHome = TryNormalizeDirectory(runtime.ExistingDshHome);
                if (detectedHome is null)
                {
                    continue;
                }

                var matchingSource = matchingInstances
                    .Where(instance => string.Equals(
                        TryNormalizeDirectory(instance.ImportedFromDshHome),
                        detectedHome,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(static instance => instance.RecentSortAt)
                    .FirstOrDefault();
                if (matchingSource is not null && !refreshRegisteredRuntimeRoots)
                {
                    continue;
                }

                var existing = matchingSource
                    ?? matchingInstances
                        .OrderByDescending(static instance => instance.RecentSortAt)
                        .First();
                if (existing.RuntimeStatus == InstanceRuntimeStatus.Running)
                {
                    errors.Add($"{existing.Name} 正在运行，不能覆盖同地址实例；请停止后重新导入。");
                    continue;
                }

                try
                {
                    if (matchingSource is null
                        && !refreshRegisteredRuntimeRoots
                        && string.IsNullOrWhiteSpace(existing.ImportedFromDshHome))
                    {
                        var repair = await _homeImporter.BackfillLegacyImportAsync(
                            runtime.ExistingDshHome,
                            existing.DshHome,
                            cancellationToken);
                        if (repair.Imported)
                        {
                            var repaired = UpdateRuntimeBinding(existing, runtime, packageRoot, repair.SourceHome);
                            backfilled.Add(repaired);
                            continue;
                        }
                    }

                    var refresh = await _homeImporter.RefreshImportAsync(
                        runtime.ExistingDshHome,
                        existing.DshHome,
                        cancellationToken);
                    var refreshed = UpdateRuntimeBinding(
                        existing,
                        runtime,
                        packageRoot,
                        refresh.SourceHome ?? detectedHome);
                    updated.Add(refreshed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    errors.Add($"{existing.Name} 覆盖同地址实例失败：{ex.Message}");
                }

                continue;
            }

            if (!registeredRoots.Add(packageRoot))
            {
                continue;
            }

            var name = CreateUniqueName(runtime.SuggestedInstanceName, usedNames);
            ManagerInstance? instance = null;
            try
            {
                instance = _registry.Register(
                    name,
                    packageRoot,
                    InstanceKind.Installed,
                    runtime.ExecutablePath,
                    runtime.Version,
                    "npm",
                    dshLaunchSpec: runtime.EffectiveLaunchSpec);
                var import = await _homeImporter.ImportAsync(
                    runtime.ExistingDshHome,
                    instance.DshHome,
                    cancellationToken);
                added.Add(instance);
                if (import.Imported)
                {
                    instance = _registry.Update(instance with
                    {
                        ImportedFromDshHome = import.SourceHome
                    });
                    added[^1] = instance;
                    imported.Add(instance);
                }
            }
            catch (OperationCanceledException)
            {
                RollBackRegistration(instance);
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                RollBackRegistration(instance);
                registeredRoots.Remove(packageRoot);
                errors.Add($"{runtime.DisplayVersionText} 导入失败：{ex.Message}");
            }
        }

        return new DetectedRuntimeRegistrationResult(added, imported, updated, backfilled, errors);
    }

    private ManagerInstance UpdateRuntimeBinding(
        ManagerInstance existing,
        DshRuntimeInfo runtime,
        string packageRoot,
        string? importedFromDshHome)
    {
        var status = existing.RuntimeStatus is InstanceRuntimeStatus.Stopped or InstanceRuntimeStatus.Ready
            ? existing.RuntimeStatus
            : InstanceRuntimeStatus.Ready;
        return _registry.Update(existing with
        {
            RootPath = packageRoot,
            DshExecutablePath = runtime.ExecutablePath,
            DetectedVersion = runtime.Version,
            RuntimeStatus = status,
            PackageManager = "npm",
            LastError = null,
            DshLaunchSpec = runtime.EffectiveLaunchSpec,
            ImportedFromDshHome = importedFromDshHome
        });
    }

    private void RollBackRegistration(ManagerInstance? instance)
    {
        if (instance is null)
        {
            return;
        }

        try
        {
            _registry.Unregister(instance.Id);
        }
        catch
        {
            // Preserve the original import failure.
        }

        try
        {
            if (Directory.Exists(instance.DshHome)
                && (File.GetAttributes(instance.DshHome) & FileAttributes.ReparsePoint) == 0)
            {
                Directory.Delete(instance.DshHome, recursive: true);
            }
        }
        catch
        {
            // Preserve the original import failure.
        }
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

    private static string? TryNormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
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
