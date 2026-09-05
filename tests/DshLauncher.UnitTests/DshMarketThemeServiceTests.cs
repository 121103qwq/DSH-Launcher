using System.Net;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class DshMarketThemeServiceTests
{
    [Fact]
    public async Task ProbeEnablesLinkOnlyForUpstreamUiThemePreference()
    {
        Uri? observedUri = null;
        HttpMethod? observedMethod = null;
        using var handler = new DelegateHandler(async request =>
        {
            observedUri = request.RequestUri;
            observedMethod = request.Method;
            using var requestDocument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var rpcId = requestDocument.RootElement.GetProperty("rpcId").GetString();
            var body = JsonSerializer.Serialize(new
            {
                type = "server-response",
                rpcId,
                result = new
                {
                    ok = true,
                    value = new
                    {
                        namespaces = new[]
                        {
                            new
                            {
                                ns = "ui-theme",
                                revision = 7,
                                value = new { preference = "system" }
                            }
                        }
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        using var service = new DshMarketThemeService(client);

        var result = await service.ProbeThemeCapabilityAsync(CreateRunningInstance());

        Assert.True(result.IsSupported);
        Assert.Equal("system", result.Preference);
        Assert.Equal(7, result.Revision);
        Assert.Equal(HttpMethod.Post, observedMethod);
        Assert.Equal("/api/settings.describe", observedUri?.AbsolutePath);
    }

    [Fact]
    public async Task ProbeKeepsLinkDisabledWhenUiThemeNamespaceIsAbsent()
    {
        using var handler = new DelegateHandler(async request =>
        {
            using var requestDocument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var rpcId = requestDocument.RootElement.GetProperty("rpcId").GetString();
            var body = JsonSerializer.Serialize(new
            {
                type = "server-response",
                rpcId,
                result = new
                {
                    ok = true,
                    value = new
                    {
                        namespaces = new[]
                        {
                            new
                            {
                                ns = "unrelated",
                                revision = 1,
                                value = new { enabled = true }
                            }
                        }
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        using var service = new DshMarketThemeService(client);

        var result = await service.ProbeThemeCapabilityAsync(CreateRunningInstance());

        Assert.False(result.IsSupported);
        Assert.Equal(ThemeCapabilityStatus.Unsupported, result.Status);
        Assert.Contains("未暴露 ui-theme.preference", result.Reason);
    }

    private static ManagerInstance CreateRunningInstance() => new(
        Id: "theme-test",
        Name: "Theme test",
        RootPath: "C:\\runtime",
        Kind: InstanceKind.Installed,
        DshHome: "C:\\dsh-home",
        DshExecutablePath: null,
        DetectedVersion: "0.1.0-rc.11",
        RuntimeStatus: InstanceRuntimeStatus.Running,
        PackageManager: "npm",
        LastError: null,
        RegisteredAt: DateTimeOffset.UtcNow,
        Port: 34567,
        WebUrl: "http://127.0.0.1:34567/");

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
