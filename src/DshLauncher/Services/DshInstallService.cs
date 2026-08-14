using System.Diagnostics;
using System.Text;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class DshInstallService
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    public async Task<DshInstallResult> InstallAsync(
        NodeRuntimeInfo nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        if (!nodeRuntime.IsAvailable || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath))
        {
            return DshInstallResult.Failure("未找到可用的 Node.js，不能执行 DSh 安装。");
        }

        var npmPath = FindNpm(nodeRuntime.ExecutablePath);
        var startInfo = CreateStartInfo(npmPath);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return DshInstallResult.Failure("npm 进程无法启动。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            var completionTask = Task.WhenAll(exitTask, outputTask, errorTask);
            var timeoutTask = Task.Delay(InstallTimeout, cancellationToken);
            var completedTask = await Task.WhenAny(completionTask, timeoutTask);

            if (completedTask != completionTask)
            {
                TryKill(process);
                await WaitForExitSafelyAsync(completionTask);
                cancellationToken.ThrowIfCancellationRequested();
                return DshInstallResult.Failure("DSh 安装超过 10 分钟，已终止 npm 进程。");
            }

            await completionTask;
            var output = outputTask.Result.Trim();
            var error = errorTask.Result.Trim();
            if (process.ExitCode != 0)
            {
                return DshInstallResult.Failure(
                    string.IsNullOrWhiteSpace(error)
                        ? $"npm 安装失败，退出码 {process.ExitCode}。"
                        : error,
                    process.ExitCode,
                    output);
            }

            return DshInstallResult.Success(output);
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
            return DshInstallResult.Failure($"执行 DSh 安装失败：{ex.Message}");
        }
    }

    private static string FindNpm(string nodeExecutablePath)
    {
        var nodeDirectory = Path.GetDirectoryName(Path.GetFullPath(nodeExecutablePath));
        if (!string.IsNullOrWhiteSpace(nodeDirectory))
        {
            foreach (var fileName in new[] { "npm.cmd", "npm.exe", "npm" })
            {
                var candidate = Path.Combine(nodeDirectory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "npm.cmd";
    }

    private static ProcessStartInfo CreateStartInfo(string npmPath)
    {
        const string commandArguments = "install --global @deepseek-ai/dsh";
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (Path.GetExtension(npmPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(npmPath).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /c \"\"{npmPath}\" {commandArguments}\"";
        }
        else
        {
            startInfo.FileName = npmPath;
            startInfo.Arguments = commandArguments;
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
            // Installation cleanup must not mask the original error.
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
            // A terminated npm process may fail while its output streams close.
        }
    }
}
