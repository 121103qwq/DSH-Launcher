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

    private static string CreateInstallation(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        File.WriteAllText(Path.Combine(root, "DeepSeek Harness.exe"), string.Empty);
        File.WriteAllText(Path.Combine(root, "resources", "app.asar"), string.Empty);
        return root;
    }
}
