using System.Net;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ThemeIntegrationControllerTests
{
    [Fact]
    public void NonWebProfileDisablesChatCapabilityImmediately()
    {
        using var controller = new ThemeIntegrationController();

        var generation = controller.BeginProfileSelection("web");
        controller.SetChatCapability(
            ThemeCapabilityProbeResult.Supported("system", null, "supported"), generation);
        Assert.True(controller.ChatCapability.IsSupported);

        controller.BeginProfileSelection("headless");

        Assert.Equal(ThemeCapabilityStatus.Unsupported, controller.ChatCapability.Status);
        Assert.Contains("web profile", controller.ChatCapability.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LateProbeCannotRestoreCapabilityAfterProfileChanges()
    {
        var requestReceived = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var responseReady = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            requestReceived.TrySetResult(document.RootElement.GetProperty("rpcId").GetString()!);
            return await responseReady.Task;
        });
        using var client = new HttpClient(handler);
        using var marketService = new DshMarketThemeService(client);
        using var controller = new ThemeIntegrationController(marketService);

        var webGeneration = controller.BeginProfileSelection("web");
        var probe = controller.ProbeChatCapabilityAsync(
            CreateRunningInstance(),
            profileGeneration: webGeneration);
        var rpcId = await requestReceived.Task;

        controller.BeginProfileSelection("headless");
        responseReady.SetResult(CreateSupportedResponse(rpcId));
        await probe;

        Assert.Equal(ThemeCapabilityStatus.Unsupported, controller.ChatCapability.Status);
    }

    [Fact]
    public async Task SwitchingBackToWebReprobesCapability()
    {
        using var handler = new DelegateHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var rpcId = document.RootElement.GetProperty("rpcId").GetString();
            return CreateSupportedResponse(rpcId!);
        });
        using var client = new HttpClient(handler);
        using var marketService = new DshMarketThemeService(client);
        using var controller = new ThemeIntegrationController(marketService);

        controller.BeginProfileSelection("headless");
        var webGeneration = controller.BeginProfileSelection("web");

        Assert.Equal(ThemeCapabilityStatus.Unknown, controller.ChatCapability.Status);
        await controller.ProbeChatCapabilityAsync(
            CreateRunningInstance(),
            profileGeneration: webGeneration);

        Assert.True(controller.ChatCapability.IsSupported);
    }

    private static HttpResponseMessage CreateSupportedResponse(string rpcId) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
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
                                    value = new { preference = "system" }
                                }
                            }
                        }
                    }
                }),
                Encoding.UTF8,
                "application/json")
        };

    private static ManagerInstance CreateRunningInstance() => new(
        Id: "theme-controller-test",
        Name: "Theme controller test",
        RootPath: "C:\\runtime",
        Kind: InstanceKind.Installed,
        DshHome: "C:\\dsh-home",
        DshExecutablePath: null,
        DetectedVersion: "0.1.0",
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
