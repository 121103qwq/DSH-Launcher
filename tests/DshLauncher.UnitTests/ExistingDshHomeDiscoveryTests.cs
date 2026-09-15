using System.Text;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ExistingDshHomeDiscoveryTests
{
    [Fact]
    public async Task RuntimeHomesAreDiscoveredAndNormalizedWithoutDuplicates()
    {
        using var temporary = new TestDirectory();
        var home = CreateHome(temporary.Path, "runtime-home");
        var runtime = CreateRuntime(temporary.Path, "runtime", home);
        var duplicate = runtime with { ExistingDshHome = Path.Combine(home, ".") };

        var candidates = await new ExistingDshHomeDiscoveryService(
                new LauncherPaths(Path.Combine(temporary.Path, "launcher")))
            .DiscoverAsync(new[] { runtime, duplicate }, Array.Empty<ManagerInstance>());

        var candidate = Assert.Single(candidates, item =>
            string.Equals(item.DshHome, Path.GetFullPath(home), StringComparison.OrdinalIgnoreCase));
        Assert.Same(runtime, candidate.Runtime);
        Assert.Contains("Runtime", candidate.Source);
        Assert.False(candidate.IsRegistered);
    }

    [Fact]
    public async Task RegisteredExternalHomeIsKeptAndMarkedEvenWhenRuntimeIsKnown()
    {
        using var temporary = new TestDirectory();
        var launcherRoot = Path.Combine(temporary.Path, "launcher");
        var home = CreateHome(temporary.Path, "registered-home");
        var runtime = CreateRuntime(temporary.Path, "runtime", home: null);
        var instance = new ManagerInstance(
            "registered-instance",
            "我的旧桌面",
            runtime.PackageRoot!,
            InstanceKind.Installed,
            home,
            runtime.ExecutablePath,
            runtime.Version,
            InstanceRuntimeStatus.Ready,
            "npm",
            null,
            DateTimeOffset.UtcNow,
            DshLaunchSpec: runtime.LaunchSpec,
            UsesExternalDshHome: true);

        var candidates = await new ExistingDshHomeDiscoveryService(new LauncherPaths(launcherRoot))
            .DiscoverAsync(new[] { runtime }, new[] { instance });

        var candidate = Assert.Single(candidates, item =>
            string.Equals(item.DshHome, Path.GetFullPath(home), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("我的旧桌面", candidate.Name);
        Assert.True(candidate.IsRegistered);
        Assert.Same(runtime, candidate.Runtime);
        Assert.Contains("已登记", candidate.Source);
    }

    [Fact]
    public async Task LauncherOwnedAndRuntimeInstallHomesAreExcluded()
    {
        using var temporary = new TestDirectory();
        var launcherRoot = Path.Combine(temporary.Path, "launcher");
        var runtimeRoot = Path.Combine(temporary.Path, "runtime");
        var ownedHome = CreateHome(launcherRoot, "instances", "owned");
        var installHome = CreateHome(runtimeRoot, "nested-home");
        var validHome = CreateHome(temporary.Path, "valid-home");
        var runtime = CreateRuntime(temporary.Path, "runtime", validHome) with
        {
            PackageRoot = runtimeRoot
        };

        var candidates = await new ExistingDshHomeDiscoveryService(new LauncherPaths(launcherRoot))
            .DiscoverAsync(
                new[]
                {
                    runtime with { ExistingDshHome = ownedHome },
                    runtime with { ExistingDshHome = installHome },
                    runtime with { ExistingDshHome = validHome }
                },
                Array.Empty<ManagerInstance>());

        Assert.DoesNotContain(candidates, item =>
            string.Equals(item.DshHome, Path.GetFullPath(ownedHome), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, item =>
            string.Equals(item.DshHome, Path.GetFullPath(installHome), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, item =>
            string.Equals(item.DshHome, Path.GetFullPath(validHome), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfiguredAndDefaultHomesAreBothOffered()
    {
        using var temporary = new TestDirectory();
        var previous = Environment.GetEnvironmentVariable("DSH_HOME");
        var profile = Path.Combine(temporary.Path, "profile");
        var configured = CreateHome(temporary.Path, "configured-home");
        var defaultHome = CreateHome(profile, ".dsh");
        try
        {
            Environment.SetEnvironmentVariable("DSH_HOME", configured);
            var candidates = await new ExistingDshHomeDiscoveryService(
                    new LauncherPaths(Path.Combine(temporary.Path, "launcher")),
                    profile)
                .DiscoverAsync(Array.Empty<DshRuntimeInfo>(), Array.Empty<ManagerInstance>());

            Assert.Contains(candidates, item =>
                string.Equals(item.DshHome, Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase)
                && item.Source.Contains("环境变量", StringComparison.Ordinal));
            Assert.Contains(candidates, item =>
                string.Equals(item.DshHome, Path.GetFullPath(defaultHome), StringComparison.OrdinalIgnoreCase)
                && item.Source.Contains("~/.dsh", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", previous);
        }
    }

    private static DshRuntimeInfo CreateRuntime(string root, string name, string? home)
    {
        var runtimeRoot = Path.Combine(root, name);
        Directory.CreateDirectory(runtimeRoot);
        var executable = Path.Combine(runtimeRoot, "dsh.cmd");
        File.WriteAllText(executable, "@echo off", Encoding.UTF8);
        return new DshRuntimeInfo(
            true,
            executable,
            "1.0.0",
            runtimeRoot,
            null,
            LaunchSpec: new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.DirectCommand,
                executable),
            ExistingDshHome: home);
    }

    private static string CreateHome(params string[] parts)
    {
        var home = Path.Combine(parts);
        Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        File.WriteAllText(Path.Combine(home, "settings.yaml"), "settings: keep", Encoding.UTF8);
        return home;
    }
}
