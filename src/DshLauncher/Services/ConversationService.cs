using System.Text;
using System.Text.Json;
using System.IO;
using System.Globalization;
using DshLauncher.Models;
using ZstdSharp;

namespace DshLauncher.Services;

/// <summary>
/// File-level conversation management for DSh's JSONL persistence backend.
/// It reads only the header for listings and never rewrites an append-only log.
/// </summary>
public sealed class ConversationService
{
    private const int ZstdReadBufferSize = 64 * 1024;
    private const long MaxSearchCharactersPerSession = 32L * 1024 * 1024;
    private readonly LauncherPaths _paths;
    private readonly Func<string, bool> _isRunning;

    public ConversationService(
        LauncherPaths? paths = null,
        Func<string, bool>? isRunning = null,
        ExternalDshHomeGuard? homeGuard = null)
    {
        _paths = paths ?? new LauncherPaths();
        _isRunning = isRunning ?? (_ => false);
        _homeGuard = homeGuard ?? new ExternalDshHomeGuard();
    }

    private readonly ExternalDshHomeGuard _homeGuard;

    public IReadOnlyList<ConversationEntry> List(ManagerInstance instance)
    {
        var root = GetSessionsRoot(instance);
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return Array.Empty<ConversationEntry>();
        }

        var result = new List<ConversationEntry>();
        var titles = ReadSessionTitles(instance);
        Walk(root, root, result, titles, instance.Name, highestGenerationOnly: true);
        return result
            .OrderByDescending(entry => entry.UpdatedAt)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Enumerates every retained generation for maintenance operations. The
    /// normal conversation page uses <see cref="List"/> and shows only the
    /// highest generation in each session directory.
    /// </summary>
    public IReadOnlyList<ConversationEntry> ListAll(ManagerInstance instance)
    {
        var root = GetSessionsRoot(instance);
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return Array.Empty<ConversationEntry>();
        }

