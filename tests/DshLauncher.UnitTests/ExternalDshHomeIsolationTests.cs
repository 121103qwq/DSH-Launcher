using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ExternalDshHomeIsolationTests
{
    [Fact]
    public void SettingsStayInLauncherAndNeverAdoptExternalLauncherMetadata()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateInstance(temporary.Path);
        var oldSettings = Path.Combine(instance.DshHome, ".dsh-launcher", "version-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldSettings)!);
        File.WriteAllText(oldSettings, "{\"ActiveProfileName\":\"other\",\"SyncAllConfiguration\":true}");
        var before = File.ReadAllBytes(oldSettings);
        var service = new VersionSettingsService(paths);

        Assert.Equal("web", service.Read(instance).ActiveProfileName);
        service.Save(instance, new VersionSettingsData
        {
            ActiveProfileName = "desktop",
            SyncAllConfiguration = true,
            ConversationSyncMode = ConversationSyncMode.All,
            ConversationWorkspace = "shared",
            SyncModelProviders = true,
            IdleStopMinutes = 5
        });

        Assert.Equal(Path.Combine(paths.InstancesDirectory, instance.Id, "version-settings.json"),
            service.GetSettingsPath(instance));
        var saved = service.Read(instance);
        Assert.Equal("desktop", saved.ActiveProfileName);
        Assert.Equal(5, saved.IdleStopMinutes);
        Assert.False(saved.SyncAllConfiguration);
        Assert.False(saved.SyncModelProviders);
        Assert.Equal(ConversationSyncMode.Independent, saved.ConversationSyncMode);
        Assert.Null(saved.ConversationWorkspace);
        Assert.Equal(before, File.ReadAllBytes(oldSettings));
        Assert.Single(Directory.GetFiles(instance.DshHome, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExternalHomeIsExcludedEvenWhenGlobalOrPeerRequestsAllSync()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var service = new VersionSettingsService(paths);
        var external = CreateInstance(temporary.Path);
        var owned = external with { Id = "owned", UsesExternalDshHome = false,
            DshHome = paths.GetInstanceDshHome("owned") };
        service.SaveLauncherSettings(new LauncherSettingsData { SyncAllConfiguration = true });
        service.Save(owned, new VersionSettingsData
        {
            SyncAllConfiguration = true,
            ConversationSyncMode = ConversationSyncMode.All,
            SyncModelProviders = true
        });

        foreach (var (left, right) in new[] { (owned, external), (external, owned) })
        {
            Assert.False(service.ShouldSyncConfiguration(left, right));
            Assert.False(service.ShouldSyncConversations(left, right));
            Assert.False(service.ShouldSyncModelProviders(left, right));
        }
        Assert.True(service.ShouldSyncConfiguration(owned, owned));
        Assert.True(service.ShouldSyncConversations(owned, owned));
        Assert.True(service.ShouldSyncModelProviders(owned, owned));
        Assert.False(Directory.Exists(external.DshHome));
    }

    [Fact]
    public async Task MissingExternalHomeIsNotRecreatedOnStart()
    {
        using var temporary = new TestDirectory();
        var instance = CreateInstance(temporary.Path);
        File.WriteAllText(instance.DshExecutablePath!, "@exit /b 1");
        await using var runner = new DshInstanceRunner();
        var result = await runner.StartAsync(instance);

        Assert.False(result.IsSuccess);
        Assert.Contains("外部 DSH_HOME 不存在", result.Error);
        Assert.False(Directory.Exists(instance.DshHome));
    }

    [Fact]
    public async Task ExternalHomeWithNoSelectedProfileIsNotInitializedOnStart()
    {
        using var temporary = new TestDirectory();
        var instance = CreateInstance(temporary.Path);
        File.WriteAllText(instance.DshExecutablePath!, "@exit /b 1");
        Directory.CreateDirectory(instance.DshHome);
        File.WriteAllText(Path.Combine(instance.DshHome, "settings.yaml"), "custom: keep");
        await using var runner = new DshInstanceRunner();
        var result = await runner.StartAsync(instance);

        Assert.False(result.IsSuccess);
        Assert.Contains("不存在 Profile", result.Error);
        Assert.False(Directory.Exists(Path.Combine(instance.DshHome, "profiles")));
        Assert.Equal("custom: keep", File.ReadAllText(Path.Combine(instance.DshHome, "settings.yaml")));
    }

    [Fact]
    public void HealthRepairDoesNotRecreateMissingExternalHome()
    {
        using var temporary = new TestDirectory();
        var instance = CreateInstance(temporary.Path);
        var result = new VersionHealthService().Repair(instance, DshRuntimeInfo.Missing(), false);
        Assert.Empty(result.Actions);
        Assert.False(Directory.Exists(instance.DshHome));
    }

    private static ManagerInstance CreateInstance(string root) => new(
        Id: "external", Name: "Existing desktop", RootPath: root, Kind: InstanceKind.Installed,
        DshHome: Path.Combine(root, "existing-home"), DshExecutablePath: Path.Combine(root, "dsh.cmd"),
        DetectedVersion: "0.1.0", RuntimeStatus: InstanceRuntimeStatus.Ready,
        PackageManager: "npm", LastError: null, RegisteredAt: DateTimeOffset.UtcNow,
        UsesExternalDshHome: true);
}
