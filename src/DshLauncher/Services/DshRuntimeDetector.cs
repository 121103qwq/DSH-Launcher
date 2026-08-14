using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class DshRuntimeDetector
{
    private static readonly TimeSpan CandidateTimeout = TimeSpan.FromSeconds(2);

    public async Task<DshRuntimeInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var candidate in GetCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = await ReadVersionAsync(candidate, cancellationToken);
            if (version is null)
            {
                continue;
            }

            return new DshRuntimeInfo(
                true,
                candidate,
                version,
                TryFindPackageRoot(candidate),
                null);
        }

        return DshRuntimeInfo.Missing("PATH 中没有可运行的 dsh.cmd 或 dsh.exe。");
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .FirstOrDefault(static line => line.Length > 0);
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
