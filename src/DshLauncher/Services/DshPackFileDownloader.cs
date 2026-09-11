using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace DshLauncher.Services;

/// <summary>单个 <c>files[]</c> 条目的下载结果。</summary>
public sealed record PackFileDownloadResult(bool Succeeded, long BytesWritten, string? UsedUrl, string? Error)
{
    public static PackFileDownloadResult Failed(string error, string? usedUrl = null) =>
        new(false, 0, usedUrl, error);
}

/// <summary>
/// <c>files[]</c> 重内容下载器（#24 第 4 步，用户决策 Q2）：按 urls[] 依次尝试镜像、
/// **强制 sha256 + size 校验**、先写临时文件再原子落位、任何失败都不留半成品。
/// 取流动作可注入（测试用假流覆盖镜像回退与校验失败，不联网）。
/// </summary>
public sealed class PackFileDownloader
{
    /// <summary>单个下载文件的大小上限（files[] 只承载重内容，但仍要有硬上限）。</summary>
    public const long MaximumDownloadBytes = 64L * 1024 * 1024;

    public const string UserAgent = "DSH-Launcher/1.0 (+pack-files)";

    private const string TemporarySuffix = ".download";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly Func<Uri, CancellationToken, Task<Stream>> _fetch;

    private readonly TimeSpan _timeout;

    public PackFileDownloader(
        Func<Uri, CancellationToken, Task<Stream>>? fetch = null,
        TimeSpan? timeout = null)
    {
        _fetch = fetch ?? FetchWithHttpClientAsync;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>按镜像顺序下载并校验一个条目；成功后文件已在 destinationPath。</summary>
    public async Task<PackFileDownloadResult> DownloadAsync(
        PackFileEntry entry,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!PackFormat.IsSafeRelativePath(entry.Path))
        {
            return PackFileDownloadResult.Failed($"条目路径不安全：{entry.Path}");
        }

        if (!PackFormat.IsValidSha256(entry.Sha256) || entry.Size <= 0)
        {
            return PackFileDownloadResult.Failed($"条目校验信息不完整：{entry.Path}");
        }

        if (entry.Size > MaximumDownloadBytes)
        {
            return PackFileDownloadResult.Failed(
                $"文件超过单文件上限（{MaximumDownloadBytes / 1024 / 1024} MB）：{entry.Path}");
        }

        if (entry.Urls.Count == 0)
        {
            return PackFileDownloadResult.Failed($"没有可用的下载地址：{entry.Path}");
        }

        var failures = new List<string>();
        foreach (var url in entry.Urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                failures.Add($"{url}：不是 http(s) 地址");
                continue;
            }

            var temporaryPath = destinationPath + TemporarySuffix;
            try
            {
                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var written = await FetchToFileAsync(uri, temporaryPath, entry.Size, cancellationToken);
                if (!TryVerify(temporaryPath, entry.Sha256, entry.Size, out var verifyError))
                {
                    failures.Add($"{url}：{verifyError}");
                    TryDelete(temporaryPath);
                    continue;
                }

                // 校验通过才落位（先删旧文件，避免已存在时移动失败）。
                TryDelete(destinationPath);
                File.Move(temporaryPath, destinationPath);
                return new PackFileDownloadResult(true, written, url, null);
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporaryPath);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                failures.Add($"{url}：{ex.Message}");
                TryDelete(temporaryPath);
            }
        }

        return PackFileDownloadResult.Failed(failures.Count == 0
            ? $"没有可用的下载地址：{entry.Path}"
            : string.Join("；", failures));
    }

    /// <summary>校验本地文件的 sha256 与大小（下载与"包内载荷"两条路径共用）。</summary>
    public static bool TryVerify(string filePath, string expectedSha256, long expectedSize, out string? error)
    {
        error = null;
        if (!File.Exists(filePath))
        {
            error = "文件不存在。";
            return false;
        }

        var info = new FileInfo(filePath);
        if (info.Length != expectedSize)
        {
            error = $"大小不符（期望 {expectedSize} 字节，实际 {info.Length} 字节）。";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            if (!string.Equals(hash, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                error = $"sha256 不符（期望 {expectedSha256[..Math.Min(12, expectedSha256.Length)]}…，实际 {hash[..12]}…）。";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取文件失败：{ex.Message}";
            return false;
        }
    }

    private async Task<long> FetchToFileAsync(Uri uri, string temporaryPath, long expectedSize, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var token = timeoutSource.Token;

        await using var stream = await _fetch(uri, token);
        await using var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > expectedSize)
            {
                // 早停：比声明还大就没必要继续下（省流量，也避免被超大流拖死）。
                throw new InvalidDataException($"下载内容超过声明的 {expectedSize} 字节。");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), token);
        }

        await file.FlushAsync(token);
        return total;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static async Task<Stream> FetchWithHttpClientAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清临时文件失败不影响判定：上层会按"未落位"处理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
