using System.Text;
using System.Text.Json;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// File-level conversation management for DSh's JSONL persistence backend.
/// It reads only the header for listings and never rewrites an append-only log.
/// </summary>
public sealed class ConversationService
{
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
        Walk(root, root, result);
        return result
            .OrderByDescending(entry => entry.UpdatedAt)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    public string Export(ManagerInstance instance, ConversationEntry entry, string destinationPath)
    {
        EnsureStopped(instance);
        var source = ValidateEntry(instance, entry);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("导出目标不能为空。", nameof(destinationPath));
        }

        var destination = Path.GetFullPath(destinationPath.Trim());
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

    public string Import(ManagerInstance instance, string sourcePath)
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

        if (!source.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前导入入口只接受未压缩 session.jsonl；压缩日志会保留在原格式，待后续加入受支持的 Zstandard 读取器。");
        }

        var header = ReadHeader(source);
        if (header is null)
        {
            throw new InvalidDataException("导入文件不是可识别的 DSh session.jsonl。");
        }

        var sessionsRoot = GetSessionsRoot(instance);
        var projectDirectory = ProjectDirectory(sessionsRoot, header.WorkingDirectory);
        var sessionDirectory = Path.Combine(projectDirectory, EncodeSegment(header.SessionId));
        var target = Path.Combine(sessionDirectory, "session.jsonl");
        EnsurePathDoesNotEscape(target, sessionsRoot);
        EnsureNoReparseComponents(sessionDirectory, sessionsRoot);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"实例中已经存在相同会话 ID：{header.SessionId}");
        }

        Directory.CreateDirectory(sessionDirectory);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
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

    private void Walk(string directory, string root, ICollection<ConversationEntry> result)
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
                    Walk(entry, root, result);
                    continue;
                }

                var fileName = Path.GetFileName(entry);
                if (!fileName.Equals("session.jsonl", StringComparison.OrdinalIgnoreCase)
                    && !fileName.Equals("session.jsonl.zstd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(entry);
                    var header = fileName.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : ReadHeader(entry);
                    result.Add(new ConversationEntry(
                        Path.GetRelativePath(root, entry),
                        Path.GetFullPath(entry),
                        header?.SessionId,
                        header?.WorkingDirectory,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        info.Length,
                        fileName.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase),
                        header is not null));
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
        if (!fileName.Equals("session.jsonl", StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals("session.jsonl.zstd", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("只能操作 DSh session.jsonl 文件。");
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
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || line.Length > 256_000)
            {
                return null;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "session", StringComparison.Ordinal)
                || !root.TryGetProperty("id", out var id)
                || string.IsNullOrWhiteSpace(id.GetString()))
            {
                return null;
            }

            var sessionId = id.GetString()!;
            if (sessionId.Length > 256 || sessionId.Any(char.IsControl))
            {
                return null;
            }

            var cwd = root.TryGetProperty("cwd", out var cwdValue) && cwdValue.ValueKind == JsonValueKind.String
                ? cwdValue.GetString()
                : null;
            if (cwd is not null && (cwd.Length > 4096 || cwd.Any(char.IsControl)))
            {
                return null;
            }

            return new HeaderInfo(sessionId, cwd);
        }
        catch (JsonException)
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

    private sealed record HeaderInfo(string SessionId, string? WorkingDirectory);
}
