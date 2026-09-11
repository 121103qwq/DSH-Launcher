using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Merges DSh session files between versions that share a conversation policy.
/// A merge only writes stopped instances. Running DSh processes keep exclusive
/// ownership of their own session files until the next lifecycle boundary.
/// </summary>
public sealed class ConversationSyncService
{
    private const string SyncStateFileName = "conversation-sync.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly VersionSettingsService _settingsService;
    private readonly Func<string, bool> _isManagedRunning;

    public ConversationSyncService(
        VersionSettingsService settingsService,
        Func<string, bool>? isManagedRunning = null)
    {
        _settingsService = settingsService;
        _isManagedRunning = isManagedRunning ?? (_ => false);
    }

    public ConversationSyncResult Synchronize(
        ManagerInstance focus,
        IEnumerable<ManagerInstance> versions)
    {
        var all = NormalizeVersions(versions.Append(focus));
        var component = FindComponent(focus, all);
        return SynchronizeComponent(component);
    }

    public ConversationSyncResult SynchronizeAll(IEnumerable<ManagerInstance> versions)
    {
        var all = NormalizeVersions(versions);
        var remaining = new HashSet<string>(all.Select(version => version.Id), StringComparer.Ordinal);
        var total = new SyncAccumulator();

        foreach (var version in all)
        {
            if (!remaining.Contains(version.Id))
            {
                continue;
            }

            var component = FindComponent(version, all);
            foreach (var member in component)
            {
                remaining.Remove(member.Id);
            }

            total.Add(SynchronizeComponent(component));
        }

        return total.ToResult();
    }

    public ConversationSyncResult PropagateDeletion(
        ManagerInstance focus,
        string relativePath,
        IEnumerable<ManagerInstance> versions)
    {
        var all = NormalizeVersions(versions.Append(focus));
        var component = FindComponent(focus, all);
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized is null)
        {
            return new ConversationSyncResult(
                0,
                component.Count(IsRunning),
                new[] { "会话路径无效，不能同步删除。" });
        }

        var stopped = component.Where(version => !IsRunning(version)).ToArray();
        var errors = new List<string>();
        var deletedAt = DateTime.UtcNow;
        foreach (var version in stopped)
        {
            try
            {
                DeleteSessionFile(version, normalized);
                UpdateDeletionState(version, normalized, deletedAt, deleted: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{version.Name}/{normalized}：{ex.Message}");
            }
        }

        return new ConversationSyncResult(0, component.Count - stopped.Length, errors);
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
                    || string.Equals(current.Id, candidate.Id, StringComparison.Ordinal))
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
        if (string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            || string.Equals(left.DshHome, right.DshHome, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return _settingsService.ShouldSyncConversations(left, right);
        }
        catch
        {
            // An invalid settings file must not make another version's data
            // writable by accident. The settings page reports the error.
            return false;
        }
    }

