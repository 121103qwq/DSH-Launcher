using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class OfficialDesktopVersionListTests
{
    [Fact]
    public void SelectingOfficialDesktopDisablesInstanceOperationsWithoutSelectingARuntime()
    {
        using var temporary = new TestDirectory();
        ModPackMarketViewTests.WpfPageHost.Run(hostRoot =>
        {
            var paths = new LauncherPaths(temporary.Path);
            var registry = new InstanceRegistry(paths);
            var instance = new ManagerInstance(
                "managed", "测试实例", temporary.Path, InstanceKind.Installed,
                Path.Combine(temporary.Path, "home"), null, "1.0.0",
                InstanceRuntimeStatus.Ready, "npm", null, DateTimeOffset.UtcNow);
            var desktop = new DeepSeekDesktopInstallation(
                temporary.Path, "2.0.0", "", "", "", "", IsOfficial: true, UpdateChannel: "beta");
            var selectedInstances = new List<ManagerInstance>();
            var page = new VersionControlWindow(
                new[] { instance }, instance, new VersionPackageService(registry, paths),
                () => instance, _ => { }, _ => { }, selectedInstances.Add,
                _ => Task.FromResult<IReadOnlyList<ManagerInstance>>(Array.Empty<ManagerInstance>()),
                new VersionHealthService(), new VersionSnapshotService(paths), new ExtensionService(),
                () => NodeRuntimeInfo.Missing(), () => DshRuntimeInfo.Missing(), _ => false,
                updated => updated, () => { });
            page.OfficialDesktops.Add(desktop);
            hostRoot.Children.Add(page);

            page.SelectedEntry = desktop;

            Assert.Same(desktop, page.SelectedEntry);
            Assert.Null(page.SelectedVersion);
            Assert.Empty(selectedInstances);
            Assert.Single(page.Versions);
            Assert.Equal("2 个", page.VersionCountText);
            Assert.Contains("官方桌面端", page.SelectedVersionDetails);
            Assert.Contains("更新通道：beta", page.SelectedVersionDetails);
            Assert.False(page.CanClone);
            Assert.False(page.CanDelete);
            Assert.False(page.CanCheck);
            Assert.False(page.CanRepair);
            Assert.False(page.CanSnapshot);
            Assert.False(page.CanRollback);
            Assert.Empty(page.Snapshots);

            page.SelectedEntry = instance;

            Assert.Same(instance, page.SelectedVersion);
            Assert.Same(instance, Assert.Single(selectedInstances));
            Assert.True(page.CanClone);
            Assert.True(page.CanDelete);
            Assert.True(page.CanCheck);
            Assert.True(page.CanSnapshot);
        });
    }
}
