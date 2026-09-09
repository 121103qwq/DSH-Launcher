using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed record DiagnoseResult(bool Ok, string? ArchivePath, string? Error);

/// <summary>
/// 一键诊断导出（借鉴 Ruler4396/dsh-launcher 的 --diagnose 设计，MIT）：
/// `DSH Launcher.exe --diagnose [--out &lt;zip 路径&gt;]` 或设置页按钮，
/// 把 Launcher 日志、崩溃日志、守护日志、环境/版本、设置与状态文件、错误码汇总
/// 打包成 zip（默认落到"下载"文件夹），供用户自主上传。
///
/// 脱敏铁律：
/// - 绝不包含 .credentials.yaml / 会话内容 / node_modules；
/// - 路径中的用户名替换为 %USER%，键值形如 api_key/token/secret/password 的值打码；
/// - 无遥测：产物只落本地，由用户自行决定是否分享。
/// </summary>
public sealed class DiagnoseExportService
{
    private static readonly Regex SecretPattern = new(
        @"(?i)(""(?:[^""]*(?:api[_-]?key|token|secret|password|authorization)[^""]*)""\s*:\s*"")([^""]*)("")",
        RegexOptions.Compiled);

    private static readonly Regex YamlSecretPattern = new(
        @"(?im)^(\s*[A-Za-z0-9_.-]*(?:api[_-]?key|token|secret|password|authorization)[A-Za-z0-9_.-]*\s*[:=]\s*)(\S+)",
        RegexOptions.Compiled);

    private readonly LauncherPaths _paths;

