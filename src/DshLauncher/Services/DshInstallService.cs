using System.Diagnostics;
using System.Text;
using System.IO;
using DshLauncher.Models;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

public sealed class DshInstallService
{
    public const string OfficialRegistry = "https://registry.npmjs.org";
    public const string ChinaRegistry = "https://registry.npmmirror.com";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim VersionInstallGate = new(1, 1);

    /// <summary>
    /// 全局 DSh 只在 Installed 目标（或未指定目标的设置页）缺失时安装；
    /// Source 实例使用项目自带 CLI，不需要全局 @deepseek-ai/dsh。
    /// </summary>
    public static bool ShouldInstallGlobalDSh(bool dshAvailable, InstanceKind? targetKind) =>
        !dshAvailable && (targetKind is null or InstanceKind.Installed);

    public async Task<DshInstallResult> InstallAsync(
        NodeRuntimeInfo nodeRuntime,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(nodeRuntime, registry: null, installDirectory: null, cancellationToken);
    }

    public async Task<DshInstallResult> InstallAsync(
        NodeRuntimeInfo nodeRuntime,
        string? registry,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(nodeRuntime, registry, installDirectory: null, cancellationToken);
    }

    public async Task<DshInstallResult> InstallAsync(
        NodeRuntimeInfo nodeRuntime,
        string? registry,
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        return await InstallVersionAsync(
            nodeRuntime,
            packageVersion: null,
            registry,
            installDirectory,
            cancellationToken);
    }

