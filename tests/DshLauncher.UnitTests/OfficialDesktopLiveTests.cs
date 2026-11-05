using System.Diagnostics;
using DshLauncher.Services;
using Xunit;
using Xunit.Abstractions;

namespace DshLauncher.UnitTests;

// Opt-in: runs only on a machine with the real official desktop installed and running
// (set DSH_LIVE_OFFICIAL=1). Verifies detection, owner HOME reading and check latency.
public sealed class OfficialDesktopLiveTests(ITestOutputHelper output)
{
    private static bool Enabled => OperatingSystem.IsWindows()
        && Environment.GetEnvironmentVariable("DSH_LIVE_OFFICIAL") == "1";

    [Fact]
    public void RealInstallIsDetectedAndRunningOwnerReportsHome()
    {
        if (!Enabled) return;
        var installation = new OfficialDesktopService().Detect();
        Assert.NotNull(installation);
        output.WriteLine($"root={installation!.InstallRoot} exe={installation.LaunchSpec?.HostPath} version={installation.DesktopVersion} channel={installation.UpdateChannel} url={installation.UpdateUrl}");
        Assert.False(string.IsNullOrWhiteSpace(installation.DesktopVersion));
        if (!new OfficialDesktopService().IsRunning(installation)) return;
        var owners = ExternalDshHomeGuard.ReadDesktopOwners(null);
        foreach (var owner in owners) output.WriteLine($"owner pid={owner.ProcessId} product={owner.ProductName} home={owner.DshHome}");
        Assert.Contains(owners, owner => owner.ProductName == "DeepSeek Harness"
            && owner.DshHome is not null
            && ExternalDshHomeGuard.SameHome(owner.DshHome, OfficialDesktopService.ResolveDefaultHome()));
    }

    [Fact]
    public void OccupancyCheckLatency()
    {
        if (!Enabled) return;
        ExternalDshHomeGuard.ReadDesktopOwners(null);
        var watch = Stopwatch.StartNew();
        const int runs = 10;
        for (var i = 0; i < runs; i++) ExternalDshHomeGuard.ReadDesktopOwners(null);
        var detect = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++) DeepSeekDesktopDetector.DetectInstallations();
        output.WriteLine($"ReadDesktopOwners avg={watch.Elapsed.TotalMilliseconds / runs - detect.Elapsed.TotalMilliseconds / runs:F1}ms(approx) DetectInstallations avg={detect.Elapsed.TotalMilliseconds / runs:F1}ms");
    }
}
