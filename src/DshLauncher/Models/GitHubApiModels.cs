using System.Net;
using System.Net.Http;
using System.Text;

namespace DshLauncher.Models;

/// <summary>
/// GitHub response quota information returned by the API headers.
/// </summary>
public sealed record GitHubRateLimitInfo(
    int? Limit,
    int? Remaining,
    DateTimeOffset? ResetAt,
    DateTimeOffset? RetryAfterAt,
    DateTimeOffset ObservedAt)
{
    public DateTimeOffset? RecoveryAt => RetryAfterAt ?? ResetAt;

    public bool IsLimited => Remaining == 0
        || RetryAfterAt is not null
        || Remaining is null && ResetAt is not null;

    public TimeSpan? RecoveryDelay => RecoveryAt is { } recovery
        ? recovery - ObservedAt
        : null;
}

/// <summary>
/// Current GitHub status exposed by the marketplace callers.
/// </summary>
public sealed record GitHubApiStatus(
    GitHubRateLimitInfo? RateLimit,
    bool IsRateLimited,
    string? Message)
{
    public int? Remaining => RateLimit?.Remaining;

    public DateTimeOffset? ResetAt => RateLimit?.ResetAt;

    public DateTimeOffset? RetryAfterAt => RateLimit?.RetryAfterAt;

    public DateTimeOffset? RecoveryAt => RateLimit?.RecoveryAt;
}

/// <summary>
/// A GitHub rate-limit failure with the parsed recovery metadata attached.
/// </summary>
public sealed class GitHubRateLimitException : HttpRequestException
{
    public GitHubRateLimitException(
        string message,
        GitHubRateLimitInfo? rateLimit,
        HttpStatusCode statusCode,
        Uri requestUri)
        : base(message, inner: null, statusCode)
    {
        RateLimit = rateLimit;
        RequestUri = requestUri;
    }

    public GitHubRateLimitInfo? RateLimit { get; }

    public Uri RequestUri { get; }

    public int? Remaining => RateLimit?.Remaining;

    public DateTimeOffset? RecoveryAt => RateLimit?.RecoveryAt;
}

/// <summary>
/// GitHub GET response detached from HttpClient, so it can also represent a
/// 304 whose body was restored from the validator cache.
/// </summary>
public sealed class GitHubApiResponse
{
    private readonly byte[] _body;

    internal GitHubApiResponse(
        HttpStatusCode statusCode,
        byte[] body,
        long? contentLength,
        bool isFromCache,
        bool isNotModified,
        string? etag,
        DateTimeOffset? lastModified,
        GitHubRateLimitInfo? rateLimit)
    {
        StatusCode = statusCode;
        _body = body;
        ContentLength = contentLength;
        IsFromCache = isFromCache;
        IsNotModified = isNotModified;
        ETag = etag;
        LastModified = lastModified;
        RateLimit = rateLimit;
    }

    public HttpStatusCode StatusCode { get; }

    public bool IsSuccessStatusCode =>
        ((int)StatusCode >= 200 && (int)StatusCode <= 299) || IsNotModified;

    public bool IsFromCache { get; }

    public bool IsNotModified { get; }

    public long? ContentLength { get; }

    public string? ETag { get; }

    public DateTimeOffset? LastModified { get; }

    public GitHubRateLimitInfo? RateLimit { get; }

    public string ReadAsString() => Encoding.UTF8.GetString(_body);

    public byte[] ReadAsByteArray() => _body.ToArray();

    public void EnsureSuccessStatusCode()
    {
        if (!IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub 请求失败（HTTP {(int)StatusCode} {StatusCode}）。",
                inner: null,
                StatusCode);
        }
    }
}
