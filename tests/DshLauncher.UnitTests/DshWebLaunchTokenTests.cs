using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class DshWebLaunchTokenTests
{
    [Fact]
    public void ParsesTokenUrlForTheLaunchedPortOnly()
    {
        const string output = "starting\ndsh web: http://127.0.0.1:4000/?token=abc\ndsh web: http://127.0.0.1:18765/?token=IqaC-x_y\n";
        Assert.Equal("http://127.0.0.1:18765/?token=IqaC-x_y", DshInstanceRunner.TryParseAuthenticatedWebUrl(output, 18765));
        Assert.Null(DshInstanceRunner.TryParseAuthenticatedWebUrl(output, 1234));
    }

    [Fact]
    public void IgnoresBannersWithoutTokenOrNonLoopback()
    {
        Assert.Null(DshInstanceRunner.TryParseAuthenticatedWebUrl("dsh web: http://127.0.0.1:5000/\n", 5000));
        Assert.Null(DshInstanceRunner.TryParseAuthenticatedWebUrl("dsh web: http://example.com:5000/?token=x\n", 5000));
    }

    [Fact]
    public void DetectsLaunchTokenInWebUrl()
    {
        Assert.True(DshApiClient.HasLaunchToken("http://127.0.0.1:5000/?token=x"));
        Assert.False(DshApiClient.HasLaunchToken("http://127.0.0.1:5000/"));
    }

    [Fact]
    public void RedactsTokenForDisplay() =>
        Assert.Equal("http://127.0.0.1:5000/?token=***", DshApiClient.RedactLaunchToken("http://127.0.0.1:5000/?token=secret"));

    [Fact]
    public async Task ReauthenticatesOnceWhenSessionCookieIsRejected()
    {
        var calls = new List<string>();
        var posts = 0;
        using var handler = new StubHandler(request =>
        {
            calls.Add(request.Method.Method + " " + request.RequestUri!.PathAndQuery);
            if (request.Method == System.Net.Http.HttpMethod.Get)
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.SeeOther);
            return new System.Net.Http.HttpResponseMessage(++posts == 1
                ? System.Net.HttpStatusCode.Unauthorized
                : System.Net.HttpStatusCode.InternalServerError);
        });
        using var api = new DshApiClient(new System.Net.Http.HttpClient(handler));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => api.ReadModelsAsync("http://127.0.0.1:5000/?token=t"));

        Assert.Contains("500", error.Message);
        Assert.Equal(2, calls.Count(call => call == "GET /?token=t"));
        Assert.Equal(2, posts);
    }

    private sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> send)
        : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
