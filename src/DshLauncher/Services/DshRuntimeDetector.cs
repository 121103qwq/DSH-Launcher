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
        var scan = await ScanAsync(
            preferredInstallDirectory,
            DeepSeekDesktopDetector.DetectInstallations(),
            cancellationToken);
        return scan.PrimaryRuntime;
    }

    internal async Task<DshRuntimeInfo> DetectAsync(
        string? preferredInstallDirectory,
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations,
        CancellationToken cancellationToken = default)
    {
        var scan = await ScanAsync(
            preferredInstallDirectory,
            desktopInstallations,
            cancellationToken);
        return scan.PrimaryRuntime;
    }

    public async Task<IReadOnlyList<DshRuntimeInfo>> DetectAllAsync(
        string? preferredInstallDirectory,
        CancellationToken cancellationToken = default)
    {
        var scan = await ScanAsync(
            preferredInstallDirectory,
            DeepSeekDesktopDetector.DetectInstallations(),
            cancellationToken);
        return scan.Runtimes;
    }

    internal Task<DshRuntimeScanResult> ScanAsync(
        string? preferredInstallDirectory,
        CancellationToken cancellationToken = default) =>
        ScanAsync(
            preferredInstallDirectory,
            DeepSeekDesktopDetector.DetectInstallations(),
            cancellationToken);

    internal async Task<DshRuntimeScanResult> ScanAsync(
        string? preferredInstallDirectory,
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations,
        CancellationToken cancellationToken = default)
    {
        var foundCandidate = false;
        var runtimes = new List<DshRuntimeInfo>();
        var seenPackageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in GetCandidates(preferredInstallDirectory, desktopInstallations))
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

            var desktopInstallation = desktopInstallations.FirstOrDefault(item => string.Equals(
                item.DshExecutablePath,
                candidate,
                StringComparison.OrdinalIgnoreCase));
            var reportedVersion = await ReadVersionAsync(
                candidate,
                desktopInstallation?.NodeExecutablePath,
                cancellationToken);
            if (!string.Equals(reportedVersion, packageVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedPackageRoot = Path.GetFullPath(packageRoot);
            if (!seenPackageRoots.Add(normalizedPackageRoot))
            {
                continue;
            }

            runtimes.Add(new DshRuntimeInfo(
                true,
                candidate,
                packageVersion,
                normalizedPackageRoot,
                null,
                TryReadNodeEngine(packageRoot),
                desktopInstallation?.DesktopVersion,
                desktopInstallation?.NodeExecutablePath));
        }

        return new DshRuntimeScanResult(runtimes, foundCandidate);
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
        if (IsDshPackageRoot(nested))
        {
            return nested;
        }

        return DeepSeekDesktopDetector.TryDetect(normalized)?.DshPackageRoot;
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
        var scopeDirectory = Directory.GetParent(normalized)?.FullName;
        var nodeModulesDirectory = scopeDirectory is null
            ? null
            : Directory.GetParent(scopeDirectory)?.FullName;
        if (nodeModulesDirectory is null)
        {
            return null;
        }

        var prefixDirectory = Directory.GetParent(nodeModulesDirectory)?.FullName;
        var searchDirectories = new[]
        {
            Path.Combine(nodeModulesDirectory, ".bin"),
            prefixDirectory,
            nodeModulesDirectory
        };
        foreach (var directory in searchDirectories.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var fileName in new[] { "dsh.cmd", "dsh.exe", "dsh" })
            {
                var candidate = Path.Combine(directory!, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
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
        return GetCandidates(
            preferredInstallDirectory,
            DeepSeekDesktopDetector.DetectInstallations());
    }

    internal static IEnumerable<string> GetCandidates(
        string? preferredInstallDirectory,
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations)
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

        foreach (var installation in desktopInstallations)
        {
            if (seen.Add(installation.DshExecutablePath))
            {
                yield return installation.DshExecutablePath;
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

    private static async Task<string?> ReadVersionAsync(
        string executablePath,
        string? bundledNodeExecutablePath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, bundledNodeExecutablePath)
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

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string? bundledNodeExecutablePath = null)
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

        if (!string.IsNullOrWhiteSpace(bundledNodeExecutablePath))
        {
            var inheritedPath = startInfo.Environment.TryGetValue("PATH", out var configuredPath)
                ? configuredPath ?? string.Empty
                : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            startInfo.Environment["PATH"] = DshInstanceRunner.BuildPathWithNodeDirectory(
                bundledNodeExecutablePath,
                inheritedPath);
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

internal sealed record DshRuntimeScanResult(
    IReadOnlyList<DshRuntimeInfo> Runtimes,
    bool FoundCandidate)
{
    public DshRuntimeInfo PrimaryRuntime => Runtimes.FirstOrDefault()
        ?? DshRuntimeInfo.Missing(FoundCandidate
            ? "找到了 DSh 命令，但安装包无法解析、命令不能运行，或命令版本与安装包不一致。请使用“准备运行环境”修复。"
            : "PATH、所选安装位置和 DeepSeek Desktop 中没有可运行的 DSh 启动文件。");
}
