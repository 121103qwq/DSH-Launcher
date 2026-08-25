using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Reads stable GitHub Releases and performs explicit, digest-verified
/// update/rollback downloads. Installation is delegated to the downloaded
/// single-file Launcher after the current process starts shutting down.
/// </summary>
public sealed class LauncherUpdateService : IDisposable
{
    public const string RepositoryOwner = "121103qwq";
    public const string RepositoryName = "DSH-Launcher";
    public const string ReleaseAssetName = "DSH.Launcher.exe";
    public const string ApplyModeArgument = "--apply-launcher-update";

    private const long MaximumAssetBytes = 512L * 1024 * 1024;
    private const long MinimumAssetBytes = 1L * 1024 * 1024;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan OldProcessExitTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public LauncherUpdateService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = HttpTimeout };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0");
        }

        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!_client.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
        {
            _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    public static Version CurrentVersion => NormalizeVersion(
        typeof(LauncherUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public async Task<IReadOnlyList<LauncherReleaseInfo>> ReadReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases?per_page=30";
        using var response = await _client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub Releases 返回格式无效。");
        }

        var releases = new List<LauncherReleaseInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (ReadBoolean(item, "draft") || ReadBoolean(item, "prerelease"))
            {
                continue;
            }

            var tag = ReadString(item, "tag_name");
            if (!TryParseReleaseTag(tag, out var version))
            {
                continue;
            }

            var asset = item.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array
                    ? assets.EnumerateArray().FirstOrDefault(candidate =>
                        string.Equals(ReadString(candidate, "name"), ReleaseAssetName, StringComparison.Ordinal)
                        && string.Equals(ReadString(candidate, "state"), "uploaded", StringComparison.OrdinalIgnoreCase))
                    : default;
            var assetUrl = asset.ValueKind == JsonValueKind.Object
                ? ReadString(asset, "browser_download_url")
                : null;
            var assetSize = asset.ValueKind == JsonValueKind.Object
                && asset.TryGetProperty("size", out var sizeElement)
                && sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : 0;
            var digest = asset.ValueKind == JsonValueKind.Object ? ReadString(asset, "digest") : null;
            var sha256 = TryParseSha256Digest(digest);
            if (!IsTrustedAssetUrl(assetUrl, tag))
            {
                assetUrl = null;
                sha256 = null;
            }

            releases.Add(new LauncherReleaseInfo(
                tag!,
                version,
                ReadString(item, "name") ?? tag!,
                ReadString(item, "body") ?? string.Empty,
                ReadDateTimeOffset(item, "published_at"),
                assetUrl,
                assetSize,
                sha256));
        }

        return releases
            .GroupBy(release => release.Version)
            .Select(group => group.OrderByDescending(release => release.PublishedAt).First())
            .OrderByDescending(release => release.Version)
            .ToArray();
    }

    public async Task<LauncherReleaseInfo?> CheckForUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;
        return (await ReadReleasesAsync(cancellationToken))
            .FirstOrDefault(release => release.CanInstall && release.Version > current);
    }

    public Task<string> DownloadReleaseAsync(
        LauncherReleaseInfo release,
        IProgress<NodeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadReleaseCoreAsync(release, progress, validateFileVersion: true, cancellationToken);

    internal async Task<string> DownloadReleaseCoreAsync(
        LauncherReleaseInfo release,
        IProgress<NodeDownloadProgress>? progress,
        bool validateFileVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.CanInstall
            || release.AssetSize is < MinimumAssetBytes or > MaximumAssetBytes
            || !IsSha256(release.Sha256)
            || !IsTrustedAssetUrl(release.AssetUrl, release.TagName))
        {
            throw new InvalidDataException($"Release {release.TagName} 没有可安全安装的 {ReleaseAssetName}。");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "DSH Launcher",
            "updates",
            release.TagName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, ReleaseAssetName);
        var temporary = $"{destination}.part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var responseLength = response.Content.Headers.ContentLength;
            if (responseLength is > MaximumAssetBytes
                || responseLength is { } declared && declared != release.AssetSize)
            {
                throw new InvalidDataException("GitHub Release 附件大小与 metadata 不一致。");
            }

            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
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
                    if (received > MaximumAssetBytes)
                    {
                        throw new InvalidDataException("下载的 Launcher 文件超过安全大小上限。");
                    }

                    progress?.Report(new NodeDownloadProgress(
                        received,
                        release.AssetSize,
                        received * 100.0 / release.AssetSize));
                }

                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            var actualSize = new FileInfo(temporary).Length;
            if (actualSize != release.AssetSize)
            {
                throw new IOException(
                    $"Launcher 下载不完整：预期 {release.AssetSize} 字节，实际 {actualSize} 字节。");
            }

            var actualHash = ComputeSha256(temporary);
            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Launcher 下载文件未通过 GitHub SHA-256 校验。");
            }

            if (validateFileVersion && !DownloadedVersionMatches(temporary, release.Version))
            {
                throw new InvalidDataException(
                    $"下载文件版本与 Release {release.TagName} 不一致，已拒绝安装。");
            }

            File.Move(temporary, destination, overwrite: false);
            progress?.Report(new NodeDownloadProgress(actualSize, actualSize, 100));
            return destination;
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public static bool TryLaunchApplyHelper(
        string downloadedExecutable,
        string targetExecutable,
        int currentProcessId,
        string expectedSha256,
        out string? error)
    {
        error = null;
        string? helper = null;
        try
        {
            var source = Path.GetFullPath(downloadedExecutable);
            var target = Path.GetFullPath(targetExecutable);
            if (!File.Exists(source) || !File.Exists(target))
            {
                throw new FileNotFoundException("更新源文件或当前 Launcher 不存在。");
            }

            ValidateLauncherTarget(target);
            if (!IsSha256(expectedSha256)
                || !ComputeSha256(source).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新文件 SHA-256 不匹配。");
            }

            helper = Path.Combine(
                Path.GetDirectoryName(source)!,
                $"DSH.Launcher.UpdateHelper-{Guid.NewGuid():N}.exe");
            File.Copy(source, helper, overwrite: false);
            if (!ComputeSha256(helper).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(helper);
                throw new InvalidDataException("更新辅助程序 SHA-256 不匹配。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helper)!
            };
            startInfo.ArgumentList.Add(ApplyModeArgument);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("--wait-pid");
            startInfo.ArgumentList.Add(currentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--sha256");
            startInfo.ArgumentList.Add(expectedSha256.ToUpperInvariant());
            using var helperProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Launcher 更新辅助进程。");
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            if (helper is not null)
            {
                TryDeleteFile(helper);
            }

            error = ex.Message;
            return false;
        }
    }

    public static bool TryValidateUpdateTarget(string targetExecutable, out string? error)
    {
        error = null;
        try
        {
            var target = Path.GetFullPath(targetExecutable);
            ValidateLauncherTarget(target);
            var directory = Path.GetDirectoryName(target)!;
            var probe = Path.Combine(directory, $".dsh-launcher-update-probe-{Guid.NewGuid():N}.tmp");
            using (new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryParseApplyArguments(
        IReadOnlyList<string> args,
        out LauncherUpdateApplyRequest? request)
    {
        request = null;
        if (args.Count != 7 || !string.Equals(args[0], ApplyModeArgument, StringComparison.Ordinal))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            if (!values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("--target", out var target)
            || !values.TryGetValue("--wait-pid", out var pidText)
            || !int.TryParse(pidText, out var pid)
            || pid <= 0
            || !values.TryGetValue("--sha256", out var sha256)
            || !IsSha256(sha256))
        {
            return false;
        }

        try
        {
            request = new LauncherUpdateApplyRequest(
                Path.GetFullPath(target),
                pid,
                sha256.ToUpperInvariant());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static int ApplyUpdateAndRestart(
        LauncherUpdateApplyRequest request,
        string sourceExecutable)
    {
        try
        {
            var source = Path.GetFullPath(sourceExecutable);
            var target = Path.GetFullPath(request.TargetPath);
            ValidateLauncherTarget(target);
            RejectReparsePoint(source, "更新文件");
            if (!ComputeSha256(source).Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新辅助进程自身的 SHA-256 不匹配。");
            }

            try
            {
                using var oldProcess = Process.GetProcessById(request.WaitProcessId);
                var processPath = oldProcess.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(processPath)
                    || !Path.GetFullPath(processPath).Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("等待进程不是目标 DSH Launcher。");
                }

                if (!oldProcess.WaitForExit((int)OldProcessExitTimeout.TotalMilliseconds))
                {
                    throw new TimeoutException("当前 DSH Launcher 超过 2 分钟仍未退出，更新已取消。");
                }
            }
            catch (ArgumentException)
            {
                // 正常关闭很快时，旧进程可能在 helper 读取 PID 前已经退出。
            }

            ReplaceExecutableAtomically(source, target, request.ExpectedSha256);
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target)!
            });
            return 0;
        }
        catch (Exception ex)
        {
            WriteApplyError(ex);
            TryRestartLauncher(request.TargetPath);
            return 1;
        }
    }

    internal static void ReplaceExecutableAtomically(
        string source,
        string target,
        string expectedSha256)
    {
        ValidateLauncherTarget(target);
        var targetDirectory = Path.GetDirectoryName(target)!;
        var staged = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.update-{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.backup-{Guid.NewGuid():N}.bak");
        try
        {
            File.Copy(source, staged, overwrite: false);
            if (!ComputeSha256(staged).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("写入目标目录后的更新文件 SHA-256 不匹配。");
            }

            try
            {
                File.Replace(staged, target, backup, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceWithMove(staged, target, backup);
            }
            catch (IOException)
            {
                ReplaceWithMove(staged, target, backup);
            }

            if (!ComputeSha256(target).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(backup))
                {
                    File.Copy(backup, target, overwrite: true);
                }

                throw new InvalidDataException("替换后的 Launcher SHA-256 不匹配，已恢复旧文件。");
            }

            TryDeleteFile(backup);
        }
        finally
        {
            TryDeleteFile(staged);
        }
    }

    private static void ReplaceWithMove(string staged, string target, string backup)
    {
        File.Move(target, backup, overwrite: false);
        try
        {
            File.Move(staged, target, overwrite: false);
        }
        catch
        {
            if (!File.Exists(target) && File.Exists(backup))
            {
                File.Move(backup, target, overwrite: false);
            }

            throw;
        }
    }

    internal static bool TryParseReleaseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var value = tag?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.TrimStart('v', 'V');
        var parts = value.Split('.');
        if (parts.Length != 3
            || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit))
            || value.Contains('-', StringComparison.Ordinal)
            || !Version.TryParse(value, out var parsed)
            || parsed.Major < 0
            || parsed.Minor < 0
            || parsed.Build < 0)
        {
            return false;
        }

        version = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static string? TryParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        var value = digest?.Trim();
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = value[prefix.Length..];
        return IsSha256(hash) ? hash.ToUpperInvariant() : null;
    }

    private static bool IsTrustedAssetUrl(string? value, string? tag)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedPrefix = $"/{RepositoryOwner}/{RepositoryName}/releases/download/{tag}/";
        return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith($"/{ReleaseAssetName}", StringComparison.Ordinal);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => Uri.IsHexDigit(character));

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool DownloadedVersionMatches(string path, Version expected)
    {
        try
        {
            var value = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(value, out var actual)
                && NormalizeVersion(actual) == expected;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void ValidateLauncherTarget(string target)
    {
        var fullPath = Path.GetFullPath(target);
        if (!File.Exists(fullPath)
            || !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新目标不是现有的 Windows Launcher EXE。");
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("更新目标没有父目录。");
        if (string.Equals(
                directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("不能在磁盘根目录执行 Launcher 更新。");
        }

        RejectReparsePoint(directory, "Launcher 目录");
        RejectReparsePoint(fullPath, "Launcher EXE");
        var version = FileVersionInfo.GetVersionInfo(fullPath);
        if (!string.Equals(version.ProductName, "DSH Launcher", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(version.FileDescription, "DSH Launcher", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新目标不是 DSH Launcher。");
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{label}不能是符号链接或重解析点。");
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
        }
    }

    private static void WriteApplyError(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "DSH Launcher");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "update-error.log"),
                $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void TryRestartLauncher(string target)
    {
        try
        {
            if (File.Exists(target))
            {
                Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(target)!
                });
            }
        }
        catch
        {
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static DateTimeOffset ReadDateTimeOffset(JsonElement element, string property) =>
        DateTimeOffset.TryParse(ReadString(element, property), out var value)
            ? value
            : DateTimeOffset.MinValue;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
