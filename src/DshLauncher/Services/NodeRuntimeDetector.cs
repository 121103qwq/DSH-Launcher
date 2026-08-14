using System.Diagnostics;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class NodeRuntimeDetector
{
    public async Task<NodeRuntimeInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        var available = new List<(string Path, string Version)>();
        foreach (var candidate in GetCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = await ReadVersionAsync(candidate, cancellationToken);
            if (version is not null)
            {
                available.Add((candidate, version));
            }
        }

        var best = available
            .OrderByDescending(static item => Version.TryParse(item.Version, out var parsed) ? parsed : new Version(0, 0))
            .FirstOrDefault();
        if (best.Path is not null)
        {
            return new NodeRuntimeInfo(true, best.Path, best.Version, null);
        }

        return NodeRuntimeInfo.Missing("PATH 和 Windows 常见安装目录中都没有可用的 node.exe。");
    }

    private static IEnumerable<string> GetCandidates()
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

            var candidate = Path.Combine(trimmed, "node.exe");
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        var commonDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var directory in commonDirectories.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = Path.Combine(directory, "Programs", "nodejs", "node.exe");
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static async Task<string?> ReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            }
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
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var completedTask = await Task.WhenAny(completionTask, timeoutTask);

            if (completedTask != completionTask)
            {
                TryKill(process);
                await WaitForExitSafelyAsync(completionTask);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            await completionTask;
            var output = outputTask.Result.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var firstLine = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim();

            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return null;
            }

            return firstLine.StartsWith('v') ? firstLine[1..] : firstLine;
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
            // Detection must never take down the Launcher because a candidate is broken.
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
            // The process may have already exited or refused a second wait.
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
            // The process may have failed while being terminated.
        }
    }
}
