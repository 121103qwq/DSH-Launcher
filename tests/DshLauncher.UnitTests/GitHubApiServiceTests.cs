using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class GitHubApiServiceTests
{
    [Fact]
    public async Task ThemePreviewSkipsOversizedGitHubImageAndTriesTheNextOne()
    {
        using var oversizedStream = new TrackingStream(new byte[9 * 1024 * 1024]);
        using var oversizedContent = new StreamContent(oversizedStream);
        oversizedContent.Headers.ContentLength = 9 * 1024 * 1024;
        var readme = "![preview](https://raw.githubusercontent.com/example/theme/main/a.png)\n"
            + "![preview](https://raw.githubusercontent.com/example/theme/main/b.png)";
        using var handler = new FakeHttpMessageHandler(
            _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    content = Convert.ToBase64String(Encoding.UTF8.GetBytes(readme))
                }))
            },
            _ => new(HttpStatusCode.OK) { Content = oversizedContent },
            _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        using var client = new HttpClient(handler);
        var marketplace = new MarketplaceService(httpClient: client);
        var item = new MarketplaceItem("theme", "theme", null, null, "", "github:example/theme",
            "https://github.com/example/theme", "主题", MarketplaceSourceKind.GitHubTopic,
            "GitHub", MarketplaceVerificationStatus.Unverified, "");

        var preview = await marketplace.GetThemeReadmePreviewAsync(item);

        Assert.Equal(0, oversizedStream.BytesRead);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, preview.ImageBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedResponsesStopBeforeBufferingTheEntireBody(bool contentLengthKnown)
    {
        using var stream = new TrackingStream(new byte[4096]);
        using var content = new StreamContent(stream);
        if (contentLengthKnown)
        {
            content.Headers.ContentLength = 4096;
        }
        using var handler = new FakeHttpMessageHandler(_ => new(HttpStatusCode.OK) { Content = content });
        using var client = new HttpClient(handler);
        var service = new GitHubApiService(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            new Uri("https://raw.githubusercontent.com/example/project/main/preview.png"),
            maximumResponseBytes: 64));

        Assert.Equal(contentLengthKnown ? 0 : 65, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ExactLimitBodyCanBeReusedOnNotModified()
    {
        using var handler = new FakeHttpMessageHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[64])
                };
                response.Headers.TryAddWithoutValidation("ETag", "\"image\"");
                return response;
            },
            _ => new(HttpStatusCode.NotModified));
        using var client = new HttpClient(handler);
        var service = new GitHubApiService(client);
        var uri = new Uri("https://raw.githubusercontent.com/example/project/main/preview.png");

        Assert.Equal(64, (await service.GetAsync(uri, maximumResponseBytes: 64)).ReadAsByteArray().Length);
        var cached = await service.GetAsync(uri, maximumResponseBytes: 64);
        Assert.True(cached.IsFromCache);
        Assert.Equal(64, cached.ReadAsByteArray().Length);
    }

    [Fact]
    public async Task StricterLimitDoesNotReuseAnOversizedCachedBody()
    {
        using var handler = new FakeHttpMessageHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[128])
                };
                response.Headers.TryAddWithoutValidation("ETag", "\"large\"");
                return response;
            },
            _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[128]) });
        using var client = new HttpClient(handler);
        var service = new GitHubApiService(client);
        var uri = new Uri("https://raw.githubusercontent.com/example/project/main/preview.png");
        await service.GetAsync(uri);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(uri, maximumResponseBytes: 64));
        Assert.Null(handler.Requests[1].IfNoneMatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCallerCancellationRemainDistinguishable(bool cancelCaller)
    {
        using var handler = new StalledHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        using var cancellation = new CancellationTokenSource();
        if (cancelCaller)
        {
            cancellation.Cancel();
        }
        var service = new GitHubApiService(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(
            new Uri("https://api.github.com/rate_limit"), cancellation.Token));
        Assert.Equal(cancelCaller, cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ConditionalGetReusesBodyAndParsesRateLimitHeaders()
    {
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        using var handler = new FakeHttpMessageHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"cached\"}", Encoding.UTF8, "application/json")
                };
                response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "60");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "42");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", resetAt.ToString());
                return response;
            },
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotModified);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", resetAt.ToString());
                response.Headers.TryAddWithoutValidation("Retry-After", "120");
                return response;
            });
        using var client = new HttpClient(handler);
        var service = new GitHubApiService(client);
        var uri = new Uri("https://api.github.com/repos/example/project");

        var first = await service.GetAsync(uri);
        var observedBeforeSecond = DateTimeOffset.UtcNow;
        var second = await service.GetAsync(uri);
        var observedAfterSecond = DateTimeOffset.UtcNow;

        Assert.Equal("{\"name\":\"cached\"}", first.ReadAsString());
        Assert.Equal(first.ReadAsString(), second.ReadAsString());
        Assert.True(second.IsFromCache);
        Assert.True(second.IsNotModified);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("\"v1\"", handler.Requests[1].IfNoneMatch);

        var rateLimit = second.RateLimit;
        Assert.NotNull(rateLimit);
        Assert.Equal(0, rateLimit!.Remaining);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetAt), rateLimit.ResetAt);
        Assert.NotNull(rateLimit.RetryAfterAt);
        Assert.InRange(
            rateLimit.RetryAfterAt!.Value,
            observedBeforeSecond.AddSeconds(110),
            observedAfterSecond.AddSeconds(130));
        Assert.True(service.IsRateLimited);
        Assert.Equal(0, service.Status.Remaining);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses;

        public FakeHttpMessageHandler(
            params Func<HttpRequestMessage, HttpResponseMessage>[] responseFactories)
        {
            responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responseFactories);
        }

        public List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values)
                ? values.Single()
                : null;
            Requests.Add(new RequestSnapshot(ifNoneMatch));
            return Task.FromResult(responses.Dequeue()(request));
        }

        public sealed record RequestSnapshot(string? IfNoneMatch);
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
