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
/// 会话格式版本（会话头 <c>version</c>）：启动器不解释事件体，只按头部版本命名文件
/// 并把迁移交给 dsh 自己的 format catalog（work-log/51）。
/// </summary>
public sealed class ConversationService
{
    private const int MaxHeaderBytes = 256_000;
    private const int ZstdReadBufferSize = 64 * 1024;
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private readonly LauncherPaths _paths;
    private readonly Func<string, bool> _isRunning;

    public ConversationService(
        LauncherPaths? paths = null,
        Func<string, bool>? isRunning = null)
    {
        _paths = paths ?? new LauncherPaths();
        _isRunning = isRunning ?? (_ => false);
    }

    public IReadOnlyList<ConversationEntry> List(ManagerInstance instance)
    {
        var root = GetSessionsRoot(instance);
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return Array.Empty<ConversationEntry>();
        }

        var result = new List<ConversationEntry>();
        var titles = ReadSessionTitles(instance);
        Walk(root, root, result, titles, instance.Name);
        return result
            .OrderByDescending(entry => entry.UpdatedAt)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>全文检索单文件解压后读取上限（32 MiB，超出部分不扫）。</summary>
    public const int MaxSearchBytes = 32 * 1024 * 1024;
    private const int MaxMatchesPerFile = 2000;
    private const int SnippetBefore = 48;
    private const int SnippetAfter = 140;

    /// <summary>
    /// 会话全文检索：扫多个实例的 session.jsonl / session.v&lt;N&gt;.jsonl（含 .zstd）正文。
    /// 单个文件损坏/不可读只跳过该文件；不改写任何文件；不触碰上游 SQLite。
    /// 排序：命中次数降序 → 更新时间降序。
    /// </summary>
    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(
        IEnumerable<ManagerInstance> instances,
        string? query,
        int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var term = query?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<ConversationSearchHit>>(Array.Empty<ConversationSearchHit>());
        }

        var targets = instances
            .Where(instance => !string.IsNullOrWhiteSpace(instance.DshHome))
            .GroupBy(instance => instance.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var limit = Math.Clamp(maxResults, 1, 500);
        return Task.Run(() => SearchCore(targets, term, limit, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<ConversationSearchHit> SearchCore(
        IReadOnlyList<ManagerInstance> instances,
        string term,
        int maxResults,
        CancellationToken cancellationToken)
    {
        // JSONL 里反斜杠/引号是转义的：带这类字符的词再试一次转义后的形式。
        var escapedTerm = term.Contains('\\', StringComparison.Ordinal) || term.Contains('"', StringComparison.Ordinal)
            ? term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            : null;
        var hits = new List<ConversationSearchHit>();
        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ConversationEntry> entries;
            try
            {
                entries = List(instance);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = ReadSessionText(entry.FullPath);
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or ZstdException)
                {
                    // 损坏/不可读文件单独跳过。
                    continue;
                }

                var count = CountOccurrences(text, term, escapedTerm, out var firstIndex, out var matchLength);
                if (count == 0)
                {
                    continue;
                }

                hits.Add(new ConversationSearchHit(
                    entry,
                    count,
                    BuildSnippet(text, firstIndex, matchLength, out var snippetMatchStart),
                    snippetMatchStart,
                    matchLength));
            }
        }

        return hits
            .OrderByDescending(hit => hit.MatchCount)
            .ThenByDescending(hit => hit.Entry.UpdatedAt)
            .Take(maxResults)
            .ToArray();
    }

    private static int CountOccurrences(
        string text,
        string term,
        string? escapedTerm,
        out int firstIndex,
        out int matchLength)
    {
        firstIndex = -1;
        matchLength = term.Length;
        var count = CountInText(text, term, ref firstIndex);
        if (count > 0 || escapedTerm is null)
        {
            return count;
        }

        matchLength = escapedTerm.Length;
        return CountInText(text, escapedTerm, ref firstIndex);
    }

    private static int CountInText(string text, string term, ref int firstIndex)
    {
        var count = 0;
        var index = 0;
        while (count < MaxMatchesPerFile)
        {
            var found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            if (firstIndex < 0)
            {
                firstIndex = found;
            }

            count++;
            index = found + term.Length;
        }

        return count;
    }

    private static string BuildSnippet(string text, int index, int length, out int matchStart)
    {
        var start = Math.Max(0, index - SnippetBefore);
        var end = Math.Min(text.Length, index + length + SnippetAfter);
        var snippet = text[start..end]
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        matchStart = index - start;
        if (start > 0)
        {
            snippet = "…" + snippet;
            matchStart++;
        }

        if (end < text.Length)
        {
            snippet += "…";
        }

        return snippet;
    }

    private static string ReadSessionText(string path)
    {
        using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Stream source = file;
        if (path.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase))
        {
            source = new DecompressionStream(file, ZstdReadBufferSize, checkEndOfStream: false, leaveOpen: true);
        }

        using (source)
        {
            var buffer = new byte[ZstdReadBufferSize];
            using var memory = new MemoryStream();
            while (memory.Length < MaxSearchBytes)
            {
                var toRead = (int)Math.Min(buffer.Length, MaxSearchBytes - memory.Length);
                var read = source.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                memory.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }
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
                if (IsReparsePoint(path) || !IsSessionFileName(path))
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
                        info.Name.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase),
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

        if (!IsSessionFileName(source))
        {
            throw new InvalidDataException("只能恢复 DSh session.jsonl 或 session.jsonl.zstd 备份。");
        }

        if (ReadHeader(source) is null)
        {
            throw new InvalidDataException("备份不是可识别的 DSh session 文件，不能恢复。");
        }

        var restored = Import(instance, source);
        // 恢复代表重新创建会话。使用当前时间可让同步服务识别它晚于旧删除标记，
        // 从而在下一次同步时清除该标记，而不是把刚恢复的文件再次删除。
        File.SetLastWriteTimeUtc(restored, DateTime.UtcNow);
        return restored;
    }

