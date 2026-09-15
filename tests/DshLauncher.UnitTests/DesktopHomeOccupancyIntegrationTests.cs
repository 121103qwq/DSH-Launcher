using System.Diagnostics;
using System.IO;
using System.Text;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;
using Xunit.Sdk;
using Xunit.Abstractions;
using System.Runtime.Loader;
using System.Text.Json;

namespace DshLauncher.UnitTests;

[Collection("DesktopOccupancy")]
public sealed class DesktopHomeOccupancyIntegrationTests
{
    private readonly ITestOutputHelper _output;
    public DesktopHomeOccupancyIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PerformanceWithTwoDesktopOwners()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateDesktopFixture(temporary.Path);
        var home = CreateHome(temporary.Path, "first");
        var otherHome = CreateHome(temporary.Path, "second");
        using var first = StartDesktopHelper(fixture.HostPath, home);
        using var second = StartDesktopHelper(fixture.HostPath, otherHome);
        try
        {
            WaitUntilDesktopHelperIsReadable(first, home);
            WaitUntilDesktopHelperIsReadable(second, otherHome);
            var instance = CreateExternalInstance(fixture, home);
            var guard = new ExternalDshHomeGuard();
            var baselinePath = Environment.GetEnvironmentVariable("DSH_PERF_BASELINE_DLL");
            if (!string.IsNullOrWhiteSpace(baselinePath))
            {
                var context = new AssemblyLoadContext("occupancy-perf-baseline", isCollectible: true);
                try
                {
                    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
                    var instanceType = assembly.GetType(typeof(ManagerInstance).FullName!)!;
                    var originalInstance = JsonSerializer.Deserialize(JsonSerializer.Serialize(instance), instanceType);
                    var guardType = assembly.GetType(typeof(ExternalDshHomeGuard).FullName!)!;
                    var originalGuard = Activator.CreateInstance(guardType);
                    var getConflict = guardType.GetMethod(nameof(ExternalDshHomeGuard.GetConflict))!;
                    Measure("Baseline", () => (string?)getConflict.Invoke(originalGuard, new[] { originalInstance }));
                }
                finally { context.Unload(); }
            }
            Measure("Current", () => guard.GetConflict(instance));
        }
        finally
        {
            StopHelper(first);
            StopHelper(second);
        }
    }

    private void Measure(string label, Func<string?> check)
    {
        for (var warmup = 0; warmup < 5; warmup++) Assert.NotNull(check());
        const int samples = 20;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var sample = 0; sample < samples; sample++) Assert.NotNull(check());
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"{label} / two desktops: {elapsed.TotalMilliseconds / samples:F3} ms/call; {allocated / samples:N0} bytes/call.");
    }

    [Fact]
    public async Task RealGuardTracksCopiedDesktopHomeAndDiscoveryWithoutTouchingInstalledDesktop()
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitOperatingSystem
            || !Environment.Is64BitProcess)
        {
            return;
        }

        using var temporary = new TestDirectory();
        var fixture = CreateDesktopFixture(temporary.Path);
        var home = CreateHome(temporary.Path, "running-home");
        var differentHome = CreateHome(temporary.Path, "different-home");
        var instance = CreateExternalInstance(fixture, home);
        var differentHomeInstance = instance with
        {
            Id = "different-home",
            Name = "different-home",
            DshHome = differentHome
        };
        var guard = new ExternalDshHomeGuard();

        // Existing real Desktop processes are allowed to make the guard fail
        // closed. Record that baseline so this test only attributes changes to
        // its own copied helper, rather than requiring a pristine machine.
        var baselineSameHome = guard.GetConflict(instance);
        var baselineDifferentHome = guard.GetConflict(differentHomeInstance);

        using var helper = StartDesktopHelper(fixture.HostPath, home);
        try
        {
            WaitUntilDesktopHelperIsReadable(helper, home);

            var sameHomeConflict = guard.GetConflict(instance);
            Assert.Contains($"PID {helper.Id}", sameHomeConflict ?? string.Empty, StringComparison.Ordinal);

            var differentHomeConflict = guard.GetConflict(differentHomeInstance);
            Assert.Equal(baselineDifferentHome, differentHomeConflict);
            Assert.DoesNotContain($"PID {helper.Id}", differentHomeConflict ?? string.Empty, StringComparison.Ordinal);

            var managedSelf = instance with
            {
                RuntimeOwnership = InstanceRuntimeOwnership.Managed,
                ProcessId = helper.Id,
                ProcessStartedAt = ReadStartedAt(helper)
            };
            var managedSelfConflict = guard.GetConflict(managedSelf);
            Assert.Equal(baselineSameHome, managedSelfConflict);
            Assert.DoesNotContain($"PID {helper.Id}", managedSelfConflict ?? string.Empty, StringComparison.Ordinal);

            var candidates = await new ExistingDshHomeDiscoveryService(
                    new LauncherPaths(Path.Combine(temporary.Path, "launcher")))
                .DiscoverAsync(Array.Empty<DshRuntimeInfo>(), Array.Empty<ManagerInstance>());
            var runningCandidate = candidates.FirstOrDefault(candidate =>
                ExternalDshHomeGuard.SameHome(candidate.DshHome, home));
            Assert.NotNull(runningCandidate);
            Assert.Contains("运行中", runningCandidate!.Source, StringComparison.Ordinal);
        }
        finally
        {
            StopHelper(helper);
        }

        var afterExitConflict = guard.GetConflict(instance);
        Assert.Equal(baselineSameHome, afterExitConflict);
        Assert.DoesNotContain($"PID {helper.Id}", afterExitConflict ?? string.Empty, StringComparison.Ordinal);
    }

    private static DesktopFixture CreateDesktopFixture(string root)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemCommandShell = Path.Combine(systemDirectory, "cmd.exe");
        Assert.True(File.Exists(systemCommandShell), "系统 cmd.exe 不可用，无法执行 Desktop 占用集成测试。");

        var installRoot = Path.Combine(root, "desktop-install");
        var unpackedRoot = Path.Combine(installRoot, "resources", "app.asar.unpacked");
        var packageRoot = Path.Combine(unpackedRoot, "node_modules", "@deepseek-ai", "dsh");
        var dshEntryPoint = Path.Combine(packageRoot, "lib", "bin.js");
        var desktopCli = Path.Combine(unpackedRoot, "lib", "desktop-cli.js");
        var pnpmScript = Path.Combine(unpackedRoot, "node_modules", "pnpm", "bin", "pnpm.mjs");
        var host = Path.Combine(installRoot, "DSH Desktop.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(dshEntryPoint)!);
        Directory.CreateDirectory(Path.GetDirectoryName(desktopCli)!);
        Directory.CreateDirectory(Path.GetDirectoryName(pnpmScript)!);
        File.Copy(systemCommandShell, host);
        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.0.0-test\"}",
            new UTF8Encoding(false));
        File.WriteAllText(dshEntryPoint, string.Empty, Encoding.ASCII);
        File.WriteAllText(desktopCli, string.Empty, Encoding.ASCII);
        File.WriteAllText(pnpmScript, string.Empty, Encoding.ASCII);
        File.WriteAllText(
            Path.Combine(installRoot, "VERSION.txt"),
            "DSH Desktop v0.0.0-test\n",
            new UTF8Encoding(false));

        var installation = DeepSeekDesktopDetector.TryDetect(installRoot);
        Assert.NotNull(installation);
        Assert.Equal(Path.GetFullPath(host), installation!.LaunchSpec?.HostPath);
        return new DesktopFixture(installRoot, host, installation);
    }

    private static ManagerInstance CreateExternalInstance(DesktopFixture fixture, string home) =>
        new(
            Id: "running-home",
            Name: "running-home",
            RootPath: fixture.InstallRoot,
            Kind: InstanceKind.Installed,
            DshHome: home,
            DshExecutablePath: fixture.Installation.DshExecutablePath,
            DetectedVersion: fixture.Installation.DshVersion,
            RuntimeStatus: InstanceRuntimeStatus.Ready,
            PackageManager: null,
            LastError: null,
            RegisteredAt: DateTimeOffset.UtcNow,
            DshLaunchSpec: fixture.Installation.LaunchSpec,
            UsesExternalDshHome: true);

    private static string CreateHome(string root, string name)
    {
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
        File.WriteAllText(Path.Combine(home, "settings.yaml"), "settings: test\n", new UTF8Encoding(false));
        return home;
    }

    private static Process StartDesktopHelper(string host, string home)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = host,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(host)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 120 127.0.0.1 > nul");
        startInfo.Environment["DSH_HOME"] = home;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("占用测试辅助进程未启动。");
    }

    private static void WaitUntilDesktopHelperIsReadable(Process process, string expectedHome)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    var environment = ProcessDshHomeReader.Read(process);
                    if (environment.IsReadable
                        && string.Equals(environment.DshHome, expectedHome, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // The helper exited before its environment became readable.
            }

            Thread.Sleep(50);
        }

        throw new XunitException("无法读取占用测试辅助进程的 DSH_HOME。");
    }

    private static DateTimeOffset ReadStartedAt(Process process) =>
        new DateTimeOffset(process.StartTime).ToUniversalTime();

    private static void StopHelper(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The helper already exited while the test was finishing.
        }
    }

    private sealed record DesktopFixture(
        string InstallRoot,
        string HostPath,
        DeepSeekDesktopInstallation Installation);
}
