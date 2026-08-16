using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Downloads and installs a compatible Windows x64 Node.js via its official
/// installer. The Launcher itself never bundles or depends on Node.js; this
/// service only prepares the machine for DSh when the user explicitly asks.
/// </summary>
public sealed class NodeInstallService
{
    public const string OfficialDistBase = "https://nodejs.org/dist";
    public const string MirrorDistBase = "https://npmmirror.com/mirrors/node";

    // Pinned compatible LTS fallback when the live version index is unreachable.
    public const string DefaultVersion = "v22.23.2";

    private const string WindowsMsiFileNameSuffix = "-x64.msi";
    private const long MinimumMsiBytes = 1_000_000;
    private const int InstallTimeoutExitCode = -2;
    private const int InstallCancelledExitCode = -3;
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(20);
    private readonly HttpClient _httpClient;

    // 超时后未被终止、仍在后台运行的 msiexec；它退出前不允许再次启动
    // Node 安装，避免两次 Windows Installer 安装同一产品互相重叠。
    // internal 仅供 SelfTest 注入模拟进程；句柄由延迟清理任务负责释放。
    internal Process? LingeringInstaller;

    /// <summary>残留安装进程仍在运行时为 true；UI 据此保持关闭保护。</summary>
    internal bool HasLingeringInstaller => IsProcessAlive(LingeringInstaller);

    /// <summary>残留安装进程退出且其 MSI 清理完成后触发（延迟清理任务内触发）。</summary>
    internal event Action? LingeringInstallerCompleted;

    public NodeInstallService()
        : this(new HttpClient { Timeout = HttpTimeout })
    {
    }

    internal NodeInstallService(HttpMessageHandler handler)
        : this(new HttpClient(handler) { Timeout = HttpTimeout })
    {
    }

