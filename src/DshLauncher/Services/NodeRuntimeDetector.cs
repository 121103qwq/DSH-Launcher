using System.Diagnostics;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class NodeRuntimeDetector
{
    private const int MaximumConcurrentChecks = 4;

    public Task<NodeRuntimeInfo> DetectAsync(CancellationToken cancellationToken = default) =>
        DetectAsync(preferredPath: null, requiredEngine: null, cancellationToken);

    public Task<NodeRuntimeInfo> DetectAsync(
        string? preferredPath,
        CancellationToken cancellationToken = default) =>
        DetectAsync(preferredPath, requiredEngine: null, cancellationToken);

    public async Task<NodeRuntimeInfo> DetectAsync(
        string? preferredPath,
        string? requiredEngine,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var normalizedPreferredPath = NormalizePreferredPath(preferredPath);
            if (normalizedPreferredPath is not null && File.Exists(normalizedPreferredPath))
            {
                var preferredVersion = await ReadVersionAsync(normalizedPreferredPath, cancellationToken);
                if (preferredVersion is not null)
                {
                    return new NodeRuntimeInfo(true, normalizedPreferredPath, preferredVersion, null);
                }
            }
        }

        using var gate = new SemaphoreSlim(MaximumConcurrentChecks);
        var checks = GetCandidates()
            .Select(candidate => InspectCandidateAsync(candidate, gate, cancellationToken))
            .ToArray();
        var available = (await Task.WhenAll(checks))
            .Where(static item => item.HasValue)
            .Select(static item => item!.Value)
            .ToArray();
        var best = SelectBestCandidate(available, requiredEngine);
        if (best is { } selected)
        {
            return new NodeRuntimeInfo(true, selected.Path, selected.Version, null);
        }

        return NodeRuntimeInfo.Missing("PATH、Windows 常见安装目录和 DeepSeek Desktop 中都没有可用的 node.exe。");
    }

    internal static (string Path, string Version)? SelectBestCandidate(
        IReadOnlyList<(string Path, string Version)> available,
        string? requiredEngine)
    {
        var ordered = available
            .Where(static item => NodeRuntimeInfo.TryParseVersion(item.Version, out _))
            .OrderByDescending(static item =>
            {
                NodeRuntimeInfo.TryParseVersion(item.Version, out var parsed);
                return parsed;
            })
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requiredEngine))
        {
            var compatible = ordered.FirstOrDefault(item =>
                NodeRuntimeInfo.EvaluateCompatibility(item.Version, requiredEngine)
                    == NodeRuntimeCompatibility.Compatible);
            if (compatible.Path is not null)
            {
                return compatible;
            }
        }

        return ordered[0];
    }

    private static async Task<(string Path, string Version)?> InspectCandidateAsync(
        string candidate,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
            {
                return null;
            }

            var version = await ReadVersionAsync(candidate, cancellationToken);
            return version is null ? null : (candidate, version);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? NormalizePreferredPath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    internal static IEnumerable<string> GetCandidates(
        IReadOnlyList<DeepSeekDesktopInstallation>? desktopInstallations = null,
        IReadOnlyList<string>? pathDirectories = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in pathDirectories ?? RuntimeSearchPaths.GetCurrentDirectories())
        {
            var candidate = Path.Combine(directory, "node.exe");
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
            // Official Node.js MSI installs to <ProgramFiles>\nodejs; zip/nvm
            // style installs typically live under <dir>\Programs\nodejs. Both
            // must be found without a Launcher restart after installation.
            foreach (var relative in new[] { Path.Combine("Programs", "nodejs"), "nodejs" })
            {
                var candidate = Path.Combine(directory, relative, "node.exe");
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var installation in desktopInstallations ?? DeepSeekDesktopDetector.DetectInstallations())
        {
            if (seen.Add(installation.NodeExecutablePath))
            {
                yield return installation.NodeExecutablePath;
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

            var normalized = firstLine.StartsWith('v') ? firstLine[1..] : firstLine;
            return NodeRuntimeInfo.TryParseVersion(normalized, out _) ? normalized : null;
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