    public DiagnoseExportService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    /// <summary>命令行入口：返回进程退出码（0 成功，1 失败）。</summary>
    public static int RunFromCommandLine(string[] args)
    {
        var output = ReadOption(args, "--out");
        var result = new DiagnoseExportService().Export(output);
        if (!result.Ok)
        {
            LauncherLog.Error("诊断包导出失败。", ErrorCodes.E1003, new { result.Error });
            Console.Error.WriteLine("诊断包导出失败：" + result.Error);
            return 1;
        }

        Console.WriteLine("诊断包已导出：" + result.ArchivePath);
        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public DiagnoseResult Export(string? outputPath = null)
    {
        try
        {
            var archivePath = string.IsNullOrWhiteSpace(outputPath)
                ? BuildDefaultPath()
                : Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            AddText(archive, "env.txt", BuildEnvironmentReport());
            AddText(archive, "launcher-log.txt", ReadLogTail(LauncherLog.LogPath, 3000));
            AddText(archive, "crash.txt", ReadLogTail(Path.Combine(LauncherLog.LogDirectory, "crash.log"), 500));
            AddText(archive, "watchdog.txt", ReadLogTail(Path.Combine(LauncherLog.LogDirectory, "watchdog.log"), 1000));
            AddText(archive, "settings.txt", Sanitize(ReadFileOrPlaceholder(
                Path.Combine(_paths.RootDirectory, "launcher-settings.json"))));
            AddText(archive, "state.txt", BuildStateReport());
            AddText(archive, "errors.txt", SummarizeErrors(LauncherLog.LogPath));
            AddInstanceSettings(archive);
            LauncherLog.Info("诊断包已导出。", ErrorCodes.E1003, new { archivePath });
            return new DiagnoseResult(true, archivePath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new DiagnoseResult(false, null, ex.Message);
        }
    }

    private static string BuildDefaultPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        if (!Directory.Exists(downloads))
        {
            downloads = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        if (!Directory.Exists(downloads))
        {
            downloads = profile;
        }

        return Path.Combine(downloads, $"dsh-launcher-diagnose-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    }

    private string BuildEnvironmentReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("DSH Launcher 诊断包");
        builder.AppendLine($"created_utc={DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"launcher_version={typeof(App).Assembly.GetName().Version}");
        builder.AppendLine($"os={Environment.OSVersion}");
        builder.AppendLine($"arch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"dotnet={Environment.Version}");
        builder.AppendLine($"webview2={ReadWebView2Version() ?? "(未检测到 Evergreen WebView2 注册表项)"}");
        builder.AppendLine($"webview2_data={WebView2DataFolder.ResolveForCurrentProcess()}");
        builder.AppendLine($"proxy={ProxyConfigurator.Describe()}");
        builder.AppendLine($"node={RunCapture("node", "--version")}");
        builder.AppendLine();
        builder.AppendLine("--- 已注册实例（脱敏） ---");
        foreach (var instance in TryReadInstances())
        {
            builder.AppendLine(
                $"{instance.Name}\tkind={instance.KindText}\tversion={instance.DetectedVersion ?? "-"}\t" +
                $"status={instance.RuntimeStatus}\tport={instance.Port?.ToString() ?? "-"}");
            builder.AppendLine($"  root={Sanitize(instance.RootPath)}");
            builder.AppendLine($"  dsh_home={Sanitize(instance.DshHome)}");
        }

        return Sanitize(builder.ToString());
    }

    private string BuildStateReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("--- instances.json（脱敏） ---");
        builder.AppendLine(Sanitize(ReadFileOrPlaceholder(_paths.InstancesFilePath)));
        foreach (var name in new[] { "window-state.json", "webcache-version.json", "runtime-cache.json", "marketplace-sources.json" })
        {
            builder.AppendLine($"--- {name} ---");
            builder.AppendLine(Sanitize(ReadFileOrPlaceholder(Path.Combine(_paths.RootDirectory, name))));
        }

        return builder.ToString();
    }

    private void AddInstanceSettings(ZipArchive archive)
    {
        if (!Directory.Exists(_paths.InstancesDirectory))
        {
            return;
        }

        foreach (var instanceDirectory in Directory.EnumerateDirectories(_paths.InstancesDirectory))
        {
            var settingsPath = Path.Combine(instanceDirectory, "dsh-home", ".dsh-launcher", "version-settings.json");
            if (!File.Exists(settingsPath))
            {
                continue;
            }

            var entryName = $"instances/{Path.GetFileName(instanceDirectory)}/version-settings.json";
            AddText(archive, entryName, Sanitize(ReadFileOrPlaceholder(settingsPath)));
        }
    }

    private IReadOnlyList<ManagerInstance> TryReadInstances()
    {
        try
        {
            if (!File.Exists(_paths.InstancesFilePath))
            {
                return Array.Empty<ManagerInstance>();
            }

            var json = File.ReadAllText(_paths.InstancesFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<ManagerInstance>>(json) ?? new List<ManagerInstance>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<ManagerInstance>();
        }
    }

    private static string ReadFileOrPlaceholder(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "（文件不存在）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "（读取失败：" + ex.Message + "）";
        }
    }

    private static string ReadLogTail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "（日志不存在）";
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }

            return Sanitize(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "（读取失败：" + ex.Message + "）";
        }
    }

    internal static string SummarizeErrors(string logPath)
    {
        var counts = new Dictionary<string, (int Count, string First)>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(logPath))
            {
                return "（无日志）";
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (!line.TrimStart().StartsWith('{'))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (!document.RootElement.TryGetProperty("code", out var code)
                        || code.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var key = code.GetString() ?? "?";
                    var message = document.RootElement.TryGetProperty("msg", out var msg)
                        && msg.ValueKind == JsonValueKind.String
                        ? msg.GetString() ?? string.Empty
                        : string.Empty;
                    counts[key] = counts.TryGetValue(key, out var current)
                        ? (current.Count + 1, current.First)
                        : (1, message);
                }
                catch (JsonException)
                {
                    // 跳过损坏行。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "（读取失败：" + ex.Message + "）";
        }

        if (counts.Count == 0)
        {
            return "（无错误码记录）";
        }

        var builder = new StringBuilder();
        foreach (var pair in counts.OrderByDescending(item => item.Value.Count))
        {
            builder.AppendLine($"[{pair.Key}] x{pair.Value.Count}  {ErrorCodes.Describe(pair.Key)}");
            if (!string.IsNullOrWhiteSpace(pair.Value.First))
            {
                builder.AppendLine("    例: " + Sanitize(pair.Value.First));
            }
        }

        return builder.ToString();
    }

    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                // 原文形式（普通日志）与 JSON 转义形式（统一日志每行是 JSON，反斜杠双写）都要覆盖。
                text = text.Replace(profile, "%USER%", StringComparison.OrdinalIgnoreCase);
                text = text.Replace(
                    profile.Replace("\\", "\\\\", StringComparison.Ordinal),
                    "%USER%",
                    StringComparison.OrdinalIgnoreCase);
                var userName = Path.GetFileName(profile.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(userName))
                {
                    // 路径上下文中的用户名：单反斜杠与 JSON 双反斜杠两种形态。
                    text = Regex.Replace(text,
                        @"\\\\" + Regex.Escape(userName) + @"(?=\\\\)",
                        @"\\USERNAME",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    text = Regex.Replace(text,
                        @"\\" + Regex.Escape(userName) + @"(?=\\)",
                        @"\USERNAME",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
            }

            text = text.Replace("%USERPROFILE%", "%USER%", StringComparison.OrdinalIgnoreCase);
            // 通用用户目录形态：C:\Users\xxx 与 JSON 转义 C:\\Users\\xxx。
            text = Regex.Replace(text,
                @"[A-Za-z]:\\{1,2}Users\\{1,2}[^\\""/:\s]+",
                "%USER%",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = SecretPattern.Replace(text, "$1***$3");
            text = YamlSecretPattern.Replace(text, "$1***");
            return text;
        }
        catch
        {
            return text;
        }
    }

    private static string? ReadWebView2Version()
    {
        try
        {
            foreach (var view in new[] { "64", "32" })
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}}");
                if (key?.GetValue("pv") is string version && !string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 注册表不可读时忽略。
        }

        return null;
    }

    private static string RunCapture(string file, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "(无法启动)";
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(4000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 超时进程清理尽力而为。
                }

                return "(执行超时)";
            }

            var text = output.Result.Trim();
            return string.IsNullOrWhiteSpace(text) ? "(无输出)" : text;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "(不可用：" + ex.Message + ")";
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content ?? string.Empty);
    }
}
