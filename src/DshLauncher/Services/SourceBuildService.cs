using System.Diagnostics;
using System.IO;
using System.Text;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class SourceBuildService
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(10);
    private readonly Func<string, string?>? _commandResolver;
    private readonly TimeSpan _commandTimeout;

    public SourceBuildService(
        Func<string, string?>? commandResolver = null,
        TimeSpan? commandTimeout = null)
    {
        _commandResolver = commandResolver;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    public async Task<SourceBuildResult> PrepareAsync(
        SourceProjectInfo project,
        NodeRuntimeInfo nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        if (!project.IsValid || !project.IsDshSource)
        {
            return SourceBuildResult.Failure(project.Error ?? "Source 项目无法识别。");
        }

        if (!project.HasBuildScript)
        {
            return SourceBuildResult.Failure("Source 项目缺少 build 脚本，不能生成可启动的 DSh 入口。");
        }

        if (!nodeRuntime.IsAvailable || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath))
        {
            return SourceBuildResult.Failure("Source 构建需要可用的 Node.js。");
        }

        var nodeCompatibility = nodeRuntime.GetCompatibility(project.NodeEngine);
        if (nodeCompatibility != NodeRuntimeCompatibility.Compatible)
        {
            return SourceBuildResult.Failure(
                $"当前 Node.js {nodeRuntime.VersionText} 的兼容状态为 {nodeCompatibility}，不满足 Source 的 engines.node 要求：{project.NodeEngine ?? "未声明"}。请切换到兼容版本。");
        }

        var packageManager = NormalizePackageManager(project.PackageManager);
        if (packageManager is null)
        {
            return SourceBuildResult.Failure("Source 项目没有可识别的包管理器。");
        }

        var command = ResolveCommand(packageManager, nodeRuntime.ExecutablePath);
        if (command is null)
        {
            return SourceBuildResult.Failure(
                $"找不到 {packageManager}。请先安装该包管理器，或确保它位于 PATH 中。");
        }

        var output = new StringBuilder();
        var dependenciesInstalled = project.DependenciesPresent;
        if (!dependenciesInstalled)
        {
            var install = await RunCommandAsync(
                command,
                project.RootPath,
                new[] { "install" },
                nodeRuntime.ExecutablePath,
                cancellationToken);
            AppendOutput(output, install.Output);
            if (!install.IsSuccess)
            {
                return SourceBuildResult.Failure(
                    install.Error ?? "Source 依赖安装失败。",
                    TrimOutput(output.ToString()),
                    dependenciesInstalled: false);
            }

            dependenciesInstalled = Directory.Exists(Path.Combine(project.RootPath, "node_modules"));
            if (!dependenciesInstalled)
            {
                return SourceBuildResult.Failure(
                    "包管理器返回成功，但没有生成 node_modules，已停止构建。",
                    TrimOutput(output.ToString()),
                    dependenciesInstalled: false);
            }
        }

        var build = await RunCommandAsync(
            command,
            project.RootPath,
            new[] { "run", "build" },
            nodeRuntime.ExecutablePath,
            cancellationToken);
        AppendOutput(output, build.Output);
        if (!build.IsSuccess)
        {
            return SourceBuildResult.Failure(
                build.Error ?? "Source 构建失败。",
                TrimOutput(output.ToString()),
                dependenciesInstalled,
                buildExecuted: true);
        }

        var entrypoint = SourceProjectInspector.TryFindBuiltCliEntrypoint(project.RootPath);
        if (entrypoint is null)
        {
            return SourceBuildResult.Failure(
                "Source 构建返回成功，但没有找到 apps/cli 的构建入口（预期为 lib/bin.js 或 dist/bin.js）。",
                TrimOutput(output.ToString()),
                dependenciesInstalled,
                buildExecuted: true);
        }

        return SourceBuildResult.Success(
            entrypoint,
            TrimOutput(output.ToString()),
            dependenciesInstalled,
            buildExecuted: true);
    }

    private ResolvedCommand? ResolveCommand(string packageManager, string nodePath)
    {
        var resolved = _commandResolver?.Invoke(packageManager)
            ?? ResolveDefaultCommand(packageManager, nodePath);
        if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
        {
            return new ResolvedCommand(resolved!, Array.Empty<string>());
        }

        if (_commandResolver is not null)
        {
            return null;
        }

        var corepack = FindAdjacentOrOnPath("corepack", nodePath);
        return corepack is null
            ? null
            : new ResolvedCommand(corepack, new[] { packageManager });
    }

    private static string? ResolveDefaultCommand(string packageManager, string nodePath)
    {
        return FindAdjacentOrOnPath(packageManager, nodePath);
    }

    private static string? FindAdjacentOrOnPath(string command, string nodePath)
    {
        var nodeDirectory = Path.GetDirectoryName(nodePath);
        var names = OperatingSystem.IsWindows()
            ? new[] { $"{command}.cmd", $"{command}.exe", command }
            : new[] { command };

        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(nodeDirectory))
            {
                var adjacent = Path.Combine(nodeDirectory, name);
                if (File.Exists(adjacent))
                {
                    return adjacent;
                }
            }

            var fromPath = FindOnPath(name);
            if (fromPath is not null)
            {
                return fromPath;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<CommandResult> RunCommandAsync(
        ResolvedCommand command,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string nodePath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(command, workingDirectory, arguments, nodePath)
        };

        try
        {
            if (!process.Start())
            {
                return CommandResult.Failure("包管理器进程无法启动。", "");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(_commandTimeout, cancellationToken);
            var completed = await Task.WhenAny(exitTask, timeoutTask);
            if (completed != exitTask)
            {
                TryKill(process);
                await WaitForExitSafelyAsync(process);
                cancellationToken.ThrowIfCancellationRequested();
                return CommandResult.Failure(
                    $"包管理器命令超过 {_commandTimeout.TotalMinutes:0.#} 分钟，已终止。",
                    TrimOutput(await ReadOutputAsync(stdoutTask, stderrTask)));
            }

            await exitTask;
            var output = TrimOutput(await ReadOutputAsync(stdoutTask, stderrTask));
            return process.ExitCode == 0
                ? CommandResult.Success(output)
                : CommandResult.Failure($"包管理器命令退出码为 {process.ExitCode}。", output);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            return CommandResult.Failure($"执行包管理器命令失败：{ex.Message}", "");
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        ResolvedCommand command,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string nodePath)
    {
        var allArguments = command.PrefixArguments.Concat(arguments).ToArray();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (Path.GetExtension(command.ExecutablePath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(command.ExecutablePath).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /c \"\"{command.ExecutablePath}\" {string.Join(' ', allArguments.Select(QuoteForCommandLine))}\"";
        }
        else
        {
            startInfo.FileName = command.ExecutablePath;
            foreach (var argument in allArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        var nodeDirectory = Path.GetDirectoryName(nodePath);
        if (!string.IsNullOrWhiteSpace(nodeDirectory))
        {
            var existingPath = startInfo.Environment["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            startInfo.Environment["PATH"] = nodeDirectory + Path.PathSeparator + existingPath;
        }

        return startInfo;
    }

    private static string QuoteForCommandLine(string value)
    {
        return value.Contains(' ') || value.Contains('\t') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private static string? NormalizePackageManager(string? packageManager) => packageManager?.Trim().ToLowerInvariant() switch
    {
        "npm" => "npm",
        "pnpm" => "pnpm",
        "yarn" => "yarn",
        "bun" => "bun",
        _ => null
    };

    private static async Task<string> ReadOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        var output = await stdoutTask;
        var error = await stderrTask;
        return string.Join(Environment.NewLine, new[] { output, error }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void AppendOutput(StringBuilder target, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        if (target.Length > 0)
        {
            target.AppendLine();
        }

        target.Append(output);
    }

    private static string TrimOutput(string output)
    {
        const int maxLength = 12000;
        return output.Length <= maxLength ? output : output[^maxLength..];
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
            // Cleanup is best effort and must not mask the original error.
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
            // The process may already have exited while being terminated.
        }
    }

    private sealed record ResolvedCommand(string ExecutablePath, IReadOnlyList<string> PrefixArguments);

    private sealed record CommandResult(bool IsSuccess, string? Error, string Output)
    {
        public static CommandResult Success(string output) => new(true, null, output);

        public static CommandResult Failure(string error, string output) => new(false, error, output);
    }
}
