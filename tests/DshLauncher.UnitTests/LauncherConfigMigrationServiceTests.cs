using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class LauncherConfigMigrationServiceTests
{
    [Fact]
    public void MigratesLegacyInstancesAndSettingsToSchemaV1WithBackups()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        Directory.CreateDirectory(paths.RootDirectory);

        var instancesPath = paths.InstancesFilePath;
        var settingsPath = Path.Combine(paths.RootDirectory, "launcher-settings.json");
        const string legacyInstances = "[{\"Id\":\"legacy\",\"Name\":\"Legacy\"}]";
        const string legacySettings = "{\"syncAllConfiguration\":true,\"workspaces\":[\"shared\"]}";
        File.WriteAllText(instancesPath, legacyInstances, new UTF8Encoding(false));
        File.WriteAllText(settingsPath, legacySettings, new UTF8Encoding(false));

        var results = new LauncherConfigMigrationService(paths).MigrateExistingConfiguration();

        var instanceResult = Assert.Single(results, result => result.Kind == LauncherConfigFileKind.InstanceRegistry);
        var settingsResult = Assert.Single(results, result => result.Kind == LauncherConfigFileKind.LauncherSettings);
        Assert.True(instanceResult.Migrated);
        Assert.True(settingsResult.Migrated);
        Assert.NotNull(instanceResult.BackupPath);
        Assert.NotNull(settingsResult.BackupPath);
        Assert.Equal(legacyInstances, File.ReadAllText(instanceResult.BackupPath!, Encoding.UTF8));
        Assert.Equal(legacySettings, File.ReadAllText(settingsResult.BackupPath!, Encoding.UTF8));

        using (var document = JsonDocument.Parse(File.ReadAllText(instancesPath, Encoding.UTF8)))
        {
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("instances").ValueKind);
        }

        using (var document = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8)))
        {
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(document.RootElement.GetProperty("syncAllConfiguration").GetBoolean());
        }
    }

    [Fact]
    public void FutureSchemaIsRejectedWithoutChangingOriginalFile()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        Directory.CreateDirectory(paths.RootDirectory);
        var path = paths.InstancesFilePath;
        const string futureSchema = "{\"schemaVersion\":99,\"instances\":[]}";
        File.WriteAllText(path, futureSchema, new UTF8Encoding(false));

        var service = new LauncherConfigMigrationService(paths);
        var exception = Assert.Throws<InvalidDataException>(() =>
            service.EnsureCurrent(path, LauncherConfigFileKind.InstanceRegistry));

        Assert.Contains("较新的 Launcher", exception.Message);
        Assert.Equal(futureSchema, File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.schema-v*.bak"));
    }

    [Fact]
    public void SettingsSaveCannotOverwriteFutureSchema()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        Directory.CreateDirectory(paths.RootDirectory);
        var path = Path.Combine(paths.RootDirectory, "launcher-settings.json");
        const string futureSchema = "{\"schemaVersion\":99,\"syncAllConfiguration\":true}";
        File.WriteAllText(path, futureSchema, new UTF8Encoding(false));

        var service = new VersionSettingsService(paths);
        Assert.Throws<InvalidDataException>(() =>
            service.SaveLauncherSettings(new LauncherSettingsData()));

        Assert.Equal(futureSchema, File.ReadAllText(path, Encoding.UTF8));
    }
}
