using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Owns compatibility handling for Launcher-managed JSON configuration. Other
/// services only deserialize the current schema after this service succeeds.
/// </summary>
public sealed class LauncherConfigMigrationService
{
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly LauncherPaths _paths;

    public LauncherConfigMigrationService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    public IReadOnlyList<LauncherConfigMigrationResult> MigrateExistingConfiguration()
    {
        var targets = new List<(string Path, LauncherConfigFileKind Kind)>
        {
            (_paths.InstancesFilePath, LauncherConfigFileKind.InstanceRegistry),
            (Path.Combine(_paths.RootDirectory, "launcher-settings.json"), LauncherConfigFileKind.LauncherSettings)
        };

        if (Directory.Exists(_paths.InstancesDirectory) && !IsReparsePoint(_paths.InstancesDirectory))
        {
            foreach (var instanceDirectory in Directory.EnumerateDirectories(
                         _paths.InstancesDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(instanceDirectory))
                {
                    continue;
                }

                targets.Add((
                    Path.Combine(instanceDirectory, "dsh-home", ".dsh-launcher", "version-settings.json"),
                    LauncherConfigFileKind.VersionSettings));
            }
        }

        var results = new List<LauncherConfigMigrationResult>(targets.Count);
        foreach (var target in targets)
        {
            try
            {
                results.Add(EnsureCurrent(target.Path, target.Kind));
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException
                                       or ArgumentException)
            {
                results.Add(new LauncherConfigMigrationResult(
                    target.Path,
                    target.Kind,
                    Migrated: false,
                    Error: ex.Message));
            }
        }

        return results;
    }

    public LauncherConfigMigrationResult EnsureCurrent(string path, LauncherConfigFileKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path);
        if (!File.Exists(normalized))
        {
            return new LauncherConfigMigrationResult(normalized, kind, Migrated: false);
        }

        var gate = FileGates.GetOrAdd(normalized, static _ => new object());
        lock (gate)
        {
            var original = File.ReadAllText(normalized, Encoding.UTF8);
            var root = JsonNode.Parse(original)
                ?? throw new InvalidDataException($"配置文件为空：{normalized}");
            var version = ReadSchemaVersion(root, kind, normalized);
            if (version > LauncherConfigSchema.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"配置文件来自较新的 Launcher（schema {version}），当前仅支持 schema {LauncherConfigSchema.CurrentVersion}：{normalized}");
            }

            if (version == LauncherConfigSchema.CurrentVersion)
            {
                ValidateCurrentShape(root, kind, normalized);
                return new LauncherConfigMigrationResult(normalized, kind, Migrated: false);
            }

            var migrated = root;
            for (var current = version; current < LauncherConfigSchema.CurrentVersion; current++)
            {
                migrated = current switch
                {
                    0 => MigrateV0ToV1(migrated, kind, normalized),
                    _ => throw new InvalidDataException($"没有可用的 schema {current} 迁移：{normalized}")
                };
            }

            ValidateCurrentShape(migrated, kind, normalized);
            var backupPath = CreateBackup(normalized, version);
            WriteAtomic(normalized, migrated.ToJsonString(WriteOptions));
            return new LauncherConfigMigrationResult(normalized, kind, Migrated: true, backupPath);
        }
    }

    private static int ReadSchemaVersion(JsonNode root, LauncherConfigFileKind kind, string path)
    {
        if (kind == LauncherConfigFileKind.InstanceRegistry && root is JsonArray)
        {
            return 0;
        }

        if (root is not JsonObject document)
        {
            throw new InvalidDataException($"配置文件根节点类型无效：{path}");
        }

        if (!document.TryGetPropertyValue("schemaVersion", out var node) || node is null)
        {
            return 0;
        }

        if (node is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 0)
        {
            throw new InvalidDataException($"配置文件 schemaVersion 无效：{path}");
        }

        return version;
    }

    private static JsonNode MigrateV0ToV1(JsonNode root, LauncherConfigFileKind kind, string path)
    {
        if (kind == LauncherConfigFileKind.InstanceRegistry)
        {
            var instances = root switch
            {
                JsonArray array => array.DeepClone(),
                JsonObject document when document["instances"] is JsonArray array => array.DeepClone(),
                _ => throw new InvalidDataException($"旧实例注册文件结构无效：{path}")
            };

            return new JsonObject
            {
                ["schemaVersion"] = LauncherConfigSchema.CurrentVersion,
                ["instances"] = instances
            };
        }

        if (root is not JsonObject settings)
        {
            throw new InvalidDataException($"旧设置文件结构无效：{path}");
        }

        var migrated = (JsonObject)settings.DeepClone();
        migrated["schemaVersion"] = LauncherConfigSchema.CurrentVersion;
        return migrated;
    }

    private static void ValidateCurrentShape(JsonNode root, LauncherConfigFileKind kind, string path)
    {
        if (root is not JsonObject document)
        {
            throw new InvalidDataException($"当前配置文件根节点类型无效：{path}");
        }

        if (kind == LauncherConfigFileKind.InstanceRegistry
            && document["instances"] is not JsonArray)
        {
            throw new InvalidDataException($"实例注册文件缺少 instances 数组：{path}");
        }
    }

    private static string CreateBackup(string path, int sourceVersion)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var backup = $"{path}.schema-v{sourceVersion}-{timestamp}-{Guid.NewGuid():N}.bak";
        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("配置文件没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}
