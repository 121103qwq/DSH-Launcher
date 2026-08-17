using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Text.RegularExpressions;
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
            var entries = JsonSerializer.Deserialize<List<ManagerInstance>>(json, JsonOptions)
                ?? new List<ManagerInstance>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var validated = new List<ManagerInstance>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is null)
                {
                    throw new InvalidDataException("实例注册文件包含空实例记录。");
                }

                var safe = ValidateStoredEntry(entry);
                if (!seenIds.Add(safe.Id))
                {
                    throw new InvalidDataException($"实例注册文件包含重复 ID：{safe.Id}");
                }

                validated.Add(safe);
            }

            return validated;
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
        string? dshHome = null,
        DshRuntimeLaunchSpec? dshLaunchSpec = null)
    {
        var normalizedName = NormalizeName(name);
        if (!Enum.IsDefined(typeof(InstanceKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "实例类型无效。");
        }

        var normalizedRoot = NormalizeDirectory(rootPath, "实例目录");
        var entries = Load().ToList();

        var id = Guid.NewGuid().ToString("N");
        var expectedHome = Path.GetFullPath(_paths.GetInstanceDshHome(id));
        var home = Path.GetFullPath(dshHome ?? expectedHome);
        if (!string.Equals(home, expectedHome, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("实例 DSH_HOME 必须位于 Launcher 自己的隔离目录中。");
        }

        Directory.CreateDirectory(home);
        if (IsReparsePoint(home))
        {
            throw new IOException("实例 DSH_HOME 不能是符号链接或重解析点。");
        }
        var normalizedExecutable = NormalizeOptionalFile(dshExecutablePath);
        var normalizedLaunchSpec = NormalizeLaunchSpec(dshLaunchSpec)
            ?? (normalizedExecutable is null
                ? null
                : new DshRuntimeLaunchSpec(DshRuntimeLaunchMode.DirectCommand, normalizedExecutable));

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
            DateTimeOffset.UtcNow,
            DshLaunchSpec: normalizedLaunchSpec);

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
        updated = ValidateStoredEntry(updated);
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

    private ManagerInstance ValidateStoredEntry(ManagerInstance entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)
            || string.IsNullOrWhiteSpace(entry.Name)
            || string.IsNullOrWhiteSpace(entry.RootPath)
            || string.IsNullOrWhiteSpace(entry.DshHome))
        {
            throw new InvalidDataException("实例注册文件包含缺少 ID、根目录或 DSH_HOME 的记录。");
        }

        if (!Regex.IsMatch(entry.Id, "^[A-Za-z0-9_-]{8,80}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException($"实例 ID 不符合格式：{entry.Id}");
        }

        if (!Enum.IsDefined(typeof(InstanceKind), entry.Kind)
            || !Enum.IsDefined(typeof(InstanceRuntimeStatus), entry.RuntimeStatus))
        {
            throw new InvalidDataException($"实例 {entry.Id} 包含未知的枚举状态。");
        }

        var rootPath = Path.GetFullPath(entry.RootPath);
        var expectedHome = Path.GetFullPath(_paths.GetInstanceDshHome(entry.Id));
        var dshHome = Path.GetFullPath(entry.DshHome);
        if (!string.Equals(dshHome, expectedHome, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"实例 {entry.Id} 的 DSH_HOME 不在 Launcher 隔离目录中。");
        }

        if (IsReparsePoint(dshHome))
        {
            throw new InvalidDataException($"实例 {entry.Id} 的 DSH_HOME 不能是符号链接或重解析点。");
        }

        var executable = NormalizeOptionalFile(entry.DshExecutablePath);
        var launchSpec = NormalizeLaunchSpec(entry.DshLaunchSpec)
            ?? (executable is null
                ? null
                : new DshRuntimeLaunchSpec(DshRuntimeLaunchMode.DirectCommand, executable));
        var status = entry.RuntimeStatus;
        var error = entry.LastError;
        if (entry.Kind == InstanceKind.Installed
            && !DshRuntimeCommandFactory.IsUsable(launchSpec)
            && status == InstanceRuntimeStatus.Ready)
        {
            status = InstanceRuntimeStatus.Unknown;
            error ??= "DSh 可执行入口当前不可用。";
        }

        return entry with
        {
            Name = NormalizeName(entry.Name),
            RootPath = rootPath,
            DshHome = dshHome,
            DshExecutablePath = executable,
            DshLaunchSpec = launchSpec,
            RuntimeStatus = status,
            LastError = error
        };
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
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

        if (IsReparsePoint(normalized))
        {
            throw new IOException($"{label}不能是符号链接或重解析点：{normalized}");
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
        return File.Exists(normalized) && !IsReparsePoint(normalized) ? normalized : null;
    }

    private static DshRuntimeLaunchSpec? NormalizeLaunchSpec(DshRuntimeLaunchSpec? spec)
    {
        if (spec is null || !Enum.IsDefined(spec.Mode))
        {
            return null;
        }

        var host = NormalizeOptionalFile(spec.HostPath);
        if (host is null)
        {
            return null;
        }

        var entry = NormalizeOptionalFile(spec.EntryPointPath);
        if (spec.Mode != DshRuntimeLaunchMode.DirectCommand && entry is null)
        {
            return null;
        }

        return spec with
        {
            HostPath = host,
            EntryPointPath = entry,
            NodeExecutablePath = NormalizeOptionalFile(spec.NodeExecutablePath),
            PnpmScriptPath = NormalizeOptionalFile(spec.PnpmScriptPath),
            ProductName = string.IsNullOrWhiteSpace(spec.ProductName) ? null : spec.ProductName.Trim(),
            ProductVersion = string.IsNullOrWhiteSpace(spec.ProductVersion) ? null : spec.ProductVersion.Trim()
        };
    }
}
