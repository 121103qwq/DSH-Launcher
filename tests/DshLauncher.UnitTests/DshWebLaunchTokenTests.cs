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
}