    private ConversationSyncResult SynchronizeComponent(IReadOnlyList<ManagerInstance> component)
    {
        var stopped = component.Where(version => !IsRunning(version)).ToArray();
        var skippedRunning = component.Count - stopped.Length;
        if (stopped.Length < 2)
        {
            return new ConversationSyncResult(0, skippedRunning, Array.Empty<string>());
        }

        var files = new Dictionary<string, List<SessionFile>>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var tombstones = ReadTombstones(stopped, errors);
        foreach (var version in stopped)
        {
            foreach (var file in EnumerateSessionFiles(version, errors))
            {
                var relativePath = NormalizeRelativePath(file.RelativePath);
                if (relativePath is null)
                {
                    errors.Add($"{version.Name}/{file.RelativePath}：会话路径无效，已跳过。");
                    continue;
                }

                // 同一会话的不同代际（v0/v1/…/vN）共用一个会话目录，按目录归并身份。
                var sessionKey = SessionFileNames.DirectoryKey(relativePath);
                if (sessionKey.Length == 0)
                {
                    errors.Add($"{version.Name}/{relativePath}：会话不在会话目录内，已跳过。");
                    continue;
                }

                var normalizedFile = file with { RelativePath = relativePath };
                if (!files.TryGetValue(sessionKey, out var candidates))
                {
                    candidates = new List<SessionFile>();
                    files[sessionKey] = candidates;
                }

                candidates.Add(normalizedFile);
            }
        }

        var copied = 0;
        var sessionKeys = new HashSet<string>(files.Keys, StringComparer.OrdinalIgnoreCase);
        sessionKeys.UnionWith(tombstones.Keys);
        foreach (var sessionKey in sessionKeys)
        {
            files.TryGetValue(sessionKey, out var candidates);
            candidates ??= new List<SessionFile>();
            if (tombstones.TryGetValue(sessionKey, out var deletedAt))
            {
                candidates = candidates
                    .Where(candidate => candidate.LastWriteTimeUtc > deletedAt)
                    .ToList();
                if (candidates.Count == 0)
                {
                    ApplyTombstone(stopped, sessionKey, deletedAt, errors);
                    continue;
                }

                ClearTombstone(stopped, sessionKey, errors);
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            // 同一会话多代际共存时取版本最高的一代（避免把旧代际回灌到已升级的实例）。
            var source = candidates
                .OrderByDescending(candidate => candidate.Generation)
                .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
                .ThenByDescending(candidate => candidate.Length)
                .ThenBy(candidate => candidate.Instance.Id, StringComparer.Ordinal)
                .First();

            foreach (var targetVersion in stopped)
            {
                if (string.Equals(source.Instance.Id, targetVersion.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var targetRoot = SessionsRoot(targetVersion);
                    var targetDirectory = Path.Combine(
                        targetRoot,
                        sessionKey.Replace('/', Path.DirectorySeparatorChar));
                    var targetPath = Path.Combine(targetDirectory, Path.GetFileName(source.RelativePath));
                    if (HasReparsePointInPath(targetPath, targetRoot))
                    {
                        throw new IOException("目标会话路径包含重解析点。");
                    }

                    // 目标已有同代或更新代际：不降级、不重复复制。
                    var existingGeneration = SessionFileNames.HighestGeneration(targetDirectory);
                    if (existingGeneration >= source.Generation)
                    {
                        continue;
                    }

                    // 新增一代：编码必须与目标一致（dsh 同一 sessions 根不允许混用），
                    // 且目标 dsh 能读该版本（v>=1 需要 0.1.5+ 的 format catalog）。
                    var targetCompressed = SessionFileNames.ResolveCompression(targetRoot);
                    var sourceCompressed = source.RelativePath.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase);
                    if (targetCompressed != sourceCompressed)
                    {
                        errors.Add($"{targetVersion.Name}/{source.RelativePath}：目标实例会话编码为"
                            + $"{(targetCompressed ? "zstd" : "plain")}、源为{(sourceCompressed ? "zstd" : "plain")}，已跳过。");
                        continue;
                    }

                    if (source.Generation > 0
                        && !SessionFileNames.SupportsVersionedGenerations(
                            targetVersion.RootPath,
                            targetVersion.DetectedVersion,
                            targetRoot))
                    {
                        errors.Add($"{targetVersion.Name}/{source.RelativePath}：目标实例的 DSh 版本较旧、"
                            + $"读不了 v{source.Generation} 会话，已跳过。");
                        continue;
                    }

                    if (File.Exists(targetPath) && FilesEqual(source.FullPath, targetPath))
                    {
                        continue;
                    }

                    if (CopyStable(source.FullPath, targetPath, source.LastWriteTimeUtc, out var error))
                    {
                        copied++;
                    }
                    else if (!string.IsNullOrWhiteSpace(error))
                    {
                        errors.Add($"{targetVersion.Name}/{source.RelativePath}：{error}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add($"{targetVersion.Name}/{source.RelativePath}：{ex.Message}");
                }
            }
        }

        return new ConversationSyncResult(copied, skippedRunning, errors);
    }

    private bool IsRunning(ManagerInstance instance) =>
        instance.RuntimeStatus == InstanceRuntimeStatus.Running
        || instance.RuntimeOwnership != InstanceRuntimeOwnership.None
        || _isManagedRunning(instance.Id);

    private static IReadOnlyList<ManagerInstance> NormalizeVersions(IEnumerable<ManagerInstance> versions) =>
        versions
            .Where(version => !string.IsNullOrWhiteSpace(version.Id)
                && !string.IsNullOrWhiteSpace(version.DshHome))
            .GroupBy(version => version.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    private static IEnumerable<SessionFile> EnumerateSessionFiles(
        ManagerInstance instance,
        ICollection<string> errors)
    {
        var root = SessionsRoot(instance);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        if (IsReparsePoint(root))
        {
            errors.Add($"{instance.Name}：sessions 目录是重解析点，已跳过。");
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{instance.Name}/{Path.GetRelativePath(root, directory)}：{ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsReparsePoint(entry))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                var fileName = Path.GetFileName(entry);
                if (!SessionFileNames.TryParse(fileName, out var generation, out _))
                {
                    continue;
                }

                SessionFile? sessionFile = null;
                try
                {
                    var info = new FileInfo(entry);
                    sessionFile = new SessionFile(
                        instance,
                        entry,
                        Path.GetRelativePath(root, entry),
                        generation,
                        info.Length,
                        info.LastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{instance.Name}/{Path.GetRelativePath(root, entry)}：{ex.Message}");
                }

                if (sessionFile is not null)
                {
                    yield return sessionFile;
                }
            }
        }
    }

    private static bool CopyStable(
        string source,
        string target,
        DateTime sourceTimestamp,
        out string? error)
    {
        error = null;
        var directory = Path.GetDirectoryName(target);
        if (directory is null)
        {
            error = "目标会话目录无效。";
            return false;
        }

        Directory.CreateDirectory(directory);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(source);
            var temporary = $"{target}.{Guid.NewGuid():N}.dshsync.tmp";
            try
            {
                File.Copy(source, temporary, overwrite: false);
                var after = new FileInfo(source);
                if (before.Length != after.Length
                    || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
                {
                    continue;
                }

                File.Move(temporary, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, sourceTimestamp);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
        }

        error ??= "源会话文件在复制期间仍在变化，已跳过。";
        return false;
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var leftBuffer = new byte[64 * 1024];
        var rightBuffer = new byte[leftBuffer.Length];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                return false;
            }

            for (var index = 0; index < leftRead; index++)
            {
                if (leftBuffer[index] != rightBuffer[index])
                {
                    return false;
                }
            }

            if (leftRead == 0)
            {
                return true;
            }
        }
    }

    private static Dictionary<string, DateTime> ReadTombstones(
        IEnumerable<ManagerInstance> versions,
        ICollection<string> errors)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            var path = SyncStatePath(version);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var state = JsonSerializer.Deserialize<ConversationSyncState>(
                    File.ReadAllText(path, Encoding.UTF8),
                    JsonOptions);
                foreach (var item in state?.Deleted ?? new Dictionary<string, DateTime>())
                {
                    var relativePath = NormalizeRelativePath(item.Key);
                    if (relativePath is null)
                    {
                        continue;
                    }

                    // 删除记录按会话目录归并（旧状态文件里的完整文件路径也归一化到这里）。
                    var sessionKey = SessionFileNames.DirectoryKey(relativePath);
                    if (sessionKey.Length == 0)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(sessionKey, out var existing)
                        || item.Value > existing)
                    {
                        result[sessionKey] = item.Value;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                errors.Add($"{version.Name}：读取会话同步状态失败：{ex.Message}");
            }
        }

        return result;
    }

    private static void ApplyTombstone(
        IEnumerable<ManagerInstance> versions,
        string sessionKey,
        DateTime deletedAt,
        ICollection<string> errors)
    {
        foreach (var version in versions)
        {
            try
            {
                DeleteSessionFile(version, sessionKey);
                UpdateDeletionState(version, sessionKey, deletedAt, deleted: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{version.Name}/{sessionKey}：{ex.Message}");
            }
        }
    }

    private static void ClearTombstone(
        IEnumerable<ManagerInstance> versions,
        string sessionKey,
        ICollection<string> errors)
    {
        foreach (var version in versions)
        {
            try
            {
                UpdateDeletionState(version, sessionKey, DateTime.MinValue, deleted: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"{version.Name}/{sessionKey}：清理会话删除状态失败：{ex.Message}");
            }
        }
    }

    /// <summary>删除一个会话目录下的全部代际文件（同一会话的 v0/v3… 都算同一会话）。</summary>
    private static void DeleteSessionFile(ManagerInstance instance, string sessionKey)
    {
        var root = SessionsRoot(instance);
        var target = Path.Combine(root, sessionKey.Replace('/', Path.DirectorySeparatorChar));
        if (HasReparsePointInPath(target, root))
        {
            throw new IOException("目标会话路径包含重解析点。");
        }

        if (!Directory.Exists(target))
        {
            if (File.Exists(target))
            {
                throw new IOException("目标会话路径不是目录。");
            }

            return;
        }

        foreach (var path in Directory.EnumerateFiles(target))
        {
            if (SessionFileNames.TryParse(Path.GetFileName(path), out _, out _))
            {
                File.Delete(path);
            }
        }
    }

    private static void UpdateDeletionState(
        ManagerInstance instance,
        string sessionKey,
        DateTime deletedAt,
        bool deleted)
    {
        var path = SyncStatePath(instance);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("会话同步状态没有父目录。");
        Directory.CreateDirectory(directory);
        var state = new ConversationSyncState();
        if (File.Exists(path))
        {
            try
            {
                state = JsonSerializer.Deserialize<ConversationSyncState>(
                    File.ReadAllText(path, Encoding.UTF8),
                    JsonOptions) ?? new ConversationSyncState();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                throw new IOException("会话同步状态文件格式无效。", ex);
            }
        }

        if (deleted)
        {
            state.Deleted[sessionKey] = deletedAt;
        }
        else
        {
            state.Deleted.Remove(sessionKey);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(state, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    private static string SyncStatePath(ManagerInstance instance) =>
        Path.Combine(instance.DshHome, ".dsh-launcher", SyncStateFileName);

    private static string? NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return null;
        }

        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return SessionFileNames.TryParse(fileName, out _, out _)
            ? normalized
            : null;
    }

    private static string SessionsRoot(ManagerInstance instance) =>
        Path.GetFullPath(Path.Combine(instance.DshHome, "sessions"));

    private static bool HasReparsePointInPath(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("目标会话路径越界。");
        }

        var current = fullPath;
        while (!string.Equals(current, fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(current))
        {
            if (IsReparsePoint(current))
            {
                return true;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record SessionFile(
        ManagerInstance Instance,
        string FullPath,
        string RelativePath,
        int Generation,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed class ConversationSyncState
    {
        public Dictionary<string, DateTime> Deleted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SyncAccumulator
    {
        private int _copied;
        private int _skippedRunning;
        private readonly List<string> _errors = new();

        public void Add(ConversationSyncResult result)
        {
            _copied += result.CopiedFiles;
            _skippedRunning += result.SkippedRunningVersions;
            _errors.AddRange(result.Errors);
        }

        public ConversationSyncResult ToResult() =>
            new(_copied, _skippedRunning, _errors.ToArray());
    }
}

public sealed record ConversationSyncResult(
    int CopiedFiles,
    int SkippedRunningVersions,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}
