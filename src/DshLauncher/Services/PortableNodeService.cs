using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 便携版 Node.js（免管理员、不写系统 PATH、不改注册表、不动已有 Node）。
/// 借鉴 MarcoG-h/DSH-Launcher 的 runtime.ts：从官方源/npmmirror 下载
/// node-vX-win-x64.zip → 解压 → 校验 node.exe 可运行 → 原子替换
/// <c>&lt;Launcher 数据根&gt;\node</c>。dsh 的社区红线是 Node ≥22.19
/// （低于该版本 node:zlib 缺 zstd、缺 AbortSignal.timeout），低于红线时自动抬到红线。
/// </summary>
public sealed class PortableNodeService
{
    /// <summary>dsh 社区红线：低于该版本 dsh 可能无法启动。</summary>
    public const string MinimumVersion = "22.19.0";

    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(15);
    private readonly NodeInstallService _installer;

    public PortableNodeService()
        : this(new NodeInstallService())
    {
    }

    internal PortableNodeService(NodeInstallService installer)
    {
        _installer = installer;
    }

    /// <summary>便携版 Node 的安装目录（Launcher 数据根下的 node\）。</summary>
    public static string DefaultInstallDirectory => new LauncherPaths().PortableNodeDirectory;

    /// <summary>
    /// 下载并安装便携版 Node。成功返回 node.exe 路径；失败/取消返回带原因的失败结果。
    /// <paramref name="targetDirectory"/> 仅供验证注入，正常调用传 null。
    /// </summary>
    public async Task<NodeInstallResult> InstallAsync(
        string distBase,
        IProgress<NodeDownloadProgress>? progress,
        CancellationToken cancellationToken,
        string? requiredNodeEngine = null,
        string? targetDirectory = null)
    {
        if (!NodeInstallService.IsSupportedDistBase(distBase))
        {
            return NodeInstallResult.Failure("Node.js 下载源不受支持，只能使用官方源或 npmmirror 国内镜像。");
        }

        var target = Path.GetFullPath(targetDirectory ?? DefaultInstallDirectory);
        var workRoot = Path.Combine(Path.GetTempPath(), "DSH Launcher", "portable-node");
        string? zipPath = null;
        string? staging = null;
        try
        {
            var resolved = await _installer.ResolveVersionAsync(distBase, requiredNodeEngine, cancellationToken);
            var version = ResolveTargetVersion(resolved, requiredNodeEngine);
            if (version is null)
            {
                return NodeInstallResult.Failure(string.IsNullOrWhiteSpace(requiredNodeEngine)
                    ? "无法解析兼容的 Node.js 版本，请稍后重试或改用国内镜像。"
                    : $"无法解析满足要求（{requiredNodeEngine}）且不低于 {MinimumVersion} 的 Node.js 版本。");
            }

            Directory.CreateDirectory(workRoot);
            zipPath = Path.Combine(workRoot, $"node-{version}-{Guid.NewGuid():N}-win-x64.zip");
            staging = Path.Combine(Path.GetDirectoryName(target)!, $".node-staging-{Guid.NewGuid():N}");

            await _installer.DownloadAsync(BuildZipUri(distBase, version), zipPath, progress, cancellationToken);
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            var extractedNode = FindExtractedNode(staging, version);
            if (extractedNode is null)
            {
                return NodeInstallResult.Failure("Node.js 压缩包解压结果不完整（缺少 node.exe），请检查网络与磁盘空间后重试。");
            }

            var actualVersion = await ReadNodeVersionAsync(extractedNode, cancellationToken);
            if (actualVersion is null)
            {
                return NodeInstallResult.Failure(
                    "便携版 Node.js 无法运行（可能被杀毒软件拦截或压缩包损坏），请重试或改用官方安装程序。");
            }

            if (!IsVersionAtLeast(actualVersion, MinimumVersion))
            {
                return NodeInstallResult.Failure(
                    $"便携版 Node.js 版本过低（{actualVersion}），dsh 需要 ≥{MinimumVersion}，请稍后重试或改用官方安装程序。");
            }

            var sourceDirectory = Path.GetDirectoryName(extractedNode)!;
            SwapIntoPlace(sourceDirectory, target);
            return NodeInstallResult.Success(Path.Combine(target, "node.exe"), actualVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NodeInstallResult.Cancelled("便携版 Node.js 下载已取消。");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            LauncherLog.Error(
                "便携版 Node.js 准备失败。",
                ErrorCodes.E1006,
                new { error = ex.Message, target });
            return NodeInstallResult.Failure($"便携版 Node.js 准备失败：{ex.Message}");
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>把已解压的 node 目录换到目标位置；失败时回滚原目录。</summary>
    internal static void SwapIntoPlace(string sourceDirectory, string targetDirectory)
    {
        var old = targetDirectory + $".old-{Guid.NewGuid():N}";
        var movedOld = false;
        if (Directory.Exists(targetDirectory))
        {
            Directory.Move(targetDirectory, old);
            movedOld = true;
        }

        try
        {
            Directory.Move(sourceDirectory, targetDirectory);
        }
        catch
        {
            if (movedOld)
            {
                try
                {
                    Directory.Move(old, targetDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 回滚失败时保留 .old- 目录，用户可手动恢复。
                }
            }

            throw;
        }

        if (movedOld)
        {
            TryDeleteDirectory(old);
        }
    }

    /// <summary>在解压目录里定位 node.exe（正常是 node-&lt;ver&gt;-win-x64\node.exe）。</summary>
    internal static string? FindExtractedNode(string stagingRoot, string version)
    {
        var expected = Path.Combine(stagingRoot, $"node-{version}-win-x64", "node.exe");
        if (File.Exists(expected))
        {
            return expected;
        }

        if (!Directory.Exists(stagingRoot))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(stagingRoot, "node-*-win-x64")
            .Select(directory => Path.Combine(directory, "node.exe"))
            .FirstOrDefault(File.Exists);
    }

    internal static Uri BuildZipUri(string distBase, string version) =>
        new($"{distBase.TrimEnd('/')}/{version}/node-{version}-win-x64.zip");

    /// <summary>
    /// 决定实际下载的版本：解析结果低于社区红线时抬到红线；抬到红线仍不满足
    /// 目标 engine 要求时返回 null（避免装出与实例要求不兼容的 Node）。
    /// </summary>
    internal static string? ResolveTargetVersion(string? resolved, string? requiredNodeEngine)
    {
        var candidate = string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (!IsVersionAtLeast(candidate, MinimumVersion))
        {
            candidate = MinimumVersion;
        }

        if (!string.IsNullOrWhiteSpace(requiredNodeEngine)
            && NodeRuntimeInfo.EvaluateCompatibility(candidate, requiredNodeEngine) != NodeRuntimeCompatibility.Compatible)
        {
            return null;
        }

        return "v" + candidate;
    }

    internal static bool IsVersionAtLeast(string? version, string minimum) =>
        NodeRuntimeInfo.TryParseVersion(version, out var parsed)
        && NodeRuntimeInfo.TryParseVersion(minimum, out var required)
        && parsed >= required;

    private static async Task<string?> ReadNodeVersionAsync(string nodeExecutable, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodeExecutable,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        try
        {
            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(VersionProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return null;
            }

            var text = (await outputTask).Trim();
            _ = errorTask;
            return text.StartsWith('v') ? text[1..] : text;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or OperationCanceledException)
        {
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程可能已退出。
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响安装结果。
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响安装结果。
        }
    }
}
