using System.IO;
using System.Text;
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
    private const string CredentialsFileName = ".credentials.yaml";
    private const long MaximumCredentialsFileBytes = 1024 * 1024;
    private readonly VersionSettingsService _settingsService;
    private readonly ModelService _modelService;
    private readonly ProviderStateService _providerStateService;
    private readonly Func<string, bool> _isRunning;
    private readonly Action<string>? _beforeProviderFileCommit;

    public ModelProviderSyncService(
        VersionSettingsService settingsService,
        ModelService modelService,
        ProviderStateService providerStateService,
        Func<string, bool>? isRunning = null)
        : this(settingsService, modelService, providerStateService, isRunning, null)
    {
    }

    internal ModelProviderSyncService(
        VersionSettingsService settingsService,
        ModelService modelService,
        ProviderStateService providerStateService,
        Func<string, bool>? isRunning,
        Action<string>? beforeProviderFileCommit)
    {
        _settingsService = settingsService;
        _modelService = modelService;
        _providerStateService = providerStateService;
        _isRunning = isRunning ?? (_ => false);
        _beforeProviderFileCommit = beforeProviderFileCommit;
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
            .Select(version => (Version: version, Timestamp: GetProviderSnapshotTimestamp(version)))
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
                    var settingsText = _modelService.BuildProviderConfigurationText(configurationSource, target);
                    CopyProviderSnapshot(
                        configurationSource,
                        target,
                        settingsText,
                        _beforeProviderFileCommit);
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

    private static DateTime? GetProviderSnapshotTimestamp(ManagerInstance instance)
    {
        return GetFileTimestamp(Path.Combine(instance.DshHome, "settings.yaml"));
    }

    private static DateTime? GetStateTimestamp(ManagerInstance instance)
    {
        var path = Path.Combine(instance.DshHome, ".dsh-launcher", "providers.json");
        return GetFileTimestamp(path);
    }

    private static DateTime? GetFileTimestamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CopyProviderSnapshot(
        ManagerInstance source,
        ManagerInstance target,
        string settingsText,
        Action<string>? beforeProviderFileCommit)
    {
        Directory.CreateDirectory(target.DshHome);
        var updates = new List<StagedProviderFile>();
        try
        {
            var settingsPath = Path.Combine(target.DshHome, "settings.yaml");
            updates.Add(StageText(settingsPath, settingsText));

            var credentialUpdate = StageCredentialStore(source, target);
            if (credentialUpdate is not null)
            {
                updates.Add(credentialUpdate);
            }

            foreach (var update in updates)
            {
                update.CreateBackup();
            }

            var committed = 0;
            try
            {
                foreach (var update in updates)
                {
                    beforeProviderFileCommit?.Invoke(update.TargetPath);
                    update.Commit();
                    committed++;
                }
            }
            catch (Exception commitError)
            {
                var rollbackErrors = new List<string>();
                for (var index = committed - 1; index >= 0; index--)
                {
                    try
                    {
                        updates[index].Restore();
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackErrors.Add(rollbackError.Message);
                    }
                }

                if (rollbackErrors.Count > 0)
                {
                    throw new IOException(
                        $"Provider 同步失败，且恢复原配置时遇到错误：{string.Join("；", rollbackErrors)}",
                        commitError);
                }

                throw;
            }
        }
        finally
        {
            foreach (var update in updates)
            {
                update.Dispose();
            }
        }
    }

    private static StagedProviderFile StageText(string targetPath, string content)
    {
        var temporary = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
        {
            writer.Write(content);
            writer.Flush();
            output.Flush(flushToDisk: true);
        }

        return new StagedProviderFile(targetPath, temporary);
    }

    private static StagedProviderFile? StageCredentialStore(ManagerInstance source, ManagerInstance target)
    {
        var sourcePath = Path.Combine(source.DshHome, CredentialsFileName);
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        RejectCredentialLink(sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length > MaximumCredentialsFileBytes)
        {
            throw new InvalidDataException("Provider 凭据文件超过 1 MiB，已拒绝同步。 ");
        }

        var targetPath = Path.Combine(target.DshHome, CredentialsFileName);
        if (File.Exists(targetPath))
        {
            RejectCredentialLink(targetPath);
        }

        var temporary = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new StagedProviderFile(targetPath, temporary);
    }

    private static void RejectCredentialLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Provider 凭据文件不能是符号链接或重解析点。 ");
        }
    }

    private static IReadOnlyList<ManagerInstance> NormalizeVersions(IEnumerable<ManagerInstance> versions) =>
        versions
            .Where(version => !string.IsNullOrWhiteSpace(version.Id)
                && !string.IsNullOrWhiteSpace(version.DshHome))
            .GroupBy(version => version.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private sealed class StagedProviderFile : IDisposable
    {
        private readonly bool _targetExisted;
        private string? _backupPath;

        public StagedProviderFile(string targetPath, string stagedPath)
        {
            TargetPath = targetPath;
            StagedPath = stagedPath;
            _targetExisted = File.Exists(targetPath);
        }

        public string TargetPath { get; }

        public string StagedPath { get; }

        public void CreateBackup()
        {
            if (!_targetExisted)
            {
                return;
            }

            _backupPath = $"{TargetPath}.{Guid.NewGuid():N}.bak";
            File.Copy(TargetPath, _backupPath, overwrite: false);
        }

        public void Commit() => File.Move(StagedPath, TargetPath, overwrite: true);

        public void Restore()
        {
            if (_targetExisted)
            {
                if (_backupPath is null || !File.Exists(_backupPath))
                {
                    throw new IOException($"缺少 Provider 同步备份：{Path.GetFileName(TargetPath)}");
                }

                File.Move(_backupPath, TargetPath, overwrite: true);
                _backupPath = null;
            }
            else if (File.Exists(TargetPath))
            {
                File.Delete(TargetPath);
            }
        }

        public void Dispose()
        {
            if (File.Exists(StagedPath))
            {
                File.Delete(StagedPath);
            }

            if (_backupPath is not null && File.Exists(_backupPath))
            {
                File.Delete(_backupPath);
            }
        }
    }
}
