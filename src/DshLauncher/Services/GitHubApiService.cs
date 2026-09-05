using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Shared conditional GET layer for GitHub API, tree and content requests.
/// It keeps response validators in memory only; callers decide how to supply
/// an optional token and this service never persists it.
/// </summary>
public sealed class GitHubApiService
{
    private const int MaxCachedResponseBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _httpClient;
    private readonly object _tokenGate = new();
    private string? _accessToken;
    private long _accessTokenVersion;
    private readonly ConcurrentDictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);
    private GitHubRateLimitInfo? _lastRateLimit;

    public GitHubApiService(HttpClient? httpClient = null, string? accessToken = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
        _accessToken = NormalizeAccessToken(accessToken);
    }

    public GitHubRateLimitInfo? LastRateLimit => Volatile.Read(ref _lastRateLimit);

    /// <summary>
    /// Replaces the in-memory token for subsequent requests. The token is not
    /// logged or persisted; changing it also isolates the validator cache and
    /// quota status from the previous token identity.
    /// </summary>
    public void UpdateAccessToken(string? token)
    {
        lock (_tokenGate)
        {
            _accessToken = NormalizeAccessToken(token);
            _accessTokenVersion++;
            _cache.Clear();
            Volatile.Write(ref _lastRateLimit, null);
        }
    }

    public GitHubApiStatus Status
    {
        get
        {
            var rateLimit = LastRateLimit;
            return new GitHubApiStatus(
                rateLimit,
                rateLimit?.IsLimited == true,
                BuildStatusMessage(rateLimit));
        }
    }

    public bool IsRateLimited => Status.IsRateLimited;

    public Task<GitHubApiResponse> GetAsync(
        Uri uri,
        CancellationToken cancellationToken = default,
        int maximumResponseBytes = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        return SendGetAsync(uri, useConditionalHeaders: true, cancellationToken, maximumResponseBytes);
    }

    public Task<GitHubApiResponse> GetAsync(
        string uri,
        CancellationToken cancellationToken = default) =>
        GetAsync(new Uri(uri, UriKind.Absolute), cancellationToken);

    public async Task<string> GetStringAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.ReadAsString();
    }

    public async Task<byte[]> GetBytesAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        var response = await GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.ReadAsByteArray();
    }

    public static bool IsGitHubUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("codeload.github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubApiResponse> SendGetAsync(
        Uri uri,
        bool useConditionalHeaders,
        CancellationToken cancellationToken,
        int maximumResponseBytes)
    {
        var tokenSnapshot = GetTokenSnapshot();
        var cacheKey = BuildCacheKey(uri, tokenSnapshot.Version);
        _cache.TryGetValue(cacheKey, out var cached);
        if (cached is not null && cached.Body.Length > maximumResponseBytes)
        {
            // A stricter caller must not reuse an oversized cached body. Fetch
            // without validators in case the remote image has since shrunk.
            cached = null;
        }
        var accessToken = tokenSnapshot.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (accessToken is not null && IsGitHubUri(uri))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (useConditionalHeaders && cached is not null)
        {
            if (!string.IsNullOrWhiteSpace(cached.ETag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            }

            if (cached.LastModified is { } cachedLastModified)
            {
                request.Headers.IfModifiedSince = cachedLastModified;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        var responseRateLimit = ParseRateLimit(response);
        UpdateRateLimit(responseRateLimit, tokenSnapshot.Version);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (cached is not null)
            {
                return new GitHubApiResponse(
                    response.StatusCode,
                    cached.Body,
                    cached.ContentLength,
                    isFromCache: true,
                    isNotModified: true,
                    GetETag(response) ?? cached.ETag,
                    GetLastModified(response) ?? cached.LastModified,
                    responseRateLimit ?? cached.RateLimit);
            }

            // A 304 without a local validator body is unusable. Retry once
            // without validators so the existing caller still gets content.
            if (useConditionalHeaders)
            {
                return await SendGetAsync(uri, useConditionalHeaders: false, timeout.Token, maximumResponseBytes);
            }

            var emptyBody = await ReadBodyAsync(response.Content, maximumResponseBytes, timeout.Token);
            return new GitHubApiResponse(
                response.StatusCode,
                emptyBody,
                response.Content.Headers.ContentLength,
                isFromCache: false,
                isNotModified: true,
                GetETag(response),
                GetLastModified(response),
                responseRateLimit);
        }

        if (IsRateLimitedResponse(response.StatusCode, responseRateLimit))
        {
            throw CreateRateLimitException(uri, response.StatusCode, responseRateLimit);
        }

        var body = await ReadBodyAsync(response.Content, maximumResponseBytes, timeout.Token);
        var etag = GetETag(response);
        var lastModified = GetLastModified(response);
        var contentLength = response.Content.Headers.ContentLength;
        var result = new GitHubApiResponse(
            response.StatusCode,
            body,
            contentLength,
            isFromCache: false,
            isNotModified: false,
            etag,
            lastModified,
            responseRateLimit);

        if (result.IsSuccessStatusCode
            && body.Length <= MaxCachedResponseBytes
            && (!string.IsNullOrWhiteSpace(etag) || lastModified is not null))
        {
            _cache[cacheKey] = new CachedResponse(
                body,
                contentLength,
                etag,
                lastModified,
                responseRateLimit);
        }

        return result;
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("GitHub 响应超过允许的大小。");
        }

        using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            // Read at most one extra byte to detect oversized chunked bodies.
            var count = (int)Math.Min(buffer.Length, maximumBytes - body.Length + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
            if (read == 0)
            {
                return body.ToArray();
            }

            if (body.Length + read > maximumBytes)
            {
                throw new InvalidDataException("GitHub 响应超过允许的大小。");
            }

            body.Write(buffer, 0, read);
        }
    }

    private void UpdateRateLimit(GitHubRateLimitInfo? rateLimit, long tokenVersion)
    {
        if (rateLimit is null)
        {
            return;
        }

        lock (_tokenGate)
        {
            if (tokenVersion == _accessTokenVersion)
            {
                Volatile.Write(ref _lastRateLimit, rateLimit);
            }
        }
    }

    private static GitHubRateLimitInfo? ParseRateLimit(HttpResponseMessage response)
    {
        var limit = ReadIntHeader(response, "X-RateLimit-Limit");
        var remaining = ReadIntHeader(response, "X-RateLimit-Remaining");
        DateTimeOffset? resetAt = null;
        var reset = ReadStringHeader(response, "X-RateLimit-Reset");
        if (long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetUnix))
        {
            try
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore malformed server metadata and preserve the response.
            }
        }

        DateTimeOffset? retryAfterAt = null;
        var retryAfter = ReadStringHeader(response, "Retry-After");
        if (long.TryParse(retryAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retrySeconds))
        {
            retryAfterAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, retrySeconds));
        }
        else if (DateTimeOffset.TryParse(
            retryAfter,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var retryDate))
        {
            retryAfterAt = retryDate;
        }

        if (limit is null && remaining is null && resetAt is null && retryAfterAt is null)
        {
            return null;
        }

        return new GitHubRateLimitInfo(
            limit,
            remaining,
            resetAt,
            retryAfterAt,
            DateTimeOffset.UtcNow);
    }

    private static bool IsRateLimitedResponse(
        HttpStatusCode statusCode,
        GitHubRateLimitInfo? rateLimit) =>
        statusCode == HttpStatusCode.TooManyRequests
        || statusCode == HttpStatusCode.Forbidden && rateLimit?.IsLimited == true;

    private static GitHubRateLimitException CreateRateLimitException(
        Uri uri,
        HttpStatusCode statusCode,
        GitHubRateLimitInfo? rateLimit)
    {
        var remaining = rateLimit?.Remaining is { } value ? value.ToString(CultureInfo.InvariantCulture) : "未知";
        var recovery = rateLimit?.RecoveryAt is { } recoveryAt
            ? $"预计 {recoveryAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 恢复"
            : "恢复时间未知";
        var message = $"GitHub 请求受到限流（HTTP {(int)statusCode}）：剩余配额 {remaining}，{recovery}。";
        return new GitHubRateLimitException(message, rateLimit, statusCode, uri);
    }

    private static string? BuildStatusMessage(GitHubRateLimitInfo? rateLimit)
    {
        if (rateLimit is null)
        {
            return null;
        }

        var remaining = rateLimit.Remaining is { } value
            ? value.ToString(CultureInfo.InvariantCulture)
            : "未知";
        return rateLimit.IsLimited
            ? $"GitHub 已限流：剩余配额 {remaining}，{FormatRecovery(rateLimit.RecoveryAt)}。"
            : $"GitHub 剩余配额：{remaining}。";
    }

    private static string FormatRecovery(DateTimeOffset? recoveryAt) => recoveryAt is { } value
        ? $"预计 {value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 恢复"
        : "恢复时间未知";

    private (string? Token, long Version) GetTokenSnapshot()
    {
        lock (_tokenGate)
        {
            return (_accessToken, _accessTokenVersion);
        }
    }

    private static string BuildCacheKey(Uri uri, long tokenVersion) =>
        $"{uri.AbsoluteUri}|token-version={tokenVersion}";

    private static string? NormalizeAccessToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : token.Trim();

    private static string? GetETag(HttpResponseMessage response) =>
        response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;

    private static DateTimeOffset? GetLastModified(HttpResponseMessage response)
    {
        if (response.Content.Headers.LastModified is { } contentLastModified)
        {
            return contentLastModified;
        }

        var raw = ReadStringHeader(response, "Last-Modified");
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static int? ReadIntHeader(HttpResponseMessage response, string name)
    {
        var value = ReadStringHeader(response, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadStringHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DSH-Launcher", "0.1"));
        return client;
    }

    private sealed record CachedResponse(
        byte[] Body,
        long? ContentLength,
        string? ETag,
        DateTimeOffset? LastModified,
        GitHubRateLimitInfo? RateLimit);
}
