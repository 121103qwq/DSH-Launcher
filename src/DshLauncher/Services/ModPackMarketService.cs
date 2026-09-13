using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Reads the public DSH-PackForge index and downloads a selected archive.
/// Market metadata is untrusted input: package contents are never executed or
/// interpreted here. VersionPackageService performs preview/import validation.
/// </summary>
public sealed class ModPackMarketService : IDisposable
{
    public const string OfficialCatalogUrl =
        "https://dsh-packforge.github.io/dsh-pack-market/index.json";

    private const int MaximumCatalogBytes = 8 * 1024 * 1024;
    private const long MaximumDownloadBytes = 256L * 1024 * 1024;
    private const string DownloadRootName = "modpack-market";
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] SupportedExtensions =
    {
        ".dshpack",
        ".dspack",
        ".tgz",
        ".tar.gz"
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public ModPackMarketService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = DownloadTimeout };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DSH-Launcher", "1.0"));
        }
    }

    public async Task<IReadOnlyList<ModPackMarketEntry>> ReadCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(CatalogTimeout);
        using var response = await _client.GetAsync(
            OfficialCatalogUrl,
            HttpCompletionOption.ResponseHeadersRead,
            operationCancellation.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri
            && !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("市场索引被重定向到不安全的地址。 ");
        }

        var bytes = await ReadResponseBytesAsync(
            response.Content,
            MaximumCatalogBytes,
            operationCancellation.Token);
        using var document = JsonDocument.Parse(bytes);
        return ParseCatalog(document.RootElement);
    }

    public async Task<string> DownloadPackageAsync(
        ModPackMarketEntry entry,
        IProgress<NodeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var metadataError = GetUnavailableReason(
            entry.DownloadUrl,
            entry.Sha256,
            entry.Size,
            entry.PackageType);
        if (metadataError is not null || !entry.IsInstallable)
        {
            var reason = metadataError
                ?? (!string.IsNullOrWhiteSpace(entry.UnavailableReason)
                    ? entry.UnavailableReason
                    : "该整合包缺少可验证的下载信息。");
            throw new InvalidDataException(
                reason);
        }

        if (!TryValidateDownloadUrl(entry.DownloadUrl, out var uri, out _))
        {
            throw new InvalidDataException("整合包下载地址无效。 ");
        }

        var extension = GetArchiveExtension(uri!);
        var root = GetDownloadRoot();
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"package{extension}");
        var temporary = $"{destination}.part";
        var completed = false;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(DownloadTimeout);
        var operationToken = operationCancellation.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operationToken);
            response.EnsureSuccessStatusCode();

            if (response.RequestMessage?.RequestUri is { } finalUri
                && !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("整合包下载被重定向到不安全的地址。 ");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaximumDownloadBytes)
            {
                throw new InvalidDataException("整合包下载超过安全大小上限。 ");
            }

            if (declaredLength is { } declared && declared != entry.Size)
            {
                throw new InvalidDataException(
                    $"整合包大小与市场索引不一致：预期 {entry.Size} 字节，响应声明 {declared} 字节。 ");
            }

            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await using var input = await response.Content.ReadAsStreamAsync(operationToken);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), operationToken)) > 0)
                {
                    received += read;
                    if (received > entry.Size)
                    {
                        throw new InvalidDataException(
                            $"整合包下载超过市场索引声明的大小：预期 {entry.Size} 字节。 ");
                    }

                    if (received > MaximumDownloadBytes)
                    {
                        throw new InvalidDataException("整合包下载超过安全大小上限。 ");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), operationToken);
                    progress?.Report(new NodeDownloadProgress(
                        received,
                        entry.Size,
                        received * 100.0 / entry.Size));
                }

                await output.FlushAsync(operationToken);
                output.Flush(flushToDisk: true);
            }

            var actualSize = new FileInfo(temporary).Length;
            if (actualSize != entry.Size)
            {
                throw new InvalidDataException(
                    $"整合包下载不完整：预期 {entry.Size} 字节，实际 {actualSize} 字节。 ");
            }

            operationToken.ThrowIfCancellationRequested();
            var actualHash = await ComputeSha256Async(temporary, operationToken);
            if (!actualHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("整合包下载未通过 SHA-256 校验。 ");
            }

            operationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            progress?.Report(new NodeDownloadProgress(actualSize, actualSize, 100));
            operationToken.ThrowIfCancellationRequested();
            completed = true;
            return destination;
        }
        finally
        {
            // The caller owns a successfully returned archive.
            TryDeleteFile(temporary);
            if (!completed)
            {
                TryDeleteFile(destination);
                TryDeleteDirectory(directory);
            }
        }
    }

    /// <summary>
    /// Removes a file returned by DownloadPackageAsync. Paths outside this
    /// service's private temporary root are ignored.
    /// </summary>
    public static void CleanupDownload(string? path)
    {
        if (!TryGetPathInsideDownloadRoot(path, out var fullPath, out var root))
        {
            return;
        }

        TryDeleteFile(fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null
            && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
            && IsPathInside(directory, root))
        {
            TryDeleteDirectory(directory);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static IReadOnlyList<ModPackMarketEntry> ParseCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("整合包市场索引根节点不是对象。 ");
        }

        var schemaVersion = ReadInt(root, "schemaVersion") ?? 1;
        if (schemaVersion is < 1 or > 2)
        {
            throw new InvalidDataException($"不支持的整合包市场索引版本：{schemaVersion}。 ");
        }

        if (!root.TryGetProperty("modpacks", out var packs)
            || packs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("整合包市场索引缺少 modpacks 数组。 ");
        }

        var entries = new List<ModPackMarketEntry>();
        foreach (var item in packs.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                entries.Add(ParseEntry(item));
            }
        }

        return entries;
    }

    private static ModPackMarketEntry ParseEntry(JsonElement item)
    {
        var packageName = ReadString(item, "name") ?? string.Empty;
        var displayName = ReadLocalized(item, "displayName")
            ?? ReadLocalized(item, "name")
            ?? packageName;
        var description = ReadLocalized(item, "description") ?? string.Empty;
        var author = ReadLocalized(item, "author") ?? string.Empty;
        var downloadUrl = ReadString(item, "downloadUrl")
            ?? ReadString(item, "url")
            ?? string.Empty;
        var packageType = ReadString(item, "type") ?? ReadString(item, "packageType");
        var sha256 = NormalizeSha256(ReadString(item, "sha256"));
        var size = ReadInt64(item, "size") ?? 0;
        var reason = GetUnavailableReason(downloadUrl, sha256, size, packageType);

        return new ModPackMarketEntry
        {
            Name = displayName,
            Description = description,
            Version = ReadString(item, "version") ?? string.Empty,
            Author = author,
            DownloadUrl = downloadUrl,
            Sha256 = sha256,
            Size = size,
            IsInstallable = reason is null,
            UnavailableReason = reason ?? string.Empty,
            PackageType = packageType,
        };
    }

    private static string? GetUnavailableReason(
        string downloadUrl,
        string? sha256,
        long size,
        string? packageType)
    {
        if (string.Equals(packageType, "dshhome", StringComparison.OrdinalIgnoreCase))
        {
            return "DSH Home 整机包暂不支持从市场导入。 ";
        }

        if (!TryValidateDownloadUrl(downloadUrl, out _, out var urlError))
        {
            return urlError;
        }

        if (!IsSha256(sha256))
        {
            return "市场条目缺少有效的 SHA-256，暂不能安全安装。 ";
        }

        if (size <= 0)
        {
            return "市场条目缺少有效的文件大小，暂不能安全安装。 ";
        }

        if (size > MaximumDownloadBytes)
        {
            return "整合包超过 Launcher 允许的下载大小上限。 ";
        }

        return null;
    }

    private static bool TryValidateDownloadUrl(
        string? value,
        out Uri? uri,
        out string? error)
    {
        uri = null;
        error = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "下载地址必须使用 HTTPS。 ";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "下载地址不能包含凭据或片段。 ";
            return false;
        }

        if (parsed.AbsolutePath.Contains("your-org", StringComparison.OrdinalIgnoreCase)
            || parsed.AbsoluteUri.Contains("your-org", StringComparison.OrdinalIgnoreCase))
        {
            error = "下载地址仍是示例占位链接，暂不能安装。 ";
            return false;
        }

        if (!SupportedExtensions.Any(extension =>
                parsed.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            error = "下载地址不是支持的 .dshpack、.dspack、.tgz 或 .tar.gz 整合包。 ";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string GetArchiveExtension(Uri uri)
    {
        var path = uri.AbsolutePath;
        foreach (var extension in SupportedExtensions.OrderByDescending(value => value.Length))
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }

        return ".dspack";
    }

    private static async Task<byte[]> ReadResponseBytesAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is > 0 && declaredLength > maximumBytes)
        {
            throw new InvalidDataException("市场索引响应超过安全大小上限。 ");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("市场索引响应超过安全大小上限。 ");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string? ReadLocalized(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var locale in new[] { "zh-CN", "en-US" })
        {
            if (value.TryGetProperty(locale, out var localized)
                && localized.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(localized.GetString()))
            {
                return localized.GetString();
            }
        }

        foreach (var localized in value.EnumerateObject())
        {
            if (localized.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(localized.Value.GetString()))
            {
                return localized.Value.GetString();
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static long? ReadInt64(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result))
        {
            return result;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? result
                : null;
    }

    private static string? NormalizeSha256(string? value)
    {
        var normalized = value?.Trim();
        return IsSha256(normalized) ? normalized!.ToLowerInvariant() : null;
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetDownloadRoot() =>
        Path.Combine(Path.GetTempPath(), "DSH Launcher", DownloadRootName);

    private static bool TryGetPathInsideDownloadRoot(
        string? path,
        out string fullPath,
        out string root)
    {
        fullPath = string.Empty;
        root = GetDownloadRoot();
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
            root = Path.GetFullPath(root);
            return IsPathInside(fullPath, root)
                && IsGeneratedDownloadPath(fullPath, root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsPathInside(string path, string root)
    {
        var separator = root.EndsWith(Path.DirectorySeparatorChar)
            || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? string.Empty
            : Path.DirectorySeparatorChar.ToString();
        return path.StartsWith(root + separator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedDownloadPath(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && Guid.TryParseExact(parts[0], "N", out _)
            && parts[1].StartsWith("package", StringComparison.OrdinalIgnoreCase);
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
            // Cleanup must not hide the original download error.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }
}
