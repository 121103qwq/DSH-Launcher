using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class InstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LauncherPaths _paths;

    public InstanceRegistry(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    public string StoragePath => _paths.InstancesFilePath;

    public IReadOnlyList<ManagerInstance> Load()
    {
        if (!File.Exists(StoragePath))
        {
            return Array.Empty<ManagerInstance>();
        }

        try
        {
            var json = File.ReadAllText(StoragePath, Encoding.UTF8);
            var entries = JsonSerializer.Deserialize<List<ManagerInstance>>(json, JsonOptions);
            return entries ?? new List<ManagerInstance>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"实例注册文件格式无效：{StoragePath}", ex);
        }
    }

    public ManagerInstance Register(
        string name,
        string rootPath,
        InstanceKind kind,
        string? dshExecutablePath = null,
        string? detectedVersion = null,
        string? packageManager = null,
        string? dshHome = null)
    {
        var normalizedName = NormalizeName(name);
        var normalizedRoot = NormalizeDirectory(rootPath, "实例目录");
        var entries = Load().ToList();

        if (entries.Any(entry => string.Equals(entry.RootPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("这个目录已经注册为 DSh 实例。");
        }

        var id = Guid.NewGuid().ToString("N");
        var home = Path.GetFullPath(dshHome ?? _paths.GetInstanceDshHome(id));
        Directory.CreateDirectory(home);
        var normalizedExecutable = NormalizeOptionalFile(dshExecutablePath);

        var entry = new ManagerInstance(
            id,
            normalizedName,
            normalizedRoot,
            kind,
            home,
            normalizedExecutable,
            detectedVersion,
            normalizedExecutable is not null ? InstanceRuntimeStatus.Ready : InstanceRuntimeStatus.Unknown,
            packageManager,
            null,
            DateTimeOffset.UtcNow);

        entries.Add(entry);
        Save(entries);
        return entry;
    }

    public bool Unregister(string id)
    {
        var entries = Load().ToList();
        var removed = entries.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        if (removed == 0)
        {
            return false;
        }

        Save(entries);
        return true;
    }

    public ManagerInstance Update(ManagerInstance updated)
    {
        var entries = Load().ToList();
        var index = entries.FindIndex(entry => string.Equals(entry.Id, updated.Id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException("找不到要更新的 DSh 实例。");
        }

        entries[index] = updated;
        Save(entries);
        return updated;
    }

    private void Save(IReadOnlyCollection<ManagerInstance> entries)
    {
        var directory = Path.GetDirectoryName(StoragePath)
            ?? throw new InvalidOperationException("实例注册文件没有父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{StoragePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, StoragePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("实例名称不能为空。", nameof(name));
        }

        if (normalized.Length > 80)
        {
            throw new ArgumentException("实例名称不能超过 80 个字符。", nameof(name));
        }

        return normalized;
    }

    private static string NormalizeDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{label}不能为空。", nameof(path));
        }

        var normalized = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"{label}不存在：{normalized}");
        }

        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? NormalizeOptionalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = Path.GetFullPath(path.Trim());
        return File.Exists(normalized) ? normalized : null;
    }
}
