using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record ModelProviderSyncResult(
    int CopiedVersions,
    int SkippedRunningVersions,
    IReadOnlyList<string> Errors,
    bool NoConfigurationSource = false)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Synchronizes only the model-provider data that DSh consumes from
/// settings.yaml. Each version keeps its own DSH_HOME; synchronization is an
/// explicit copy between stopped versions, never a shared file or live write.
/// </summary>
public sealed class ModelProviderSyncService
{
    private readonly VersionSettingsService _settingsService;
    private readonly ModelService _modelService;
    private readonly ProviderStateService _providerStateService;
    private readonly Func<string, bool> _isRunning;

    public ModelProviderSyncService(
        VersionSettingsService settingsService,
        ModelService modelService,
        ProviderStateService providerStateService,
        Func<string, bool>? isRunning = null)
    {
        _settingsService = settingsService;
        _modelService = modelService;
        _providerStateService = providerStateService;
        _isRunning = isRunning ?? (_ => false);
    }

    public ModelProviderSyncResult Synchronize(
        ManagerInstance focus,
        IEnumerable<ManagerInstance> versions)
    {
        var all = NormalizeVersions(versions.Append(focus));
        var component = FindComponent(focus, all);
        var stopped = component.Where(version => !IsRunning(version)).ToArray();
        var skippedRunning = component.Count - stopped.Length;
        if (stopped.Length < 2)
        {
            return new ModelProviderSyncResult(0, skippedRunning, Array.Empty<string>());
        }

        var configurationSource = stopped
            .Select(version => (Version: version, Timestamp: GetSettingsTimestamp(version)))
            .Where(candidate => candidate.Timestamp is not null && HasProviderConfiguration(candidate.Version))
            .OrderByDescending(candidate => candidate.Timestamp)
            .ThenByDescending(candidate => candidate.Version.Id, StringComparer.Ordinal)
            .Select(candidate => candidate.Version)
            .FirstOrDefault();
        var stateSource = stopped
            .Select(version => (Version: version, Timestamp: GetStateTimestamp(version)))
            .Where(candidate => candidate.Timestamp is not null)
            .OrderByDescending(candidate => candidate.Timestamp)
            .ThenByDescending(candidate => candidate.Version.Id, StringComparer.Ordinal)
            .Select(candidate => candidate.Version)
            .FirstOrDefault();
        if (configurationSource is null && stateSource is null)
        {
            // 同步已开启但没有任何版本带有 llm Provider 配置：明确告知调用方，
            // 而不是让“打开同步”看起来毫无作用。
            return new ModelProviderSyncResult(0, skippedRunning, Array.Empty<string>(), NoConfigurationSource: true);
        }

        var sourceStates = stateSource is null ? null : _providerStateService.Read(stateSource);
        var copied = 0;
        var errors = new List<string>();
        foreach (var target in stopped)
        {
            if (string.Equals(configurationSource?.Id, target.Id, StringComparison.Ordinal)
                && string.Equals(stateSource?.Id, target.Id, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (configurationSource is not null
                    && !string.Equals(configurationSource.Id, target.Id, StringComparison.Ordinal))
                {
                    _modelService.CopyProviderConfiguration(configurationSource, target);
                }

                if (sourceStates is not null
                    && !string.Equals(stateSource?.Id, target.Id, StringComparison.Ordinal))
                {
                    _providerStateService.Replace(target, sourceStates);
                }

                copied++;
            }
            catch (Exception ex) when (ex is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
            {
                errors.Add($"{target.Name}：{ex.Message}");
            }
        }

        return new ModelProviderSyncResult(
            copied,
            skippedRunning,
            errors,
            NoConfigurationSource: configurationSource is null);
    }

    private IReadOnlyList<ManagerInstance> FindComponent(
        ManagerInstance focus,
        IReadOnlyList<ManagerInstance> all)
    {
        var component = new List<ManagerInstance>();
        var pending = new Queue<ManagerInstance>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(focus);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current.Id))
            {
                continue;
            }

            component.Add(current);
            foreach (var candidate in all)
            {
                if (seen.Contains(candidate.Id)
                    || string.Equals(current.Id, candidate.Id, StringComparison.Ordinal)
                    || string.Equals(current.DshHome, candidate.DshHome, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ShouldSync(current, candidate))
                {
                    pending.Enqueue(candidate);
                }
            }
        }

        return component;
    }

    private bool ShouldSync(ManagerInstance left, ManagerInstance right)
    {
        try
        {
            return _settingsService.ShouldSyncModelProviders(left, right);
        }
        catch
        {
            return false;
        }
    }

    private bool HasProviderConfiguration(ManagerInstance instance)
    {
        try
        {
            return _modelService.Read(instance).Any(provider => provider.Configured);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsRunning(ManagerInstance instance) =>
        instance.RuntimeStatus == InstanceRuntimeStatus.Running
        || instance.RuntimeOwnership != InstanceRuntimeOwnership.None
        || _isRunning(instance.Id);

    private static DateTime? GetSettingsTimestamp(ManagerInstance instance)
    {
        var path = Path.Combine(instance.DshHome, "settings.yaml");
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? GetStateTimestamp(ManagerInstance instance)
    {
        var path = Path.Combine(instance.DshHome, ".dsh-launcher", "providers.json");
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ManagerInstance> NormalizeVersions(IEnumerable<ManagerInstance> versions) =>
        versions
            .Where(version => !string.IsNullOrWhiteSpace(version.Id)
                && !string.IsNullOrWhiteSpace(version.DshHome))
            .GroupBy(version => version.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
}
