using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class DshRuntimeDetector
{
    private static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex ReportedVersion = new(
        @"(?<![0-9A-Za-z])v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<DshRuntimeInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        return await DetectAsync(preferredInstallDirectory: null, cancellationToken);
    }

    public async Task<DshRuntimeInfo> DetectAsync(
        string? preferredInstallDirectory,
        CancellationToken cancellationToken = default)
    {
        var foundCandidate = false;
        foreach (var candidate in GetCandidates(preferredInstallDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(candidate))
            {
                continue;
            }

            foundCandidate = true;
            var packageRoot = TryFindPackageRoot(candidate);
            var packageVersion = packageRoot is null ? null : TryReadPackageVersion(packageRoot);
            if (packageRoot is null || string.IsNullOrWhiteSpace(packageVersion))
            {
                continue;
            }

            var reportedVersion = await ReadVersionAsync(candidate, cancellationToken);
            if (!string.Equals(reportedVersion, packageVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new DshRuntimeInfo(
                true,
                candidate,
                packageVersion,
                packageRoot,
                null,
                TryReadNodeEngine(packageRoot));
        }

        return DshRuntimeInfo.Missing(foundCandidate
            ? "找到了 DSh 命令，但安装包无法解析、命令不能运行，或命令版本与安装包不一致。请使用“准备运行环境”修复。"
            : "PATH 和所选安装位置中没有可运行的 dsh.cmd 或 dsh.exe。");
    }

    public static string? TryFindPackageRoot(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var directory = Directory.Exists(executablePath)
            ? Path.GetFullPath(executablePath)
            : Path.GetDirectoryName(Path.GetFullPath(executablePath));

        for (var i = 0; i < 5 && !string.IsNullOrWhiteSpace(directory); i++)
        {
            var packageRoot = Path.Combine(directory, "node_modules", "@deepseek-ai", "dsh");
            if (IsDshPackageRoot(packageRoot))
            {
                return packageRoot;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    public static string? TryResolvePackageRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var normalized = Path.GetFullPath(directory.Trim());
        if (IsDshPackageRoot(normalized))
        {
            return normalized;
        }

        var nested = Path.Combine(normalized, "node_modules", "@deepseek-ai", "dsh");
        return IsDshPackageRoot(nested) ? nested : null;
    }

    public static string? TryReadPackageVersion(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return null;
        }

        var normalized = Path.GetFullPath(packageRoot.Trim());
        if (!IsDshPackageRoot(normalized))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(normalized, "package.json"), Encoding.UTF8));
            return document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
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

    public static string? TryReadNodeEngine(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return null;
        }

        var normalized = Path.GetFullPath(packageRoot.Trim());
        if (!IsDshPackageRoot(normalized))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(normalized, "package.json"), Encoding.UTF8));
            var root = document.RootElement;
            return root.TryGetProperty("engines", out var engines)
                && engines.ValueKind == JsonValueKind.Object
                && engines.TryGetProperty("node", out var node)
                && node.ValueKind == JsonValueKind.String
                ? node.GetString()
                : null;
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

    public static string? ResolveNodeEngine(ManagerInstance? instance, string? detectedNodeEngine)
    {
        if (instance?.Kind == InstanceKind.Source)
        {
            // Source 是独立 runtime：未声明 engines.node 时保持未声明，
            // 不继承系统全局 installed DSh 的版本要求。
            return SourceProjectInspector.TryReadNodeEngine(instance.RootPath);
        }

        if (instance?.Kind == InstanceKind.Installed)
        {
            var packageRoot = TryResolvePackageRoot(instance.RootPath);
            if (packageRoot is null)
            {
                // 当前实例 runtime 已失效（正在重装/重绑定）时才使用
                // 重新检测到的 DSh metadata。
                return detectedNodeEngine;
            }

            // 有效 package 未声明 engines.node 时保持未声明，
            // 不继承系统中其它 DSh 的版本要求。
            return TryReadNodeEngine(packageRoot);
        }

        return detectedNodeEngine;
    }

    public static string? FindExecutableForPackageRoot(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return null;
        }

        var normalized = Path.GetFullPath(packageRoot.Trim());
        var nodeModules = Directory.GetParent(normalized)?.FullName;
        var binDirectory = nodeModules is null ? null : Directory.GetParent(nodeModules)?.FullName;
        if (binDirectory is null)
        {
            return null;
        }

        foreach (var fileName in new[] { "dsh.cmd", "dsh.exe", "dsh" })
        {
            var candidate = Path.Combine(binDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IEnumerable<string> GetCandidates()
    {
        return GetCandidates(preferredInstallDirectory: null);
    }

    public static IEnumerable<string> GetCandidates(string? preferredInstallDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedPreferred = TryNormalizeDirectory(preferredInstallDirectory);
        if (normalizedPreferred is not null)
        {
            foreach (var fileName in new[] { "dsh.cmd", "dsh.exe", "dsh" })
            {
                var candidate = Path.Combine(normalizedPreferred, fileName);
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            foreach (var fileName in new[] { "dsh.cmd", "dsh.exe", "dsh" })
            {
                var candidate = Path.Combine(trimmed, fileName);
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        var roamingNpm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm");
        foreach (var fileName in new[] { "dsh.cmd", "dsh.exe" })
        {
            var candidate = Path.Combine(roamingNpm, fileName);
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    public static bool IsExecutableInInstallDirectory(
        string? executablePath,
        string? installDirectory)
    {
        var normalizedDirectory = TryNormalizeDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(executablePath) || normalizedDirectory is null)
        {
            return false;
        }

        try
        {
            var normalizedExecutable = Path.GetFullPath(executablePath);
            var relative = Path.GetRelativePath(normalizedDirectory, normalizedExecutable);
            return !Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryNormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(directory.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsDshPackageRoot(string directory)
    {
        var packagePath = Path.Combine(directory, "package.json");
        if (!File.Exists(packagePath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packagePath, Encoding.UTF8));
            return document.RootElement.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), "@deepseek-ai/dsh", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath)
        };

        try
        {
            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            var completionTask = Task.WhenAll(exitTask, outputTask, errorTask);
            var timeoutTask = Task.Delay(CandidateTimeout, cancellationToken);
            var completedTask = await Task.WhenAny(completionTask, timeoutTask);

            if (completedTask != completionTask)
            {
                TryKill(process);
                await WaitForExitSafelyAsync(completionTask);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            await completionTask;
            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = outputTask.Result.Trim();
            var match = ReportedVersion.Match(output);
            return match.Success ? match.Groups["version"].Value : null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            throw;
        }
        catch
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            return null;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (Path.GetExtension(executablePath).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /c \"\"{executablePath}\" --version\"";
        }
        else
        {
            startInfo.FileName = executablePath;
            startInfo.Arguments = "--version";
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Broken candidates must never take down the Launcher.
        }
    }

    private static async Task WaitForExitSafelyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // The process may have exited while it was being terminated.
        }
    }

    private static async Task WaitForExitSafelyAsync(Task completionTask)
    {
        try
        {
            await completionTask;
        }
        catch
        {
            // A terminated candidate may have failed while its output was closing.
        }
    }
}