        var result = new List<ConversationEntry>();
        var titles = ReadSessionTitles(instance);
        Walk(root, root, result, titles, instance.Name, highestGenerationOnly: false);
        return result
            .OrderByDescending(entry => entry.UpdatedAt)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Validates all conditions needed before opening a conversation. This is
    /// intentionally independent of lifecycle state so callers can run it
    /// before deciding whether a stopped instance needs to be started.
    /// </summary>
    public bool CanOpen(
        ManagerInstance instance,
        ConversationEntry entry,
        out string reason)
    {
        reason = string.Empty;
        if (instance is null || entry is null)
        {
            reason = "实例或会话为空。";
            return false;
        }

        try
        {
            var source = ValidateEntry(instance, entry);
            if (!SessionFormatHelper.TryReadHeader(source, out var header))
            {
                reason = "会话 header 无效，不能打开。";
                return false;
            }

            if (entry.SessionId is not null
                && !string.Equals(entry.SessionId, header.SessionId, StringComparison.Ordinal))
            {
                reason = "会话列表中的 session ID 已过期，请刷新后重试。";
                return false;
            }

            if (!SessionFormatHelper.IsRuntimeFormatSupported(instance, header.Version))
            {
                reason =
                    $"当前 DSh runtime 不具备 Session v{header.Version} 的官方 format catalog，不能打开该会话。";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or FileNotFoundException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Searches session metadata and the append-only JSONL body without modifying it.
    /// Individual unreadable or damaged files are skipped so one bad conversation does
    /// not make the whole result unavailable.
    /// </summary>
    public IReadOnlyList<ConversationEntry> Search(
        IEnumerable<ConversationEntry> entries,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var term = query?.Trim() ?? string.Empty;
        var snapshot = entries.ToArray();
        if (term.Length == 0)
        {
            return snapshot;
        }

        var result = new List<ConversationEntry>();
        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MetadataContains(entry, term) || SessionBodyContains(entry, term, cancellationToken))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static bool MetadataContains(ConversationEntry entry, string term) =>
        entry.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || entry.InstanceName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (entry.SessionId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (entry.WorkingDirectory?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool SessionBodyContains(
        ConversationEntry entry,
        string term,
        CancellationToken cancellationToken)
    {
        if (!entry.HasValidHeader || !File.Exists(entry.FullPath) || IsReparsePoint(entry.FullPath))
        {
            return false;
        }

        try
        {
            using var source = File.OpenRead(entry.FullPath);
            if (entry.IsCompressed)
            {
                using var decompressor = new DecompressionStream(
                    source,
                    ZstdReadBufferSize,
                    checkEndOfStream: false,
                    leaveOpen: false);
                return StreamContains(decompressor, term, cancellationToken);
            }

            return StreamContains(source, term, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ZstdException
            or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool StreamContains(
        Stream stream,
        string term,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        var buffer = new char[16 * 1024];
        var carry = string.Empty;
        long charactersRead = 0;
        while (charactersRead < MaxSearchCharactersPerSession)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, MaxSearchCharactersPerSession - charactersRead);
            var count = reader.Read(buffer, 0, requested);
            if (count == 0)
            {
                return false;
            }

            charactersRead += count;
            var block = carry + new string(buffer, 0, count);
            if (block.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var overlap = Math.Min(Math.Max(0, term.Length - 1), block.Length);
            carry = overlap == 0 ? string.Empty : block[^overlap..];
        }

        return false;
    }

    public string Backup(ManagerInstance instance, ConversationEntry entry)
    {
        EnsureStopped(instance);
        var source = ValidateEntry(instance, entry);
        var destinationDirectory = _paths.GetInstanceBackupDirectory(instance.Id);
        Directory.CreateDirectory(destinationDirectory);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var relativeName = entry.RelativePath
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        var destination = Path.Combine(destinationDirectory, $"{timestamp}-{SafeFileName(relativeName)}");
        if (File.Exists(destination))
        {
            destination = Path.Combine(destinationDirectory, $"{timestamp}-{Guid.NewGuid():N}-{SafeFileName(relativeName)}");
        }

        File.Copy(source, destination, overwrite: false);
        return destination;
    }

    public IReadOnlyList<ConversationBackupEntry> ListBackups(ManagerInstance instance)
    {
        var root = Path.GetFullPath(_paths.GetInstanceBackupDirectory(instance.Id));
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return Array.Empty<ConversationBackupEntry>();
        }

        var titles = ReadSessionTitles(instance);
        var result = new List<ConversationBackupEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(path)
                    || !SessionFormatHelper.TryParseBackupFileName(path, out var format))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    var header = ReadHeader(path);
                    var backedUpAt = ReadBackupTimestamp(info);
                    result.Add(new ConversationBackupEntry(
                        info.Name,
                        info.FullName,
                        header?.SessionId,
                        header?.WorkingDirectory,
                        backedUpAt,
                        info.Length,
                        format.IsCompressed,
                        header is not null,
                        header?.SessionId is null
                            ? "无法读取的备份"
                            : BuildDisplayName(titles, header.SessionId, header.WorkingDirectory, backedUpAt),
                        instance.Name));
                }
                catch (IOException)
                {
                    // 备份可能在刷新期间被其它 Launcher 操作移走。
                }
                catch (UnauthorizedAccessException)
                {
                    // 单个不可读备份不阻断整个列表。
                }
            }
        }
        catch (IOException)
        {
            return Array.Empty<ConversationBackupEntry>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<ConversationBackupEntry>();
        }

        return result
            .OrderByDescending(entry => entry.BackedUpAt)
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ConversationStorageInfo GetStorageInfo(ManagerInstance instance)
    {
        var root = GetSessionsRoot(instance);
        var hasJsonlFiles = Directory.Exists(root)
            && !IsReparsePoint(root)
            && ContainsSessionFile(root);
        var sqliteConfigured = IsSqlitePersistenceConfigured(instance);
        return new ConversationStorageInfo(
            sqliteConfigured
                ? hasJsonlFiles
                    ? ConversationStorageKind.Mixed
                    : ConversationStorageKind.Sqlite
                : ConversationStorageKind.Jsonl,
            hasJsonlFiles);
    }

    public string RestoreBackup(ManagerInstance instance, ConversationBackupEntry backup)
    {
        EnsureStopped(instance);
        ArgumentNullException.ThrowIfNull(backup);
        var root = Path.GetFullPath(_paths.GetInstanceBackupDirectory(instance.Id));
        var source = Path.GetFullPath(backup.FullPath);
        EnsureBackupPathDoesNotEscape(source, root);
        EnsureNoReparseComponents(source, root);
        if (!File.Exists(source) || IsReparsePoint(source))
        {
            throw new FileNotFoundException("选中的对话备份不存在，或是符号链接。", source);
        }

        if (!SessionFormatHelper.TryParseBackupFileName(source, out _))
        {
            throw new InvalidDataException("只能恢复 DSh canonical session.jsonl 或 session.vN.jsonl 备份。");
        }

        if (ReadHeader(source) is null)
        {
            throw new InvalidDataException("备份不是可识别的 DSh session 文件，不能恢复。");
        }

        return Import(instance, source);
    }

    public string Export(ManagerInstance instance, ConversationEntry entry, string destinationPath)
    {
        EnsureStopped(instance);
        var source = ValidateEntry(instance, entry);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("导出目标不能为空。", nameof(destinationPath));
        }

        var destination = NormalizeExportDestination(destinationPath, entry.IsCompressed);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("导出目标不能与源文件相同。");
        }

        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("导出目标没有父目录。");
        Directory.CreateDirectory(directory);
        if (File.Exists(destination))
        {
            throw new IOException($"导出目标已存在，为避免覆盖文件已停止：{destination}");
        }

        File.Copy(source, destination, overwrite: false);
        return destination;
    }

    public string Import(ManagerInstance instance, string sourcePath, string? workingDirectoryOverride = null)
    {
        EnsureStopped(instance);
        if (!GetStorageInfo(instance).SupportsJsonlImport)
        {
            throw new NotSupportedException(
                "当前版本配置了 SQLite 会话存储，不能把 JSONL 文件写入一个不会被 DSh 使用的 sessions 目录。请先在 DSh 中切换回 JSONL 存储，或直接在当前实例窗口管理对话。");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("导入文件不能为空。", nameof(sourcePath));
        }

        var source = Path.GetFullPath(sourcePath.Trim());
        if (!File.Exists(source) || IsReparsePoint(source))
        {
            throw new FileNotFoundException("导入会话文件不存在，或是符号链接。", source);
        }

        if (!SessionFormatHelper.TryParsePortableFileName(
                source,
                out var sourceFormat,
                out _))
        {
            throw new NotSupportedException("当前导入入口只接受 JSONL 会话文件（.jsonl 或 .jsonl.zstd）。");
        }

        var header = ReadHeader(source);
        if (header is null)
        {
            throw new InvalidDataException("导入文件不是可识别的 DSh session.jsonl。");
        }

        if (!SessionFormatHelper.IsRuntimeFormatSupported(instance, header.Version))
        {
            throw new NotSupportedException(
                $"当前 DSh runtime 不具备 Session v{header.Version} 的官方 format catalog，不能导入或继续该会话。");
        }

        var sessionsRoot = GetSessionsRoot(instance);
        // 导入时可覆盖文件自带的工作目录，把会话放进目标版本指定的 workspace。
        var effectiveWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride.Trim()
            : header.WorkingDirectory;
        var projectDirectory = ProjectDirectory(sessionsRoot, effectiveWorkingDirectory);
        var sessionDirectory = Path.Combine(projectDirectory, EncodeSegment(header.SessionId));
        var target = Path.Combine(
            sessionDirectory,
            SessionFormatHelper.GetCanonicalFileName(header.Version, sourceFormat.IsCompressed));
        EnsurePathDoesNotEscape(target, sessionsRoot);
        EnsureNoReparseComponents(sessionDirectory, sessionsRoot);
        if (Directory.Exists(sessionDirectory)
            && ContainsCanonicalSessionFile(sessionDirectory))
        {
            throw new IOException($"实例中已经存在相同会话 ID：{header.SessionId}");
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"实例中已经存在相同会话 ID：{header.SessionId}");
        }

        Directory.CreateDirectory(sessionDirectory);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            // 导入代表用户主动重新创建该会话。先在临时文件上更新时间，避免
            // 同路径的旧删除标记在随后的版本同步中把刚导入的会话再次删除。
            File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow);
            File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return target;
    }

    public void Delete(ManagerInstance instance, ConversationEntry entry)
    {
        EnsureStopped(instance);
        var source = ValidateEntry(instance, entry);
        File.Delete(source);
    }

    private void Walk(
        string directory,
        string root,
        ICollection<ConversationEntry> result,
        IReadOnlyDictionary<string, string?> titles,
        string instanceName,
        bool highestGenerationOnly)
    {
        var files = new List<SessionFileCandidate>();
        CollectSessionFiles(directory, files);
        var selected = highestGenerationOnly
            ? files
                .GroupBy(
                    candidate => Path.GetDirectoryName(candidate.FullPath) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.Format.Version)
                    .ThenBy(item => item.FullPath, StringComparer.Ordinal)
                    .First())
            : files;
        foreach (var candidate in selected)
        {
            try
            {
                var info = new FileInfo(candidate.FullPath);
                var header = ReadHeader(candidate.FullPath);
                var updatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                result.Add(new ConversationEntry(
                    Path.GetRelativePath(root, candidate.FullPath),
                    Path.GetFullPath(candidate.FullPath),
                    header?.SessionId,
                    header?.WorkingDirectory,
                    updatedAt,
                    info.Length,
                    candidate.Format.IsCompressed,
                    header is not null,
                    header?.SessionId is null
                        ? "无法读取会话"
                        : BuildDisplayName(titles, header.SessionId, header.WorkingDirectory, updatedAt),
                    instanceName));
            }
            catch (IOException)
            {
                // A file can disappear while the user is viewing the list.
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible session should not stop the manager page.
            }
        }
    }

    private static void CollectSessionFiles(
        string directory,
        ICollection<SessionFileCandidate> result)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsReparsePoint(entry))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    CollectSessionFiles(entry, result);
                    continue;
                }

                var fileName = Path.GetFileName(entry);
                if (!SessionFormatHelper.TryParseFileName(fileName, out var format))
                {
                    continue;
                }

                try
                {
                    result.Add(new SessionFileCandidate(Path.GetFullPath(entry), format));
                }
                catch (IOException)
                {
                    // A file can disappear while the user is viewing the list.
                }
                catch (UnauthorizedAccessException)
                {
                    // An inaccessible session should not stop the manager page.
                }
            }
        }
        catch (IOException)
        {
            // A project/session directory can disappear or become inaccessible during a refresh.
        }
        catch (UnauthorizedAccessException)
        {
            // One inaccessible directory should not hide all other sessions.
        }
    }


    private const string SessionTitleCacheFileName = "session_projcache.json";

    private static IReadOnlyDictionary<string, string?> ReadSessionTitles(ManagerInstance instance)
    {
        var path = Path.Combine(instance.DshHome, "storages", SessionTitleCacheFileName);
        try
        {
            EnsureNoReparseComponents(path, instance.DshHome);
        }
        catch (IOException)
        {
            // 标题缓存路径经过符号链接/重解析点时拒绝读取，避免混入其它实例的元数据。
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            // ACL 拒绝读取路径属性时同样放弃标题缓存；它只是可选增强，
            // 不能让整个会话列表随可选标题一起失败。
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            // 标题缓存损坏或 schema 变化时可能持有意外类型；标题只是可选
            // 增强信息，逐层校验 ValueKind，不让格式异常中断整个会话列表。
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tables", out var tables)
                || tables.ValueKind != JsonValueKind.Object
                || !tables.TryGetProperty("sessions", out var sessions)
                || sessions.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string?>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var session in sessions.EnumerateObject())
            {
                if (session.Value.ValueKind != JsonValueKind.Object
                    || !session.Value.TryGetProperty("rows", out var rows)
                    || rows.ValueKind != JsonValueKind.Object
                    || !rows.TryGetProperty("title", out var titleRow)
                    || titleRow.ValueKind != JsonValueKind.Object
                    || !titleRow.TryGetProperty("val", out var titleValue))
                {
                    continue;
                }

                result[session.Name] = titleValue.ValueKind == JsonValueKind.String
                    ? titleValue.GetString()
                    : null;
            }

            return result;
        }
        catch (IOException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }

    private static string BuildDisplayName(
        IReadOnlyDictionary<string, string?> titles,
        string sessionId,
        string? workingDirectory,
        DateTimeOffset updatedAt)
    {
        if (titles.TryGetValue(sessionId, out var title) && !string.IsNullOrWhiteSpace(title))
        {
            var normalized = string.Join(" ", title!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 120 ? normalized : normalized[..120];
        }

        var project = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFileName(workingDirectory!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var when = updatedAt.ToLocalTime().ToString("MM-dd HH:mm");
        return string.IsNullOrEmpty(project)
            ? $"未命名 · {when}"
            : $"未命名 · {project} · {when}";
    }

    private static DateTimeOffset ReadBackupTimestamp(FileInfo info)
    {
        var prefix = info.Name.Length >= 15 ? info.Name[..15] : string.Empty;
        if (DateTime.TryParseExact(
                prefix,
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return new DateTimeOffset(parsed);
        }

        return new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
    }

    private string ValidateEntry(ManagerInstance instance, ConversationEntry entry)
    {
        var root = GetSessionsRoot(instance);
        var source = Path.GetFullPath(entry.FullPath);
        EnsurePathDoesNotEscape(source, root);
        EnsureNoReparseComponents(source, root);
        if (!File.Exists(source) || IsReparsePoint(source))
        {
            throw new FileNotFoundException("会话文件不存在，或是符号链接。", source);
        }

        if (!SessionFormatHelper.TryParseFileName(source, out _))
        {
            throw new InvalidDataException("只能操作 DSh canonical session.jsonl 或 session.vN.jsonl 文件。");
        }

        return source;
    }

    private void EnsureStopped(ManagerInstance instance)
    {
        _homeGuard.EnsureAvailable(instance);
        if (_isRunning(instance.Id))
        {
            throw new InvalidOperationException("实例正在运行，不能导入、备份或删除会话文件。请先停止实例。");
        }
    }

    private static string GetSessionsRoot(ManagerInstance instance) =>
        Path.GetFullPath(Path.Combine(instance.DshHome, "sessions"));

    private static bool ContainsSessionFile(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsReparsePoint(path)
                        && SessionFormatHelper.TryParseFileName(path, out _))
                    {
                        return true;
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsReparsePoint(child))
                    {
                        pending.Push(child);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Backend detection is informational; the normal listing path will
            // still surface every individually readable JSONL session.
        }

        return false;
    }

    private static bool ContainsCanonicalSessionFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(path => !IsReparsePoint(path)
                    && SessionFormatHelper.TryParseFileName(path, out _));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable existing directory is not safe to treat as empty;
            // reject the import rather than risk adding a second generation.
            throw new IOException("无法确认目标会话目录是否已有其它代际文件。", ex);
        }
    }

    private static bool IsSqlitePersistenceConfigured(ManagerInstance instance)
    {
        var profileRoot = Path.Combine(instance.DshHome, "profiles", "web");
        var candidates = new[]
        {
            Path.Combine(instance.DshHome, "launcher.patch.yml"),
            Path.Combine(profileRoot, "cordis.patch.yml"),
            Path.Combine(profileRoot, "cordis.yml")
        };
        if (candidates.Any(ReferencesSqlitePersistence))
        {
            return true;
        }

        foreach (var patchPath in EnumerateProfileBundlePatches(profileRoot))
        {
            if (ReferencesSqlitePersistence(patchPath))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateProfileBundlePatches(string profileRoot)
    {
        var manifestPath = Path.Combine(profileRoot, "package.json");
        if (!File.Exists(manifestPath) || IsReparsePoint(manifestPath))
        {
            yield break;
        }

        JsonDocument manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            yield break;
        }

        using (manifest)
        {
            if (!manifest.RootElement.TryGetProperty("dsh", out var dsh)
                || dsh.ValueKind != JsonValueKind.Object
                || !dsh.TryGetProperty("profile", out var profile)
                || profile.ValueKind != JsonValueKind.Object
                || !profile.TryGetProperty("bundles", out var bundles)
                || bundles.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var bundle in bundles.EnumerateArray())
            {
                if (bundle.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(bundle.GetString()))
                {
                    continue;
                }

                var packageName = bundle.GetString()!;
                foreach (var nodeModulesRoot in new[]
                {
                    Path.Combine(profileRoot, "node_modules"),
                    Path.Combine(Path.GetDirectoryName(profileRoot)!, "node_modules")
                })
                {
                    var packageRoot = ResolvePackageRoot(nodeModulesRoot, packageName);
                    if (packageRoot is null)
                    {
                        continue;
                    }

                    var packageManifestPath = Path.Combine(packageRoot, "package.json");
                    if (!File.Exists(packageManifestPath) || IsReparsePoint(packageManifestPath))
                    {
                        continue;
                    }

                    string? patchRelativePath = null;
                    try
                    {
                        using var packageStream = File.OpenRead(packageManifestPath);
                        using var packageManifest = JsonDocument.Parse(packageStream);
                        var root = packageManifest.RootElement;
                        if (root.TryGetProperty("dsh", out var packageDsh)
                            && packageDsh.ValueKind == JsonValueKind.Object
                            && packageDsh.TryGetProperty("bundle", out var packageBundle)
                            && packageBundle.ValueKind == JsonValueKind.Object
                            && packageBundle.TryGetProperty("patch", out var patch)
                            && patch.ValueKind == JsonValueKind.String)
                        {
                            patchRelativePath = patch.GetString();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(patchRelativePath))
                    {
                        continue;
                    }

                    var patchPath = Path.GetFullPath(Path.Combine(packageRoot, patchRelativePath));
                    var packagePrefix = Path.GetFullPath(packageRoot)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (patchPath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return patchPath;
                    }
                }
            }
        }
    }

    private static string? ResolvePackageRoot(string nodeModulesRoot, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)
            || Path.IsPathRooted(packageName)
            || packageName.Contains('\\'))
        {
            return null;
        }

        var segments = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")
            || (segments.Length != 1
                && (segments.Length != 2 || !segments[0].StartsWith('@'))))
        {
            return null;
        }

        return Path.Combine(new[] { nodeModulesRoot }.Concat(segments).ToArray());
    }

    private static bool ReferencesSqlitePersistence(string path)
    {
        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024)
            {
                return false;
            }

            foreach (var line in File.ReadLines(path))
            {
                var value = line.TrimStart();
                if (value.StartsWith('#'))
                {
                    continue;
                }

                if (value.Contains("@deepseek-ai/dsh-session-persistence-sqlite", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("session-persistence-sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static SessionHeaderInfo? ReadHeader(string path) =>
        SessionFormatHelper.TryReadHeader(path, out var header) ? header : null;

    internal static bool HasRecognizedSessionHeader(string path) =>
        SessionFormatHelper.TryReadHeader(path, out _);

    internal static bool TryReadSessionHeader(
        string path,
        out SessionHeaderInfo header) =>
        SessionFormatHelper.TryReadHeader(path, out header);

    private static string NormalizeExportDestination(string destinationPath, bool compressed)
    {
        var destination = Path.GetFullPath(destinationPath.Trim());
        var extension = compressed ? ".jsonl.zstd" : ".jsonl";
        var duplicatedExtension = extension + extension;
        while (destination.EndsWith(duplicatedExtension, StringComparison.OrdinalIgnoreCase))
        {
            destination = destination[..^extension.Length];
        }

        return destination;
    }

    private static string ProjectDirectory(string root, string? cwd) =>
        Path.Combine(root, cwd is null ? "_no-cwd" : ProjectKey(cwd));

    private static string ProjectKey(string cwd)
    {
        if (cwd.Length == 0)
        {
            throw new InvalidDataException("session header 的 cwd 不能为空字符串。");
        }

        var builder = new StringBuilder();
        var separatorRun = false;
        foreach (var character in cwd)
        {
            if (character is '/' or '\\' or ':')
            {
                if (!separatorRun) builder.Append('-');
                separatorRun = true;
            }
            else if (IsSafeSegmentCharacter(character))
            {
                builder.Append(character);
                separatorRun = false;
            }
            else
            {
                builder.Append('~');
                builder.Append(((int)character).ToString("X4"));
                separatorRun = false;
            }
        }

        var value = builder.ToString().TrimStart('-');
        return $"--{(value.Length == 0 ? "root" : value[..Math.Min(value.Length, 251)])}--";
    }

    private static string EncodeSegment(string value)
    {
        if (value.Length == 0)
        {
            throw new InvalidDataException("session id 不能为空。");
        }

        if (value is "." or "..")
        {
            return value == "." ? "~002E" : "~002E~002E";
        }

        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (character != '~' && IsSafeSegmentCharacter(character))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('~');
                builder.Append(((int)character).ToString("X4"));
            }
        }

        return builder.ToString();
    }

    private static bool IsSafeSegmentCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        return builder.Length == 0 ? "session.jsonl" : builder.ToString();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void EnsurePathDoesNotEscape(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("会话文件不在当前实例 sessions 目录内。");
        }
    }

    private static void EnsureBackupPathDoesNotEscape(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("选中的文件不在当前实例的对话备份目录内。");
        }
    }

    private static void EnsureNoReparseComponents(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        if (IsReparsePoint(normalizedRoot))
        {
            throw new IOException("会话根目录不能是符号链接或重解析点。");
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        if (relative is "." or "") return;
        var current = normalizedRoot;
        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
            {
                throw new IOException("会话路径不能经过符号链接或重解析点。");
            }
        }
    }

    private sealed record SessionFileCandidate(
        string FullPath,
        SessionFileFormat Format);
}
