using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class OfficialDesktopTests
{
    [Fact]
    public void DetectsAsarDesktopWithoutExposingACliOrNodeRuntime()
    {
        using var temporary = new TestDirectory();
        var root = CreateInstallation(temporary.Path);
        var installation = DeepSeekDesktopDetector.TryDetect(root);

        Assert.NotNull(installation);
        Assert.True(installation.IsOfficial);
        Assert.Equal("DeepSeek Harness", installation.ProductName);
        Assert.Equal(DshRuntimeLaunchMode.OfficialDesktopApp, installation.LaunchSpec?.Mode);
        Assert.Equal(Path.Combine(root, "DeepSeek Harness.exe"), installation.LaunchSpec?.HostPath);
        Assert.Empty(installation.NodeExecutablePath);
        Assert.Empty(installation.DshExecutablePath);
        Assert.Empty(installation.DshPackageRoot);
        Assert.False(installation.LaunchSpec!.UsesPackagedNode);
        Assert.False(DshRuntimeCommandFactory.IsUsable(installation.LaunchSpec));
        Assert.Throws<InvalidOperationException>(() => DshRuntimeCommandFactory.Create(
            installation.LaunchSpec, new[] { "web" }, root));
        Assert.Null(DshRuntimeDetector.TryResolvePackageRoot(root));
        Assert.DoesNotContain(installation.LaunchSpec.HostPath,
            NodeRuntimeDetector.GetCandidates(new[] { installation }, Array.Empty<string>()));
        Assert.DoesNotContain(installation.LaunchSpec.HostPath,
            DshRuntimeDetector.GetCandidates(null, new[] { installation }, Array.Empty<string>()));
        Assert.Single(DeepSeekDesktopDetector.DetectInstallations(new[] { root, root }));
    }

    [Fact]
    public void ChromiumChildProcessesOfOfficialDesktopAreNotSeparateOwners()
    {
        var snapshot = new ProcessTreeSnapshot(
            new Dictionary<int, string> { [1] = "DSH Launcher.exe", [10] = "DeepSeek Harness.exe", [11] = "DeepSeek Harness.exe", [12] = "node.exe" },
            new Dictionary<int, List<int>> { [1] = new() { 10 }, [10] = new() { 11, 12 } });
        Assert.False(ExternalDshHomeGuard.IsChildOfSameExecutable(snapshot, 10, "DeepSeek Harness.exe"));
        Assert.True(ExternalDshHomeGuard.IsChildOfSameExecutable(snapshot, 11, "DeepSeek Harness.exe"));
        Assert.False(ExternalDshHomeGuard.IsChildOfSameExecutable(snapshot, 12, "node.exe"));
    }

    [Fact]
    public void WindowsUninstallEntrySuppliesCustomLocationAndDisplayVersion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TestDirectory();
        var root = CreateInstallation(temporary.Path);
        var keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-Launcher-Test-"
            + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("DisplayName", "DeepSeek Harness");
                key.SetValue("InstallLocation", root);
                key.SetValue("DisplayVersion", "1.2.3");
            }
            Assert.Contains(root, DeepSeekDesktopDetector.GetInstallRootCandidates());
            Assert.Equal("1.2.3", DeepSeekDesktopDetector.TryDetect(root)?.DesktopVersion);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData("DeepSeek Harness.exe", false)]
    [InlineData("DeepSeek Harness Desktop.exe", true)]
    [InlineData("unrelated.exe", true)]
    public void IncompleteOrUnvalidatedDesktopIsRejected(string executableName, bool hasAsar)
    {
        using var temporary = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "resources"));
        File.WriteAllText(Path.Combine(temporary.Path, executableName), string.Empty);
        if (hasAsar) File.WriteAllText(Path.Combine(temporary.Path, "resources", "app.asar"), string.Empty);
        Assert.Null(DeepSeekDesktopDetector.TryDetect(temporary.Path));
    }

    [Fact]
    public void LaunchStartsOnlyTheExecutableWithTheRequestedHome()
    {
        using var temporary = new TestDirectory();
        var root = CreateInstallation(temporary.Path);
        var installation = DeepSeekDesktopDetector.TryDetect(root)!;
        var home = Path.Combine(temporary.Path, "custom-home");

        var startInfo = OfficialDesktopService.CreateStartInfo(installation, home);

        Assert.Equal(installation.LaunchSpec!.HostPath, startInfo.FileName);
        Assert.Equal(root, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(home, startInfo.Environment["DSH_HOME"]);
        Assert.False(startInfo.Environment.ContainsKey("ELECTRON_RUN_AS_NODE"));
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public void DefaultHomePrefersProcessThenUserEnvironmentThenUserProfile()
    {
        using var temporary = new TestDirectory();
        var processHome = Path.Combine(temporary.Path, "process");
        var userHome = Path.Combine(temporary.Path, "user");
        Assert.Equal(processHome, OfficialDesktopService.ResolveDefaultHome(processHome, userHome, temporary.Path));
        Assert.Equal(userHome, OfficialDesktopService.ResolveDefaultHome(null, userHome, temporary.Path));
        Assert.Equal(Path.Combine(temporary.Path, ".dsh"),
            OfficialDesktopService.ResolveDefaultHome(" ", null, temporary.Path));
    }

    [Theory]
    [InlineData("open-official-desktop")]
    [InlineData("dsh-launcher://open-official-desktop")]
    public void CommandCanOpenDesktopWithoutAManagedInstance(string argument)
    {
        var command = LauncherCommandParser.Parse(new[] { argument });
        Assert.NotNull(command);
        Assert.Equal(LauncherCommandAction.OpenOfficialDesktop, command.Action);
        Assert.Null(command.InstanceId);
    }

    [Fact]
    public void CommandCanSelectAnInstancesHome()
    {
        var command = LauncherCommandParser.Parse(new[] { "open-official-desktop", "--instance-id", "test" });
        Assert.Equal(new LauncherCommand(LauncherCommandAction.OpenOfficialDesktop, "test"), command);
    }

    [Theory]
    [InlineData("channel: latest\nurl: https://updates.example.com:8443/desktop", "latest", "https://updates.example.com:8443/desktop")]
    [InlineData("# comment\nprovider: generic\nchannel: 'beta' # comment\nurl: \"https://example.com/feed#anchor\"", "beta", "https://example.com/feed#anchor")]
    [InlineData("\uFEFFchannel: preview\r\nurl: https://example.com/feed # comment", "preview", "https://example.com/feed")]
    [InlineData("channel: 'it''s beta'\nurl: \"https://example.com/a\\u002Fb\"", "it's beta", "https://example.com/a/b")]
    [InlineData("channel: latest\nchannel: beta\nurl: https://example.com/a#b", "beta", "https://example.com/a#b")]
    [InlineData("", null, null)]
    [InlineData("provider: generic\nupdaterCacheDirName: app", null, null)]
    [InlineData("other:\n  channel: nested\n  url: https://example.com", null, null)]
    [InlineData("channel: # empty\nurl: null", null, null)]
    [InlineData("channel: 'unclosed\nurl: \"bad\\q\"", null, null)]
    [InlineData("channel: |\n  beta\nurl: [not, scalar]", null, null)]
    public void ParsesOnlySimpleTopLevelUpdateMetadata(string yaml, string? channel, string? url)
    {
        Assert.Equal((channel, url), DeepSeekDesktopDetector.ParseAppUpdateMetadata(yaml));
    }

    [Fact]
    public void OptionalUpdateMetadataIsReadOnlyAndAppearsInDesktopDescription()
    {
        using var temporary = new TestDirectory();
        var root = CreateInstallation(temporary.Path);
        var metadataPath = Path.Combine(root, "resources", "app-update.yml");
        const string metadata = "provider: generic\nchannel: beta\nurl: https://example.com/updates\n";
        File.WriteAllText(metadataPath, metadata);

        var installation = DeepSeekDesktopDetector.TryDetect(root)!;

        Assert.Equal("beta", installation.UpdateChannel);
        Assert.Equal("https://example.com/updates", installation.UpdateUrl);
        Assert.Equal("官方桌面端", installation.KindText);
        Assert.Contains("更新通道：beta", installation.Description);
        Assert.Contains("更新地址：https://example.com/updates", installation.Description);
        Assert.Equal(metadata, File.ReadAllText(metadataPath));
        Assert.False(DshRuntimeCommandFactory.IsUsable(installation.LaunchSpec));
    }

    [Fact]
    public void DesktopVersionComesFromExecutableMetadataWhenAvailable()
    {
        using var temporary = new TestDirectory();
        var root = CreateInstallation(temporary.Path);
        var executable = Path.Combine(root, "DeepSeek Harness.exe");
        File.Copy(typeof(OfficialDesktopService).Assembly.Location, executable, overwrite: true);
        var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable);

        var installation = DeepSeekDesktopDetector.TryDetect(root)!;

        Assert.False(string.IsNullOrWhiteSpace(versionInfo.FileVersion));
        Assert.Equal(versionInfo.FileVersion, installation.DesktopVersion);
        Assert.Equal(versionInfo.FileVersion, installation.LaunchSpec?.ProductVersion);
    }

    [Fact]
    public void MissingUpdateMetadataDoesNotInventAnUpdateChannel()
    {
        using var temporary = new TestDirectory();
        var installation = DeepSeekDesktopDetector.TryDetect(CreateInstallation(temporary.Path))!;

        Assert.Null(installation.UpdateChannel);
        Assert.Null(installation.UpdateUrl);
        Assert.DoesNotContain("更新通道", installation.Description);
        Assert.DoesNotContain("更新地址", installation.Description);
    }

    private static string CreateInstallation(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        File.WriteAllText(Path.Combine(root, "DeepSeek Harness.exe"), string.Empty);
        File.WriteAllText(Path.Combine(root, "resources", "app.asar"), string.Empty);
        return root;
    }
}