    public async Task<DshInstallResult> InstallVersionAsync(
        NodeRuntimeInfo nodeRuntime,
        string? packageVersion,
        string? registry,
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!nodeRuntime.IsAvailable || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath))
        {
            return DshInstallResult.Failure("未找到可用的 Node.js，不能执行 DSh 安装。");
        }

        if (registry is not null
            && !string.Equals(registry, OfficialRegistry, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(registry, ChinaRegistry, StringComparison.OrdinalIgnoreCase))
        {
            return DshInstallResult.Failure("DSh 安装源不受支持，只能使用 npm 官方源或 npmmirror 国内镜像。");
        }

        string? normalizedInstallDirectory;
        try
        {
            normalizedInstallDirectory = NormalizeInstallDirectory(installDirectory);
        }
        catch (ArgumentException ex)
        {
            return DshInstallResult.Failure(ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(packageVersion) && !IsSafePackageVersion(packageVersion))
        {
            return DshInstallResult.Failure("DSh 版本号格式无效。 ");
        }

        var npmPath = FindNpm(nodeRuntime.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(packageVersion)
            && !string.IsNullOrWhiteSpace(normalizedInstallDirectory))
        {
            return await InstallExactVersionAsync(
                npmPath,
                packageVersion,
                registry,
                normalizedInstallDirectory,
                cancellationToken);
        }

        return await RunNpmInstallAsync(
            npmPath,
            packageVersion,
            registry,
            normalizedInstallDirectory,
            cancellationToken);
    }

    private static async Task<DshInstallResult> InstallExactVersionAsync(
        string npmPath,
        string packageVersion,
        string? registry,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        await VersionInstallGate.WaitAsync(cancellationToken);
        string? stagingDirectory = null;
        try
        {
            if (InstalledVersionMatches(installDirectory, packageVersion))
            {
                return DshInstallResult.Success($"DSh {packageVersion} 已安装。");
            }

            stagingDirectory = CreateVersionStagingDirectory(installDirectory);
            var result = await RunNpmInstallAsync(
                npmPath,
                packageVersion,
                registry,
                stagingDirectory,
                cancellationToken);
            if (!result.IsSuccess)
            {
                return result;
            }

            if (!InstalledVersionMatches(stagingDirectory, packageVersion))
            {
                return DshInstallResult.Failure(
                    $"npm 安装完成，但没有找到 DSh {packageVersion} 的有效运行目录。",
                    output: result.Output);
            }

            PromoteVersionDirectory(stagingDirectory, installDirectory);
            stagingDirectory = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DshInstallResult.Failure($"保存 DSh {packageVersion} 失败：{ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(stagingDirectory))
            {
                TryDeleteDirectory(stagingDirectory);
            }

            VersionInstallGate.Release();
        }
    }

    private static async Task<DshInstallResult> RunNpmInstallAsync(
        string npmPath,
        string? packageVersion,
        string? registry,
        string? installDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(npmPath, registry, installDirectory, packageVersion);
        DshRuntimeCommandFactory.ApplyProxyFallback(startInfo);
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

    internal static string CreateVersionStagingDirectory(string installDirectory)
    {
        var normalized = NormalizeInstallDirectory(installDirectory)
            ?? throw new ArgumentException("DSh 安装位置不能为空。", nameof(installDirectory));
        var parent = Path.GetDirectoryName(normalized)
            ?? throw new ArgumentException("DSh 安装位置必须有父目录。", nameof(installDirectory));
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $".{Path.GetFileName(normalized)}.install-{Guid.NewGuid():N}");
    }

    internal static void PromoteVersionDirectory(string stagingDirectory, string installDirectory)
    {
        var staging = Path.GetFullPath(stagingDirectory);
        var target = Path.GetFullPath(installDirectory);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("DSh 版本目录必须有父目录。");
        if (!string.Equals(Path.GetDirectoryName(staging), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(staging).StartsWith($".{Path.GetFileName(target)}.install-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DSh 临时安装目录不属于目标版本目录。");
        }

        var backup = Path.Combine(parent, $".{Path.GetFileName(target)}.backup-{Guid.NewGuid():N}");
        var movedExisting = false;
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                movedExisting = true;
            }

            Directory.Move(staging, target);
        }
        catch
        {
            if (movedExisting && !Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }

        if (movedExisting)
        {
            TryDeleteDirectory(backup);
        }
    }

    private static bool InstalledVersionMatches(string installDirectory, string packageVersion)
    {
        var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(installDirectory);
        var installedVersion = packageRoot is null
            ? null
            : DshRuntimeDetector.TryReadPackageVersion(packageRoot);
        return string.Equals(installedVersion, packageVersion, StringComparison.OrdinalIgnoreCase)
            && DshRuntimeCommandFactory.IsUsable(
                packageRoot is null ? null : DshRuntimeDetector.CreateLaunchSpecForPackageRoot(packageRoot));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A stale temp/backup directory is safer than deleting an uncertain target.
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

    internal static string? NormalizeInstallDirectory(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(installDirectory.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"DSh 安装位置无效：{ex.Message}", nameof(installDirectory));
        }

        var root = Path.GetPathRoot(normalized)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("DSh 安装位置不能是磁盘根目录。", nameof(installDirectory));
        }

        if (File.Exists(normalized))
        {
            throw new ArgumentException("DSh 安装位置必须是文件夹，不能是现有文件。", nameof(installDirectory));
        }

        return normalized;
    }

    internal static bool IsSafePackageVersion(string? packageVersion) =>
        !string.IsNullOrWhiteSpace(packageVersion)
        && Regex.IsMatch(packageVersion, "^[0-9A-Za-z][0-9A-Za-z.+-]{0,79}$", RegexOptions.CultureInvariant);

    internal static ProcessStartInfo CreateStartInfo(
        string npmPath,
        string? registry,
        string? installDirectory,
        string? packageVersion = null)
    {
        var packageSpec = string.IsNullOrWhiteSpace(packageVersion)
            ? "@deepseek-ai/dsh"
            : $"@deepseek-ai/dsh@{packageVersion}";
        var commandArguments = $"install --global {packageSpec}"
            + (string.IsNullOrWhiteSpace(registry) ? string.Empty : $" --registry={registry}");
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            // npm on Windows links global command shims directly into this prefix.
            // Passing the path through the environment avoids cmd.exe quoting and
            // injection problems for user-selected paths containing shell symbols.
            startInfo.Environment["NPM_CONFIG_PREFIX"] = installDirectory;
        }

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
