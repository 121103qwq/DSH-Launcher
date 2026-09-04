using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed partial class DiagnosticBundleService
{
    private const long MaximumLogBytes = 4 * 1024 * 1024;
    private readonly LauncherPaths _paths;

    public DiagnosticBundleService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    public string Create(
        string destinationPath,
        IEnumerable<ManagerInstance> instances,
        NodeRuntimeInfo nodeRuntime,
        DshRuntimeInfo dshRuntime)
    {
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("诊断包目标没有父目录。"));
        if (File.Exists(destination))
        {
            throw new IOException($"诊断包目标已存在：{destination}");
        }

        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        AddText(archive, "environment.txt", BuildEnvironmentSummary(nodeRuntime, dshRuntime));
        AddText(archive, "instances.json", SerializeSanitizedInstances(instances));
        AddText(archive, "directory-summary.txt", BuildDirectorySummary(instances));
        AddLogs(archive);
        return destination;
    }

    private void AddLogs(ZipArchive archive)
    {
        if (Directory.Exists(_paths.LogsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log", SearchOption.TopDirectoryOnly))
            {
                AddSanitizedLog(archive, path, $"logs/{Path.GetFileName(path)}");
            }
        }

        var legacyCrashLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeek", "launcher", "crash.log");
        AddSanitizedLog(archive, legacyCrashLog, "logs/crash.log");
    }

    private static string BuildEnvironmentSummary(NodeRuntimeInfo nodeRuntime, DshRuntimeInfo dshRuntime)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($"Runtime: {Environment.Version}");
        builder.AppendLine($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"Node: {(nodeRuntime.IsAvailable ? "可用" : "不可用")} · {nodeRuntime.VersionText} · {nodeRuntime.ExecutablePath ?? "未找到"}");
        builder.AppendLine($"DSh: {(dshRuntime.IsAvailable ? "可用" : "不可用")} · {dshRuntime.VersionText} · {dshRuntime.ExecutablePath ?? "未找到"}");
        return builder.ToString();
    }

    private static string SerializeSanitizedInstances(IEnumerable<ManagerInstance> instances)
    {
        var safe = instances.Select(instance => new
        {
            instance.Id,
            instance.Name,
            instance.Kind,
            instance.DetectedVersion,
            instance.RuntimeStatus,
            instance.RuntimeOwnership,
            instance.RegisteredAt,
            instance.LastUsedAt,
            HasRuntime = instance.EffectiveDshLaunchSpec is not null,
            HasDshHome = Directory.Exists(instance.DshHome)
        });
        return JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildDirectorySummary(IEnumerable<ManagerInstance> instances)
    {
        var builder = new StringBuilder();
        foreach (var instance in instances)
        {
            builder.AppendLine($"[{instance.Name}] {instance.Id}");
            if (!Directory.Exists(instance.DshHome) || IsReparsePoint(instance.DshHome))
            {
                builder.AppendLine("  DSH_HOME 不存在或为链接");
                continue;
            }

            var count = 0;
            try
            {
                var pending = new Stack<string>();
                pending.Push(instance.DshHome);
                while (pending.Count > 0 && count < 2_000)
                {
                    var directory = pending.Pop();
                    foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (count++ >= 2_000)
                        {
                            break;
                        }

                        if (IsReparsePoint(path))
                        {
                            continue;
                        }

                        var relative = Path.GetRelativePath(instance.DshHome, path);
                        var length = new FileInfo(path).Length;
                        builder.AppendLine($"  {RedactPath(relative)} · {length} bytes");
                    }

                    foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (!IsReparsePoint(child))
                        {
                            pending.Push(child);
                        }
                    }
                }

                if (count >= 2_000)
                {
                    builder.AppendLine("  …文件清单已截断");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                builder.AppendLine($"  清单读取失败：{ex.Message}");
            }
        }

        return builder.ToString();
    }

    private static string RedactPath(string value) =>
        SensitiveNameRegex().IsMatch(value) ? "[敏感文件名已隐藏]" : value;

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddSanitizedLog(ZipArchive archive, string path, string entryName)
    {
        try
        {
            if (!File.Exists(path) || IsReparsePoint(path))
            {
                return;
            }

            var info = new FileInfo(path);
            if (info.Length > MaximumLogBytes)
            {
                return;
            }

            var content = File.ReadAllText(path, Encoding.UTF8);
            content = SecretAssignmentRegex().Replace(
                content,
                match => match.Groups[1].Value + "[已隐藏]");
            content = BearerTokenRegex().Replace(
                content,
                match => match.Groups[1].Value + "[已隐藏]");
            content = StandaloneApiKeyRegex().Replace(content, "[已隐藏的密钥]");
            AddText(archive, entryName, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    [GeneratedRegex("(?:credentials|secret|token|api[-_.]?key|password|private[-_.]?key|\\.env)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNameRegex();

    [GeneratedRegex("((?:api[-_.]?key|token|password|secret)\\s*[:=]\\s*)[^\\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("(Bearer\\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?:sk|ak)-[A-Za-z0-9_-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneApiKeyRegex();
}