    private NodeInstallService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/0.1");
    }

    public async Task<NodeInstallResult> InstallAsync(
        string distBase,
        IProgress<NodeDownloadProgress>? progress = null,
        Action? onInstallStarted = null,
        CancellationToken cancellationToken = default,
        string? requiredNodeEngine = null)
    {
        if (!IsSupportedDistBase(distBase))
        {
            return NodeInstallResult.Failure("Node.js 下载源不受支持，只能使用官方源或 npmmirror 国内镜像。");
        }

        if (IsProcessAlive(LingeringInstaller))
        {
            return NodeInstallResult.Failure(
                "上一次 Node.js 安装仍在后台运行（超时后不会被强制终止），请等待安装完成后再重新准备运行环境。");
        }

        // 已退出的残留安装程序只清引用，不释放句柄；延迟清理任务仍持有它。
        LingeringInstaller = null;

        var version = await ResolveVersionAsync(distBase, requiredNodeEngine, cancellationToken);
        if (version is null)
        {
            return NodeInstallResult.Failure(string.IsNullOrWhiteSpace(requiredNodeEngine)
                ? "无法解析兼容的 Node.js LTS 版本，请稍后重试或改用国内镜像。"
                : $"无法解析满足要求（{requiredNodeEngine}）的 Node.js 版本，请稍后重试或改用国内镜像。");
        }

        var canonicalFileName = $"node-{version}{WindowsMsiFileNameSuffix}";
        var downloadUrl = $"{distBase}/{version}/{canonicalFileName}";
        var destinationDirectory = Path.Combine(Path.GetTempPath(), "DSH Launcher");
        // 每次调用使用唯一文件名，多个 Launcher 进程并发准备同一版本时互不干扰。
        var destination = Path.Combine(destinationDirectory, $"node-{version}-{Guid.NewGuid():N}{WindowsMsiFileNameSuffix}");

        InstallerRun? installerRun = null;
        try
        {
            await DownloadAsync(new Uri(downloadUrl), destination, progress, cancellationToken);
            // MSI 会以管理员权限执行：下载源或镜像被污染时，仅凭长度校验
            // 不足以放行，必须先验证 Node.js 官方发布者的 Authenticode 签名。
            if (!MsiAuthenticodeVerifier.IsTrustedNodeInstaller(destination))
            {
                return NodeInstallResult.Failure(
                    "下载的 Node.js 安装程序未通过官方发布者签名验证，已停止安装。请更换下载源或手动安装。");
            }

            onInstallStarted?.Invoke();
            installerRun = await RunInstallerAsync(destination, cancellationToken);
            LingeringInstaller = installerRun.LingeringProcess;
            return MapExitCode(installerRun.ExitCode, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NodeInstallResult.Cancelled("Node.js 下载已取消。");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or HttpRequestException
            or TaskCanceledException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return NodeInstallResult.Failure($"Node.js 准备失败：{ex.Message}");
        }
        finally
        {
            // 清理下载的 MSI，避免每次准备在 %TEMP%\\DSH Launcher 累积安装包；
            // 超时路径下 msiexec 仍可能在后台运行，必须等它真正退出后再删除。
            CleanupInstallerFile(
                installerRun?.LingeringProcess,
                destination,
                () => LingeringInstallerCompleted?.Invoke());
        }
    }

    private static void TryDeleteInstaller(string path)
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
            // 清理失败不能掩盖真实的安装结果。
        }
    }

    // msiexec 超时后绝不会被强杀，可能仍在后台安装；此时安装包既是被占用
    // 的源文件，也可能仍被安装程序需要，清理必须推迟到进程真正退出之后。
    internal static void CleanupInstallerFile(
        System.Diagnostics.Process? installer,
        string path,
        Action? onLingeringCleanupCompleted = null)
    {
        var running = IsProcessAlive(installer);

        if (running && installer is not null)
        {
            var lingering = installer;
            _ = Task.Run(async () =>
            {
                try
                {
                    await lingering.WaitForExitAsync();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 进程已退出或无法等待时继续尝试清理。
                }
                finally
                {
                    lingering.Dispose();
                    TryDeleteInstaller(path);
                    onLingeringCleanupCompleted?.Invoke();
                }
            });
            return;
        }

        installer?.Dispose();
        TryDeleteInstaller(path);
        // 回调只在“确有残留安装进程、等它退出后补做清理”的分支触发；
        // 正常安装路径没有残留进程，不应发出完成通知。
    }

    /// <summary>
    /// 判断进程是否仍在运行：句柄已被延迟清理释放或进程已退出视为不在运行；
    /// 查询失败（如权限不足）时保守地视为仍在运行，宁可阻止重复安装。
    /// </summary>
    internal static bool IsProcessAlive(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    internal static NodeInstallResult MapExitCode(int exitCode, string version)
    {
        if (exitCode is 0 or 3010)
        {
            var expectedNode = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs",
                "node.exe");
            return NodeInstallResult.Success(expectedNode, version.TrimStart('v', 'V'));
        }

        return exitCode switch
        {
            InstallCancelledExitCode => NodeInstallResult.Cancelled(
                "Node.js 安装阶段已取消等待；Windows Installer 可能仍在后台完成安装。"),
            InstallTimeoutExitCode => NodeInstallResult.Failure(
                "Node.js 安装程序超过 10 分钟仍未结束，请检查系统安装窗口。", exitCode),
            _ => NodeInstallResult.Failure($"Node.js 安装失败，msiexec 退出码 {exitCode}。", exitCode)
        };
    }

    internal async Task<NodeDownloadProgress> DownloadAsync(
        Uri url,
        string destination,
        IProgress<NodeDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new NodeDownloadProgress(
                        received,
                        total,
                        total is > 0 ? received * 100.0 / total.Value : null));
                }
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(temporary, destination);
            var actual = new FileInfo(destination).Length;
            if (total is { } expected && actual != expected)
            {
                throw new IOException($"下载不完整：预期 {expected} 字节，实际 {actual} 字节。");
            }

            if (actual < MinimumMsiBytes)
            {
                throw new IOException("下载文件过小，可能不是有效的 Node.js 安装程序。");
            }

            return new NodeDownloadProgress(total ?? actual, total, total is > 0 ? 100 : null);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static string? SelectLtsVersion(string indexJson, string? requiredNodeEngine = null)
    {
        try
        {
            using var document = JsonDocument.Parse(indexJson);
            // 端点可能返回合法 JSON 但形状错误（错误对象、数组内混入原始值），
            // 这类索引不可用，交由固定版本兜底，不能让 InvalidOperationException 逃出。
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("version", out var versionElement)
                    || versionElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var isLts = entry.TryGetProperty("lts", out var lts)
                    && lts.ValueKind is JsonValueKind.True or JsonValueKind.String;
                if (!isLts)
                {
                    continue;
                }

                var version = versionElement.GetString()!;
                if (string.IsNullOrWhiteSpace(requiredNodeEngine))
                {
                    // 无明确要求时使用官方 installed DSh 兼容范围作为默认策略。
                    var major = GetMajor(version);
                    var minor = GetMinor(version);
                    if ((major == 22 && minor >= 19) || major >= 24)
                    {
                        return version;
                    }
                }
                else if (NodeRuntimeInfo.EvaluateCompatibility(version, requiredNodeEngine)
                    == NodeRuntimeCompatibility.Compatible)
                {
                    return version;
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to the pinned compatible LTS constant.
        }

        return null;
    }

    internal static bool DefaultVersionSatisfies(string? requiredNodeEngine) =>
        string.IsNullOrWhiteSpace(requiredNodeEngine)
        || NodeRuntimeInfo.EvaluateCompatibility(DefaultVersion, requiredNodeEngine) == NodeRuntimeCompatibility.Compatible;

    private async Task<string?> ResolveVersionAsync(string distBase, string? requiredNodeEngine, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _httpClient.GetStringAsync($"{distBase}/index.json", cancellationToken);
            var selected = SelectLtsVersion(json, requiredNodeEngine);
            if (selected is not null)
            {
                return selected;
            }

            // index 中没有满足要求的 LTS 时，只在固定版本本身满足要求时才回退，
            // 否则停止安装，避免装出与 Source/Installed engine 不兼容的 Node。
            return DefaultVersionSatisfies(requiredNodeEngine) ? DefaultVersion : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DefaultVersionSatisfies(requiredNodeEngine) ? DefaultVersion : null;
        }
    }

    private static int GetMajor(string version)
    {
        var trimmed = version.TrimStart('v', 'V');
        var separator = trimmed.IndexOf('.');
        var majorText = separator < 0 ? trimmed : trimmed[..separator];
        return int.TryParse(majorText, out var major) ? major : -1;
    }

    private static int GetMinor(string version)
    {
        var trimmed = version.TrimStart('v', 'V');
        var segments = trimmed.Split('.');
        return segments.Length > 1 && int.TryParse(segments[1], out var minor) ? minor : -1;
    }

    private static bool IsSupportedDistBase(string distBase) =>
        string.Equals(distBase, OfficialDistBase, StringComparison.OrdinalIgnoreCase)
        || string.Equals(distBase, MirrorDistBase, StringComparison.OrdinalIgnoreCase);

    private sealed record InstallerRun(int ExitCode, Process? LingeringProcess);

    private static async Task<InstallerRun> RunInstallerAsync(string msiPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{msiPath}\" /qn /norestart",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Node.js 安装程序未能启动。");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var exitTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(InstallTimeout, linked.Token);
            var completed = await Task.WhenAny(exitTask, timeoutTask);
            if (completed == exitTask)
            {
                await exitTask;
                var exitCode = process.ExitCode;
                process.Dispose();
                return new InstallerRun(exitCode, null);
            }

            // Never kill msiexec: a cancelled wait is a user abort, otherwise
            // the full 10-minute timeout elapsed. The still-running process is
            // handed back so the caller can defer MSI cleanup until it exits.
            return new InstallerRun(
                cancellationToken.IsCancellationRequested
                    ? InstallCancelledExitCode
                    : InstallTimeoutExitCode,
                process);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动 Node.js 安装程序（可能取消了管理员授权）：{ex.Message}");
        }
    }
}