    /// <summary>
    /// 一键导出实例的全部会话到目录（更换运行版本前的降级备份用）：文件名取自会话相对路径，
    /// 重名自动加序号；返回导出文件数。不修改会话本身。
    /// </summary>
    public int ExportAll(ManagerInstance instance, string destinationDirectory)
    {
        EnsureStopped(instance);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("导出目录不能为空。", nameof(destinationDirectory));
        }

        var entries = List(instance);
        if (entries.Count == 0)
        {
            return 0;
        }

        var directory = Path.GetFullPath(destinationDirectory.Trim());
        Directory.CreateDirectory(directory);
        var exported = 0;
        foreach (var entry in entries)
        {
            var extension = entry.IsCompressed ? ".jsonl.zstd" : ".jsonl";
            var stem = SafeFileName(entry.RelativePath
                .Replace(Path.DirectorySeparatorChar, '_')
                .Replace(Path.AltDirectorySeparatorChar, '_'));
            if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^extension.Length];
            }

            var candidate = Path.Combine(directory, stem + extension);
            for (var index = 2; File.Exists(candidate) && index < 1000; index++)
            {
                candidate = Path.Combine(directory, $"{stem}-{index}{extension}");
            }

            if (Export(instance, entry, candidate) is not null)
            {
                exported++;
            }
        }

        return exported;
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
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("导入文件不能为空。", nameof(sourcePath));
        }

        var source = Path.GetFullPath(sourcePath.Trim());
        if (!File.Exists(source) || IsReparsePoint(source))
        {
            throw new FileNotFoundException("导入会话文件不存在，或是符号链接。", source);
        }

        var compressed = source.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase);
        if (!compressed && !source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前导入入口只接受 session.jsonl / session.v&lt;N&gt;.jsonl（可带 .zstd）。");
        }

        var header = ReadHeader(source);
        if (header is null)
        {
            throw new InvalidDataException("导入文件不是可识别的 DSh session 文件。");
        }

        if (header.Version > 0 && !SupportsVersionedSessionFiles(instance))
        {
            throw new NotSupportedException(
                $"该会话是 v{header.Version} 格式，目标实例的 DSh 版本较旧、读不了；请导入到 0.1.5 及以上的实例。");
        }

        var sessionsRoot = GetSessionsRoot(instance);
        // 导入时可覆盖文件自带的工作目录，把会话放进目标版本指定的 workspace。
        var effectiveWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride.Trim()
            : header.WorkingDirectory;
        var projectDirectory = ProjectDirectory(sessionsRoot, effectiveWorkingDirectory);
        var sessionDirectory = Path.Combine(projectDirectory, EncodeSegment(header.SessionId));
        // 文件名版本必须等于头部版本（dsh 会逐字校对），编码跟随目标实例，避免 dsh 的encodingMismatch。
        var targetCompressed = ResolveTargetSessionCompression(instance);
        var target = Path.Combine(sessionDirectory, SessionFileNames.Build(header.Version, targetCompressed));
        EnsurePathDoesNotEscape(target, sessionsRoot);
        EnsureNoReparseComponents(sessionDirectory, sessionsRoot);
        if (File.Exists(target) || SessionDirectoryHasSessionFile(sessionDirectory))
        {
            throw new IOException($"实例中已经存在相同会话 ID：{header.SessionId}");
        }

        Directory.CreateDirectory(sessionDirectory);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            WriteSessionFile(source, temporary, compressed, targetCompressed);
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
        string instanceName)
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
                    Walk(entry, root, result, titles, instanceName);
                    continue;
                }

                var fileName = Path.GetFileName(entry);
                if (!SessionFileNames.TryParse(fileName, out _, out _))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(entry);
                    var header = ReadHeader(entry);
                    var updatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    result.Add(new ConversationEntry(
                        Path.GetRelativePath(root, entry),
                        Path.GetFullPath(entry),
                        header?.SessionId,
                        header?.WorkingDirectory,
                        updatedAt,
                        info.Length,
                        fileName.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase),
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

    /// <summary>
    /// 目标实例的会话编码：跟随该实例已有会话（dsh 同一 sessions 根不允许混用编码，
    /// 否则 dsh 会抛 encodingMismatch）；一个都没有时用 dsh 默认值 zstd。
    /// </summary>
    private static bool ResolveTargetSessionCompression(ManagerInstance instance) =>
        SessionFileNames.ResolveCompression(GetSessionsRoot(instance));

    /// <summary>
    /// 目标实例的 DSh 是否支持带版本号的会话代际（0.1.5+ 的 format catalog）。
    /// </summary>
    private static bool SupportsVersionedSessionFiles(ManagerInstance instance) =>
        SessionFileNames.SupportsVersionedGenerations(
            instance.RootPath,
            instance.DetectedVersion,
            GetSessionsRoot(instance));

    /// <summary>该会话目录里是否已经有 canonical 会话文件（同一会话的不同代际共用一个目录）。</summary>
    private static bool SessionDirectoryHasSessionFile(string sessionDirectory) =>
        SessionFileNames.DirectoryHasSessionFile(sessionDirectory);

    /// <summary>
    /// 写入会话文件：编码一致时直接复制，不一致时流式换容器。
    /// 只改外层压缩，不动会话头与任何事件行（迁移交给 dsh）。
    /// </summary>
    private static void WriteSessionFile(
        string source,
        string destination,
        bool sourceCompressed,
        bool targetCompressed)
    {
        if (sourceCompressed == targetCompressed)
        {
            File.Copy(source, destination, overwrite: false);
            return;
        }

        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using Stream decoded = sourceCompressed
            ? new DecompressionStream(input, ZstdReadBufferSize, checkEndOfStream: false, leaveOpen: true)
            : input;
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Stream encoded = targetCompressed
            ? new CompressionStream(output, leaveOpen: true)
            : output;
        decoded.CopyTo(encoded, ZstdReadBufferSize);
    }

    private static bool IsSessionFileName(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase);
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

        var fileName = Path.GetFileName(source);
        if (!SessionFileNames.TryParse(fileName, out _, out _))
        {
            throw new InvalidDataException("只能操作 DSh session.jsonl / session.v&lt;N&gt;.jsonl 文件。");
        }

        return source;
    }

    private void EnsureStopped(ManagerInstance instance)
    {
        if (_isRunning(instance.Id))
        {
            throw new InvalidOperationException("实例正在运行，不能导入、备份或删除会话文件。请先停止实例。");
        }
    }

    private static string GetSessionsRoot(ManagerInstance instance) =>
        Path.GetFullPath(Path.Combine(instance.DshHome, "sessions"));

    private static HeaderInfo? ReadHeader(string path)
    {
        try
        {
            using var source = File.OpenRead(path);
            if (path.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase))
            {
                using var decompressor = new DecompressionStream(
                    source,
                    ZstdReadBufferSize,
                    checkEndOfStream: false,
                    leaveOpen: false);
                return ParseHeader(ReadHeaderLine(decompressor));
            }

            return ParseHeader(ReadHeaderLine(source));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ZstdException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HeaderInfo? ParseHeader(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxHeaderBytes)
        {
            return null;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "session", StringComparison.Ordinal)
            || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionNumber)
            || versionNumber < 0
            || !root.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString())
            || !root.TryGetProperty("createdAt", out var createdAt)
            || !IsSafeNonNegativeInteger(createdAt)
            || !root.TryGetProperty("delegationDepth", out var delegationDepth)
            || !IsSafeNonNegativeInteger(delegationDepth))
        {
            return null;
        }

        if (root.TryGetProperty("origin", out var origin)
            && (origin.ValueKind != JsonValueKind.String || !string.Equals(origin.GetString(), "subagent", StringComparison.Ordinal)))
        {
            return null;
        }

        if (root.TryGetProperty("agentPreset", out var agentPreset)
            && agentPreset.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (root.TryGetProperty("sandboxMode", out _)
            || root.TryGetProperty("approvalPolicy", out _))
        {
            return null;
        }

        var sessionId = id.GetString()!;
        if (sessionId.Length > 256 || sessionId.Any(char.IsControl))
        {
            return null;
        }

        if (root.TryGetProperty("cwd", out var cwdValue)
            && cwdValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var cwd = root.TryGetProperty("cwd", out cwdValue)
            ? cwdValue.GetString()
            : null;
        if (cwd is not null && (cwd.Length > 4096 || cwd.Any(char.IsControl)))
        {
            return null;
        }

        return new HeaderInfo(sessionId, cwd, versionNumber);
    }

    private static bool IsSafeNonNegativeInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
        && number >= 0
        && number <= MaxSafeInteger;

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

    private static string? ReadHeaderLine(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length <= MaxHeaderBytes)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline + 1 : read;
            if (buffer.Length + count > MaxHeaderBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, count);
            if (newline >= 0)
            {
                break;
            }
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        var line = Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r', '\n');
        return line.Length > 0 && line[0] == '\uFEFF' ? line[1..] : line;
    }

    private static string ProjectDirectory(string root, string? cwd) =>
        Path.Combine(root, cwd is null ? "_no-cwd" : ProjectKey(cwd));

    /// <summary>
    /// dsh 的项目目录名编码（与 dsh <c>projectKey</c> 同源，见 docs/DSH_CONTRACT_INVENTORY.md C4）：
    /// 分隔符（<c>/ \ :</c>）折叠为单个 <c>-</c>，不安全码元转 <c>~XXXX</c>（大写四位十六进制，
    /// 与 session id 同一套转义），去掉前导 <c>-</c>，空结果取 <c>root</c>，截断到 251，
    /// 最后包成 <c>--…--</c>。harness 契约哨兵用上游测试向量回归它。
    /// </summary>
    internal static string ProjectKey(string cwd)
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

    /// <summary>
    /// session id 的目录名编码（与 dsh <c>encodeSegment</c> 同源）：安全字符
    /// <c>[A-Za-z0-9._-]</c> 原样保留，其余（含 <c>~</c> 自身）转 <c>~XXXX</c>；
    /// <c>.</c>/<c>..</c> 特例转义以消除路径穿越。
    /// </summary>
    internal static string EncodeSegment(string value)
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

    private sealed record HeaderInfo(string SessionId, string? WorkingDirectory, int Version);
}
