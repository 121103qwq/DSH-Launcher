using System.Diagnostics;
using System.Text;
using System.IO;
using DshLauncher.Models;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net.Http;

namespace DshLauncher.Services;

public sealed class DshInstallService
{
    public const string OfficialRegistry = "https://registry.npmjs.org";
    public const string ChinaRegistry = "https://registry.npmmirror.com";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim VersionInstallGate = new(1, 1);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;

    public DshInstallService()
        : this(SharedHttpClient)
    {
    }

    internal DshInstallService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0");
        }
    }

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
        return await InstallVersionAsync(
            nodeRuntime,
            packageVersion,
            registry,
            installDirectory,
            progress: null,
            cancellationToken);
    }

    public async Task<DshInstallResult> InstallVersionAsync(
        NodeRuntimeInfo nodeRuntime,
        string? packageVersion,
        string? registry,
        string? installDirectory,
        IProgress<DshInstallProgress>? progress,
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
                progress,
                cancellationToken);
        }

        return await RunNpmInstallAsync(
            npmPath,
            packageVersion,
            registry,
            normalizedInstallDirectory,
            cancellationToken);
    }

    private async Task<DshInstallResult> InstallExactVersionAsync(
        string npmPath,
        string packageVersion,
        string? registry,
        string installDirectory,
        IProgress<DshInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await VersionInstallGate.WaitAsync(cancellationToken);
        string? stagingDirectory = null;
        string? packageArchive = null;
        try
        {
            if (InstalledVersionMatches(installDirectory, packageVersion))
            {
                return DshInstallResult.Success($"DSh {packageVersion} 已安装。");
            }

            stagingDirectory = CreateVersionStagingDirectory(installDirectory);
            var parentDirectory = Path.GetDirectoryName(stagingDirectory)
                ?? throw new InvalidOperationException("DSh 临时安装目录必须有父目录。");
            packageArchive = Path.Combine(
                parentDirectory,
                $".dsh-{packageVersion}-{Guid.NewGuid():N}.tgz");
            await DownloadPackageAsync(
                packageVersion,
                registry ?? OfficialRegistry,
                packageArchive,
                progress,
                cancellationToken);
            progress?.Report(new DshInstallProgress(DshInstallProgressPhase.InstallingDependencies));
            var result = await RunNpmInstallAsync(
                npmPath,
                packageVersion,
                registry,
                stagingDirectory,
                cancellationToken,
                packageArchive);
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

            if (!string.IsNullOrWhiteSpace(packageArchive))
            {
                TryDeleteFile(packageArchive);
            }

            VersionInstallGate.Release();
        }
    }

    internal async Task<DshInstallProgress> DownloadPackageAsync(
        string packageVersion,
        string registry,
        string destination,
        IProgress<DshInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new DshInstallProgress(DshInstallProgressPhase.ResolvingPackage));
        var metadataUrl = BuildVersionMetadataUrl(registry, packageVersion);
        using var metadataResponse = await _httpClient.GetAsync(metadataUrl, cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();
        await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: cancellationToken);
        if (!metadata.RootElement.TryGetProperty("dist", out var dist)
            || !dist.TryGetProperty("tarball", out var tarballElement)
            || tarballElement.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(tarballElement.GetString(), UriKind.Absolute, out var tarballUri)
            || !IsAllowedTarballUri(tarballUri, registry))
        {
            throw new InvalidDataException("官方 DSh 包元数据没有可用的安全下载地址。");
        }

        var integrity = dist.TryGetProperty("integrity", out var integrityElement)
            && integrityElement.ValueKind == JsonValueKind.String
                ? integrityElement.GetString()
                : null;
        var shasum = dist.TryGetProperty("shasum", out var shasumElement)
            && shasumElement.ValueKind == JsonValueKind.String
                ? shasumElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(integrity) && string.IsNullOrWhiteSpace(shasum))
        {
            throw new InvalidDataException("官方 DSh 包元数据没有完整性校验值。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, tarballUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{destination}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new DshInstallProgress(
                        DshInstallProgressPhase.DownloadingPackage,
                        received,
                        total,
                        total is > 0 ? received * 100.0 / total.Value : null));
                }
            }

            var actual = new FileInfo(temporary).Length;
            if (actual == 0)
            {
                throw new IOException("下载的 DSh npm 包为空。");
            }

            if (total is { } expected && actual != expected)
            {
                throw new IOException($"DSh npm 包下载不完整：预期 {expected} 字节，实际 {actual} 字节。");
            }

            if (!VerifyPackageIntegrity(temporary, integrity, shasum))
            {
                throw new InvalidDataException("下载的 DSh npm 包未通过官方完整性校验。");
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(temporary, destination);
            var completed = new DshInstallProgress(
                DshInstallProgressPhase.DownloadingPackage,
                actual,
                total ?? actual,
                100);
            progress?.Report(completed);
            return completed;
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task<DshInstallResult> RunNpmInstallAsync(
        string npmPath,
        string? packageVersion,
        string? registry,
        string? installDirectory,
        CancellationToken cancellationToken,
        string? localPackagePath = null)
    {
        var startInfo = CreateStartInfo(
            npmPath,
            registry,
            installDirectory,
            packageVersion,
            localPackagePath);
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temporary package cleanup must not mask the installation result.
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
        string? packageVersion = null,
        string? localPackagePath = null)
    {
        var packageSpec = string.IsNullOrWhiteSpace(packageVersion)
            ? "@deepseek-ai/dsh"
            : $"@deepseek-ai/dsh@{packageVersion}";
        var commandPackageSpec = string.IsNullOrWhiteSpace(localPackagePath)
            ? packageSpec
            : $"\"{localPackagePath.Replace("%", "%%", StringComparison.Ordinal)}\"";
        var commandArguments = $"install --global {commandPackageSpec}"
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

    internal static string BuildVersionMetadataUrl(string registry, string packageVersion)
    {
        if (!IsSafePackageVersion(packageVersion))
        {
            throw new ArgumentException("DSh 版本号格式无效。", nameof(packageVersion));
        }

        var normalizedRegistry = registry.TrimEnd('/');
        if (!string.Equals(normalizedRegistry, OfficialRegistry, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedRegistry, ChinaRegistry, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("DSh 安装源不受支持。", nameof(registry));
        }

        return $"{normalizedRegistry}/@deepseek-ai%2fdsh/{Uri.EscapeDataString(packageVersion)}";
    }

    internal static bool IsAllowedTarballUri(Uri uri, string registry)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(registry.TrimEnd('/'), OfficialRegistry, StringComparison.OrdinalIgnoreCase))
        {
            return uri.Host.Equals("registry.npmjs.org", StringComparison.OrdinalIgnoreCase);
        }

        return uri.Host.Equals("registry.npmmirror.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".npmmirror.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool VerifyPackageIntegrity(string packagePath, string? integrity, string? shasum)
    {
        var sha512 = integrity?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => value.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(sha512))
        {
            var expected = sha512["sha512-".Length..];
            using var stream = File.OpenRead(packagePath);
            return Convert.ToBase64String(SHA512.HashData(stream)) == expected;
        }

        if (!string.IsNullOrWhiteSpace(shasum))
        {
            using var stream = File.OpenRead(packagePath);
            return Convert.ToHexString(SHA1.HashData(stream))
                .Equals(shasum.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0");
        return client;
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
