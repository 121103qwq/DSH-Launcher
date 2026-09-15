using System.Windows;
using System.Windows.Controls;
using DshLauncher;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class LinkExistingHomeWindowTests
{
    [Fact]
    public void ListRealProfiles_DoesNotInventWebForMissingProfiles()
    {
        var home = Path.Combine(Path.GetTempPath(), $"dsh-link-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            Assert.Empty(LinkExistingHomeWindow.ListRealProfiles(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ListRealProfiles_UsesOnlyValidProfileDirectories()
    {
        var home = Path.Combine(Path.GetTempPath(), $"dsh-link-ui-{Guid.NewGuid():N}");
        var profiles = Path.Combine(home, "profiles");
        Directory.CreateDirectory(Path.Combine(profiles, "web"));
        Directory.CreateDirectory(Path.Combine(profiles, "headless"));
        Directory.CreateDirectory(Path.Combine(profiles, "empty"));
        Directory.CreateDirectory(Path.Combine(profiles, "node_modules"));
        File.WriteAllText(Path.Combine(profiles, "web", "package.json"), "{}");
        File.WriteAllText(Path.Combine(profiles, "headless", "cordis.yml"), "bundles: []");
        File.WriteAllText(Path.Combine(profiles, "node_modules", "package.json"), "{}");

        try
        {
            Assert.Equal(new[] { "web", "headless" }, LinkExistingHomeWindow.ListRealProfiles(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void WindowAppliesSelectedCandidateAndDisablesRegisteredCandidateOffscreen()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-link-wpf-{Guid.NewGuid():N}");
        var home = Path.Combine(root, "existing-home");
        var registeredHome = Path.Combine(root, "registered-home");
        var runtimeRoot = Path.Combine(root, "runtime");
        Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
        Directory.CreateDirectory(registeredHome);
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(Path.Combine(home, "profiles", "web", "package.json"), "{}");

        try
        {
            ModPackMarketViewTests.WpfPageHost.Run(hostRoot =>
            {
                // WpfPageHost's shared Application is created before its
                // dispatcher starts; keep an unshown test Window from ever
                // changing the host lifetime policy.
                Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var runtime = new ManagerInstance(
                    Id: "runtime-test",
                    Name: "测试 Runtime",
                    RootPath: runtimeRoot,
                    Kind: InstanceKind.Installed,
                    DshHome: Path.Combine(root, "launcher-home"),
                    DshExecutablePath: Path.Combine(runtimeRoot, "dsh.cmd"),
                    DetectedVersion: "1.2.3",
                    RuntimeStatus: InstanceRuntimeStatus.Ready,
                    PackageManager: "npm",
                    LastError: null,
                    RegisteredAt: DateTimeOffset.UtcNow);
                var runtimeInfo = new DshRuntimeInfo(
                    IsAvailable: true,
                    ExecutablePath: runtime.DshExecutablePath,
                    Version: runtime.DetectedVersion,
                    PackageRoot: runtime.RootPath,
                    Error: null,
                    ExistingDshHome: home);
                var existing = new ManagerInstance(
                    Id: "already-linked",
                    Name: "已关联",
                    RootPath: runtimeRoot,
                    Kind: InstanceKind.Installed,
                    DshHome: registeredHome,
                    DshExecutablePath: runtime.DshExecutablePath,
                    DetectedVersion: runtime.DetectedVersion,
                    RuntimeStatus: InstanceRuntimeStatus.Ready,
                    PackageManager: "npm",
                    LastError: null,
                    RegisteredAt: DateTimeOffset.UtcNow,
                    UsesExternalDshHome: true);

                var window = new LinkExistingHomeWindow(
                    owner: null,
                    runtimeOptions: new[] { runtime },
                    existingNames: new[] { existing.Name },
                    existingInstances: new[] { existing },
                    runtimesProvider: static () => Array.Empty<DshRuntimeInfo>());
                window.CandidateList.ItemsSource = window.Candidates;
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.DataContext = window;
                hostRoot.Children.Add(content);
                var candidate = new ExistingDshHomeCandidate(
                    "桌面端现有配置",
                    home,
                    "测试安装来源",
                    runtimeInfo,
                    IsRegistered: false);
                var registeredCandidate = new ExistingDshHomeCandidate(
                    existing.Name,
                    registeredHome,
                    "已登记的外部 DSH_HOME",
                    runtimeInfo,
                    IsRegistered: true);
                window.Candidates.Add(candidate);
                window.Candidates.Add(registeredCandidate);

                Layout(hostRoot, 650, 760);
                window.CandidateList.SelectedItem = candidate;
                Layout(hostRoot, 650, 760);

                Assert.Equal(candidate.Name, window.InstanceName);
                Assert.Equal(Path.GetFullPath(home), window.DshHomePath);
                Assert.Same(runtime, window.SelectedRuntime);
                Assert.Equal(DshProfileService.DefaultProfileName, window.SelectedProfileName);

                var registeredItem = Assert.IsType<ListBoxItem>(
                    window.CandidateList.ItemContainerGenerator.ContainerFromItem(registeredCandidate));
                Assert.False(registeredItem.IsEnabled);
                window.Close();
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

}
