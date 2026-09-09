using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record ScannedHomeImportOutcome(
    ManagerInstance? Instance,
    bool Skipped,
    string? Error)
{
    public static ScannedHomeImportOutcome AlreadyImported() => new(null, true, null);

    public static ScannedHomeImportOutcome Failed(string error) => new(null, false, error);
}

/// <summary>
/// 把扫描到的本机 home 登记成实例（work-log/50）：一个 home → 一个实例，
/// 只认 <c>profiles/web</c>（启动器正常模式跑 <c>dsh web</c>）。文件复制复用
/// <see cref="DshHomeImportService"/>，登记复用 <see cref="InstanceRegistry"/>，
/// 失败时回滚注册，避免留下半成品实例。
/// </summary>
public sealed class ScannedHomeImportService
{
    private readonly InstanceRegistry _registry;
    private readonly DshHomeImportService _homeImporter;

    public ScannedHomeImportService(
        InstanceRegistry registry,
        DshHomeImportService? homeImporter = null)
    {
        _registry = registry;
        _homeImporter = homeImporter ?? new DshHomeImportService();
    }

    public async Task<ScannedHomeImportOutcome> ImportAsync(
        ScannedDshHome home,
        ManagerInstance template,
        IReadOnlyCollection<ManagerInstance> existingInstances,
        CancellationToken cancellationToken = default)
    {
        if (existingInstances.Any(instance =>
                IsSameSourceHome(instance.ImportedFromDshHome, home.Path)))
        {
            return ScannedHomeImportOutcome.AlreadyImported();
        }

        if (!home.Profiles.Any(static profile =>
                string.Equals(profile.Name, "web", StringComparison.OrdinalIgnoreCase)))
        {
            return ScannedHomeImportOutcome.Failed(
                $"{DisplayName(home)} 没有 profiles/web，启动器正常模式无法启动（TUI profile 暂不支持）。");
        }

        if (template.Kind != InstanceKind.Installed
            || !DshRuntimeCommandFactory.IsUsable(template.EffectiveDshLaunchSpec))
        {
            return ScannedHomeImportOutcome.Failed("没有可用的 DSh 运行版本，无法导入。");
        }

        var name = CreateUniqueName(DisplayName(home), existingInstances);
        ManagerInstance? instance = null;
        try
        {
            instance = _registry.Register(
                name,
                template.RootPath,
                InstanceKind.Installed,
                template.DshExecutablePath,
                template.DetectedVersion,
                template.PackageManager,
                dshLaunchSpec: template.EffectiveDshLaunchSpec);
            var import = await _homeImporter.ImportAsync(
                home.Path,
                instance.DshHome,
                cancellationToken);
            if (!import.Imported)
            {
                RollBack(instance);
                return ScannedHomeImportOutcome.Failed($"{DisplayName(home)} 中没有可导入的数据。");
            }

            return new ScannedHomeImportOutcome(
                _registry.Update(instance with { ImportedFromDshHome = import.SourceHome ?? home.Path }),
                false,
                null);
        }
        catch (OperationCanceledException)
        {
            RollBack(instance);
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            RollBack(instance);
            return ScannedHomeImportOutcome.Failed($"{DisplayName(home)} 导入失败：{ex.Message}");
        }
    }

    /// <summary>显示名：取目录名（.dsh-dev）。</summary>
    internal static string DisplayName(ScannedDshHome home)
    {
        var folder = home.Path
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(folder);
        return string.IsNullOrWhiteSpace(name) ? home.Path : name;
    }

    internal static bool IsSameSourceHome(string? importedFrom, string candidate) =>
        !string.IsNullOrWhiteSpace(importedFrom)
        && string.Equals(
            DshEnvironmentScanner.NormalizeForCompare(importedFrom),
            DshEnvironmentScanner.NormalizeForCompare(candidate),
            StringComparison.Ordinal);

    private static string CreateUniqueName(
        string preferred,
        IReadOnlyCollection<ManagerInstance> existingInstances)
    {
        var used = new HashSet<string>(
            existingInstances.Select(static instance => instance.Name),
            StringComparer.OrdinalIgnoreCase);
        if (used.Add(preferred))
        {
            return preferred;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{preferred} {index}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        return $"{preferred} {Guid.NewGuid():N}";
    }

    private void RollBack(ManagerInstance? instance)
    {
        if (instance is null)
        {
            return;
        }

        try
        {
            _registry.Unregister(instance.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 回滚尽力而为；残留空实例由用户在实例管理中删除。
        }

        try
        {
            var directory = Path.GetDirectoryName(instance.DshHome);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 同上：目录残留不影响正确性。
        }
    }
}
