using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public static class LauncherConfigSchema
{
    public const int CurrentVersion = 1;
}

public sealed class InstanceRegistryDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = LauncherConfigSchema.CurrentVersion;

    [JsonPropertyName("instances")]
    public List<ManagerInstance> Instances { get; set; } = new();
}

public enum LauncherConfigFileKind
{
    InstanceRegistry,
    LauncherSettings,
    VersionSettings
}

public sealed record LauncherConfigMigrationResult(
    string Path,
    LauncherConfigFileKind Kind,
    bool Migrated,
    string? BackupPath = null,
    string? Error = null);
