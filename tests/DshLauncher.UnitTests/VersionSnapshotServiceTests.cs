using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class VersionSnapshotServiceTests
{
    [Fact]
    public void LocalSnapshotRestoresPnpmWorkspace()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateTestInstance(
            "snapshot-local",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "dsh-home"));
        var workspacePath = PrepareWorkspace(instance);
        const string original = "allowBuilds:\n  demo: set this to true or false\n";
        File.WriteAllText(workspacePath, original, new UTF8Encoding(false));

        var service = new VersionSnapshotService(paths);
        var snapshot = service.CreateSnapshot(instance, "workspace");

        File.WriteAllText(workspacePath, "allowBuilds:\n  demo: false\n", new UTF8Encoding(false));
        service.RestoreSnapshot(instance, snapshot.FilePath);

        Assert.Equal(original, File.ReadAllText(workspacePath));
    }

    [Fact]
    public void PasswordSnapshotRestoresPnpmWorkspace()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateTestInstance(
            "snapshot-password",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "dsh-home"));
        var workspacePath = PrepareWorkspace(instance);
        const string original = "allowBuilds:\n  demo: set this to true or false\n";
        File.WriteAllText(workspacePath, original, new UTF8Encoding(false));
        var snapshotPath = Path.Combine(temporary.Path, "workspace.dshpsnapshot");
        const string password = "test-password";

        var service = new VersionSnapshotService(paths);
        service.ExportPasswordSnapshot(instance, snapshotPath, password);

        File.WriteAllText(workspacePath, "allowBuilds:\n  demo: false\n", new UTF8Encoding(false));
        service.RestorePasswordSnapshot(instance, snapshotPath, password);

        Assert.Equal(original, File.ReadAllText(workspacePath));
    }

    [Fact]
    public void LegacyManifestWithoutPnpmWorkspaceLeavesCurrentFileUntouched()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateTestInstance(
            "snapshot-legacy",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "dsh-home"));
        var workspacePath = PrepareWorkspace(instance);
        const string currentWorkspace = "allowBuilds:\n  demo: false\n";
        File.WriteAllText(workspacePath, currentWorkspace, new UTF8Encoding(false));
        var settingsPath = Path.Combine(instance.DshHome, "settings.yaml");
        File.WriteAllText(settingsPath, "current-settings", new UTF8Encoding(false));
        var snapshotPath = Path.Combine(temporary.Path, "legacy.dshpsnapshot");
        const string password = "legacy-password";
        var plainSnapshot = CreateLegacySnapshotWithoutWorkspace();
        var encryptedSnapshot = new PasswordSnapshotEncryptionService().Encrypt(plainSnapshot, password);
        File.WriteAllBytes(snapshotPath, encryptedSnapshot);

        File.WriteAllText(settingsPath, "changed-settings", new UTF8Encoding(false));
        var service = new VersionSnapshotService(paths);
        service.RestorePasswordSnapshot(instance, snapshotPath, password);

        Assert.Equal("settings-from-legacy-snapshot", File.ReadAllText(settingsPath));
        Assert.Equal(currentWorkspace, File.ReadAllText(workspacePath));
    }

    [Fact]
    public void ExternalSnapshotUsesLauncherVersionSettingsAndPreservesLegacyHomeMetadata()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateTestInstance(
            "snapshot-external",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "dsh-home")) with
        {
            UsesExternalDshHome = true
        };
        var homeSettings = Path.Combine(instance.DshHome, "settings.yaml");
        const string originalHomeSettings = "home-settings: original\n";
        File.WriteAllText(homeSettings, originalHomeSettings, new UTF8Encoding(false));

        var legacySettings = Path.Combine(instance.DshHome, ".dsh-launcher", "version-settings.json");
        const string legacySettingsText = "{\"legacy\":true}";
        Directory.CreateDirectory(Path.GetDirectoryName(legacySettings)!);
        File.WriteAllText(legacySettings, legacySettingsText, new UTF8Encoding(false));

        var launcherSettings = Path.Combine(paths.InstancesDirectory, instance.Id, "version-settings.json");
        const string currentLauncherSettings = "{\"ActiveProfileName\":\"desktop\"}";
        Directory.CreateDirectory(Path.GetDirectoryName(launcherSettings)!);
        File.WriteAllText(launcherSettings, currentLauncherSettings, new UTF8Encoding(false));

        var service = new VersionSnapshotService(paths);
        var snapshot = service.CreateSnapshot(instance, "external settings");

        File.WriteAllText(homeSettings, "home-settings: changed\n", new UTF8Encoding(false));
        File.WriteAllText(launcherSettings, "{\"ActiveProfileName\":\"web\"}", new UTF8Encoding(false));
        service.RestoreSnapshot(instance, snapshot.FilePath);

        Assert.Equal(originalHomeSettings, File.ReadAllText(homeSettings));
        Assert.Equal(currentLauncherSettings, File.ReadAllText(launcherSettings));
        Assert.Equal(legacySettingsText, File.ReadAllText(legacySettings));
    }

    [Fact]
    public void ExternalSnapshotOperationsDoNotRecreateMissingHome()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateTestInstance(
            "snapshot-missing-external",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "dsh-home")) with
        {
            UsesExternalDshHome = true
        };
        Directory.Delete(instance.DshHome, recursive: true);
        var service = new VersionSnapshotService(paths);

        Assert.Throws<InvalidOperationException>(() => service.CreateSnapshot(instance, "missing"));
        Assert.False(Directory.Exists(instance.DshHome));
        Assert.Throws<InvalidOperationException>(() => service.ExportPasswordSnapshot(
            instance,
            Path.Combine(temporary.Path, "missing.dshpsnapshot"),
            "password"));
        Assert.False(Directory.Exists(instance.DshHome));
        Assert.Throws<InvalidOperationException>(() => service.RestoreSnapshot(
            instance,
            Path.Combine(temporary.Path, "missing.dshsnapshot")));
        Assert.False(Directory.Exists(instance.DshHome));
    }

    private static ManagerInstance CreateTestInstance(string id, string rootPath, string dshHome)
    {
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(dshHome);
        return new ManagerInstance(
            Id: id,
            Name: id,
            RootPath: rootPath,
            Kind: InstanceKind.Installed,
            DshHome: dshHome,
            DshExecutablePath: null,
            DetectedVersion: "test",
            RuntimeStatus: InstanceRuntimeStatus.Ready,
            PackageManager: "npm",
            LastError: null,
            RegisteredAt: DateTimeOffset.UtcNow);
    }

    private static string PrepareWorkspace(ManagerInstance instance)
    {
        var profileDirectory = Path.Combine(instance.DshHome, "profiles", "web");
        Directory.CreateDirectory(profileDirectory);
        File.WriteAllText(Path.Combine(profileDirectory, "package.json"), "{}", new UTF8Encoding(false));
        return Path.Combine(profileDirectory, "pnpm-workspace.yaml");
    }

    private static byte[] CreateLegacySnapshotWithoutWorkspace()
    {
        using var plainSnapshot = new MemoryStream();
        using (var archive = new ZipArchive(plainSnapshot, ZipArchiveMode.Create, leaveOpen: true))
        {
            var settingsEntry = archive.CreateEntry("files/settings.yaml");
            using (var writer = new StreamWriter(settingsEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write("settings-from-legacy-snapshot");
            }

            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(new
                {
                    formatVersion = 1,
                    instanceId = "legacy-instance",
                    instanceName = "legacy",
                    createdAt = DateTimeOffset.UtcNow,
                    reason = "legacy",
                    managedFiles = new[] { "settings.yaml" },
                    presentFiles = new[] { "settings.yaml" },
                    isAutomatic = false
                }));
            }
        }

        return plainSnapshot.ToArray();
    }
}
