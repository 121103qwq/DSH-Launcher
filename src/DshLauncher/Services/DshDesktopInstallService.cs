using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

public enum DshDesktopDownloadSource
{
    ProjectMirror,
    GitHub
}

public sealed record DshDesktopReleaseInfo(
    string TagName,
    Version Version,
    string AssetName,
    long AssetSize,
    string Sha256,
    string GitHubDownloadUrl)
{
    public string VersionText => TagName.StartsWith('v') ? TagName : $"v{Version}";
}

/// <summary>
/// Downloads the current community-maintained DSH Desktop Windows installer.
/// The installer remains interactive; Launcher only verifies, opens it, and
/// rescans the installed runtime after it exits.
/// </summary>
public sealed class DshDesktopInstallService : IDisposable
{
    public const string RepositoryOwner = "anywhere-labs";
    public const string RepositoryName = "dsh-desktop";
    public const string ProjectWindowsDownloadUrl = "https://www.dshdesktop.cn/api/downloads/windows";

    private const long MinimumInstallerBytes = 10L * 1024 * 1024;
    private const long MaximumInstallerBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(30);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public DshDesktopInstallService(HttpClient? client = null)
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

    public async Task<DshDesktopReleaseInfo> ReadLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
        using var response = await _client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
        {
            throw new InvalidDataException("DSH Desktop 最新 Release 不是稳定版本。 ");
        }

        var tag = ReadString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)
            || !Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var version))
        {
            throw new InvalidDataException("DSH Desktop Release 版本号无效。 ");
        }

        if (!root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("DSH Desktop Release 没有 Windows 安装附件。 ");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            if (string.IsNullOrWhiteSpace(name)
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                || !name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ReadString(asset, "state"), "uploaded", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = asset.TryGetProperty("size", out var sizeElement)
                && sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : 0;
            var sha256 = ParseSha256(ReadString(asset, "digest"));
            var downloadUrl = ReadString(asset, "browser_download_url");
            if (size is < MinimumInstallerBytes or > MaximumInstallerBytes
                || sha256 is null
                || !IsTrustedGitHubAsset(downloadUrl, tag, name))
            {
                continue;
            }

            return new DshDesktopReleaseInfo(
                tag,
                version,
                name,
                size,
                sha256,
                downloadUrl!);
        }

        throw new InvalidDataException("DSH Desktop 最新 Release 没有可验证的 Windows x64 安装包。 ");
    }

    public async Task<string> DownloadInstallerAsync(
        DshDesktopReleaseInfo release,
        DshDesktopDownloadSource source,
        IProgress<NodeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.AssetSize is < MinimumInstallerBytes or > MaximumInstallerBytes
            || !IsSha256(release.Sha256)
            || !IsTrustedGitHubAsset(release.GitHubDownloadUrl, release.TagName, release.AssetName))
        {
            throw new InvalidDataException("DSH Desktop Release metadata 不完整，不能下载安装。 ");
        }

        var downloadUrl = source == DshDesktopDownloadSource.ProjectMirror
            ? ProjectWindowsDownloadUrl
            : release.GitHubDownloadUrl;
        var root = Path.Combine(
            Path.GetTempPath(),
            "DSH Launcher",
            "dsh-desktop",
            release.TagName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, release.AssetName);
        var temporary = $"{destination}.part";
        try
        {
            using var response = await _client.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
            {
                throw new InvalidDataException("DSH Desktop 安装包超过大小上限。 ");
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
                    if (received > MaximumInstallerBytes)
                    {
                        throw new InvalidDataException("DSH Desktop 安装包超过大小上限。 ");
                    }

                    progress?.Report(new NodeDownloadProgress(
                        received,
                        release.AssetSize,
                        Math.Clamp(received * 100d / release.AssetSize, 0, 100)));
                }
            }

            var length = new FileInfo(temporary).Length;
            if (length != release.AssetSize)
            {
                throw new InvalidDataException(
                    $"DSH Desktop 安装包大小不匹配：预期 {release.AssetSize}，实际 {length}。 ");
            }

            var actualSha256 = await ComputeSha256Async(temporary, cancellationToken);
            if (!actualSha256.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("DSH Desktop 安装包 SHA-256 与 GitHub Release 不一致。 ");
            }

            if (!IsWindowsExecutable(temporary))
            {
                throw new InvalidDataException("下载内容不是有效的 Windows PE 安装程序。 ");
            }

            File.Move(temporary, destination, overwrite: false);
            progress?.Report(new NodeDownloadProgress(length, length, 100));
            return destination;
        }
        catch
        {
            TryDeleteFile(temporary);
            TryDeleteFile(destination);
            TryDeleteEmptyDirectory(root);
            throw;
        }
    }

    public static void CleanupDownloadedInstaller(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DSH Launcher", "dsh-desktop"));
            if (!fullPath.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TryDeleteFile(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is not null)
            {
                TryDeleteEmptyDirectory(directory);
            }
        }
        catch
        {
            // A downloaded installer may still be held briefly by Windows.
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static bool IsTrustedGitHubAsset(string? url, string tag, string assetName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.Equals(parsed.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedPath = $"/{RepositoryOwner}/{RepositoryName}/releases/download/{tag}/{assetName}";
        return string.Equals(parsed.AbsolutePath, expectedPath, StringComparison.Ordinal);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 256 || reader.ReadUInt16() != 0x5A4D)
            {
                return false;
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset > stream.Length - 4)
            {
                return false;
            }

            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ParseSha256(string? digest)
    {
        const string prefix = "sha256:";
        var value = digest?.Trim();
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sha256 = value[prefix.Length..];
        return IsSha256(sha256) ? sha256.ToUpperInvariant() : null;
    }

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
