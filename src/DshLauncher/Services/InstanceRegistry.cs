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
    private readonly LauncherConfigMigrationService _migrationService;

    public InstanceRegistry(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
        _migrationService = new LauncherConfigMigrationService(_paths);
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
            _migrationService.EnsureCurrent(StoragePath, LauncherConfigFileKind.InstanceRegistry);
            var json = File.ReadAllText(StoragePath, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<InstanceRegistryDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("实例注册文件为空。");
            if (document.SchemaVersion != LauncherConfigSchema.CurrentVersion)
            {
                throw new InvalidDataException($"实例注册文件 schema 不受支持：{document.SchemaVersion}");
            }

            var entries = document.Instances ?? new List<ManagerInstance>();
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

            var deduplicated = DeduplicateImportedInstances(validated);
            if (deduplicated.Count != validated.Count)
            {
                Save(deduplicated);
            }

            return deduplicated;
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

    /// <summary>
    /// Registers an existing DSH_HOME in place. The HOME is only referenced in
    /// the Launcher registry; no directory is created and no file contents are
    /// copied or changed.
    /// </summary>
    public ManagerInstance RegisterExistingHome(
        string name,
        string dshHome,
        ManagerInstance runtimeTemplate)
    {
        ArgumentNullException.ThrowIfNull(runtimeTemplate);

        var normalizedName = NormalizeName(name);
        if (!Enum.IsDefined(typeof(InstanceKind), runtimeTemplate.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeTemplate), "实例类型无效。");
        }

        var normalizedRoot = NormalizeDirectory(runtimeTemplate.RootPath, "运行时目录");
        var normalizedHome = NormalizeExternalHome(dshHome);
        var entries = Load().ToList();
        EnsureExternalHomeAvailable(normalizedHome, entries);

        var normalizedExecutable = NormalizeOptionalFile(runtimeTemplate.DshExecutablePath);
        var normalizedLaunchSpec = NormalizeLaunchSpec(runtimeTemplate.DshLaunchSpec)
            ?? (normalizedExecutable is null
                ? null
                : new DshRuntimeLaunchSpec(DshRuntimeLaunchMode.DirectCommand, normalizedExecutable));
        var status = runtimeTemplate.Kind == InstanceKind.Installed
            && DshRuntimeCommandFactory.IsUsable(normalizedLaunchSpec)
            ? InstanceRuntimeStatus.Ready
            : InstanceRuntimeStatus.Unknown;

        var entry = runtimeTemplate with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = normalizedName,
            RootPath = normalizedRoot,
            DshHome = normalizedHome,
            DshExecutablePath = normalizedExecutable,
            RuntimeStatus = status,
            LastError = null,
            RegisteredAt = DateTimeOffset.UtcNow,
            ProcessId = null,
            Port = null,
            WebUrl = null,
            DshLaunchSpec = normalizedLaunchSpec,
            LastUsedAt = null,
            ImportedFromDshHome = null,
            ProcessStartedAt = null,
            UsesExternalDshHome = true,
            RuntimeOwnership = InstanceRuntimeOwnership.None,
            RuntimeResourceText = null
        };

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

        if (updated.UsesExternalDshHome)
        {
            var externalError = GetExternalHomeValidationError(updated.DshHome, allowMissing: true);
            if (externalError is not null)
            {
                throw new InvalidDataException(externalError);
            }

            EnsureExternalHomeAvailable(updated.DshHome, entries, updated.Id);
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
            var json = JsonSerializer.Serialize(new InstanceRegistryDocument
            {
                Instances = entries.ToList()
            }, JsonOptions);
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
        var dshHome = Path.GetFullPath(entry.DshHome);
        var executable = NormalizeOptionalFile(entry.DshExecutablePath);
        var launchSpec = NormalizeLaunchSpec(entry.DshLaunchSpec)
            ?? (executable is null
                ? null
                : new DshRuntimeLaunchSpec(DshRuntimeLaunchMode.DirectCommand, executable));
        var status = entry.RuntimeStatus;
        var error = entry.LastError;

        if (entry.UsesExternalDshHome)
        {
            var homeMissing = !Directory.Exists(dshHome);
            if (homeMissing)
            {
                status = InstanceRuntimeStatus.Missing;
                error ??= "外部 DSH_HOME 不存在，仍保留记录以便解除绑定。";
            }
            else
            {
                var externalError = GetExternalHomeValidationError(dshHome, allowMissing: false);
                if (externalError is not null)
                {
                    status = InstanceRuntimeStatus.Error;
                    error ??= externalError;
                }
            }

            return entry with
            {
                Name = NormalizeName(entry.Name),
                RootPath = rootPath,
                DshHome = dshHome,
                DshExecutablePath = executable,
                DshLaunchSpec = launchSpec,
                RuntimeStatus = status,
                LastError = error,
                ProcessStartedAt = status == InstanceRuntimeStatus.Running ? entry.ProcessStartedAt : null,
                ImportedFromDshHome = null,
                UsesExternalDshHome = true
            };
        }

        var expectedHome = Path.GetFullPath(_paths.GetInstanceDshHome(entry.Id));
        if (!string.Equals(dshHome, expectedHome, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"实例 {entry.Id} 的 DSH_HOME 不在 Launcher 隔离目录中。");
        }

        if (IsReparsePoint(dshHome))
        {
            throw new InvalidDataException($"实例 {entry.Id} 的 DSH_HOME 不能是符号链接或重解析点。");
        }

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
            LastError = error,
            ProcessStartedAt = status == InstanceRuntimeStatus.Running ? entry.ProcessStartedAt : null,
            ImportedFromDshHome = NormalizeOptionalPath(entry.ImportedFromDshHome)
        };
    }

    private string NormalizeExternalHome(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("外部 DSH_HOME 不能为空。", nameof(path));
        }

        if (!Path.IsPathFullyQualified(path.Trim()))
        {
            throw new ArgumentException("外部 DSH_HOME 必须是绝对路径。", nameof(path));
        }

        var normalized = Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var error = GetExternalHomeValidationError(normalized, allowMissing: false);
        if (error is not null)
        {
            throw new IOException(error);
        }

        return normalized;
    }

    private void EnsureExternalHomeAvailable(
        string home,
        IEnumerable<ManagerInstance> entries,
        string? excludedId = null)
    {
        foreach (var existing in entries)
        {
            if (string.Equals(existing.Id, excludedId, StringComparison.Ordinal))
            {
                continue;
            }

            var existingHome = NormalizeOptionalPath(existing.DshHome);
            if (existingHome is not null && PathsOverlap(home, existingHome))
            {
                throw new InvalidOperationException("该 DSH_HOME 已被其它实例注册，或与其它实例目录重叠。");
            }
        }
    }

    private string? GetExternalHomeValidationError(string home, bool allowMissing)
    {
        var root = Path.GetPathRoot(home);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(home),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            return "外部 DSH_HOME 不能是文件系统根目录。";
        }

        if (PathsOverlap(home, _paths.RootDirectory))
        {
            return "外部 DSH_HOME 不能是 Launcher 目录或其父子目录。";
        }

        if (!Directory.Exists(home))
        {
            return allowMissing ? null : "外部 DSH_HOME 不存在。";
        }

        if (IsReparsePoint(home))
        {
            return "外部 DSH_HOME 不能是符号链接或重解析点。";
        }

        if (!HasDshHomeStructure(home))
        {
            return "所选目录不像有效的 DSH_HOME（缺少 settings.yaml、profiles、sessions、storages 或凭据等结构）。";
        }

        return null;
    }

    private static bool HasDshHomeStructure(string home) =>
        IsRegularFile(Path.Combine(home, "settings.yaml"))
        || IsRegularFile(Path.Combine(home, ".credentials.yaml"))
        || IsRegularDirectory(Path.Combine(home, "profiles"))
        || IsRegularDirectory(Path.Combine(home, "sessions"))
        || IsRegularDirectory(Path.Combine(home, "storages"))
        || IsRegularDirectory(Path.Combine(home, "skills"))
        || IsRegularDirectory(Path.Combine(home, ".agents"));

    private static bool IsRegularFile(string path) =>
        File.Exists(path) && !IsReparsePoint(path);

    private static bool IsRegularDirectory(string path) =>
        Directory.Exists(path) && !IsReparsePoint(path);

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || IsPathInside(left, right)
        || IsPathInside(right, left);

    private static bool IsPathInside(string path, string parent)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        if (string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedParent + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
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

    private static string? NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ManagerInstance> DeduplicateImportedInstances(
        IReadOnlyList<ManagerInstance> entries)
    {
        var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in entries
                     .Where(static entry => entry.Kind == InstanceKind.Installed)
                     .Select(static entry => (Entry: entry, Identity: GetImportedRuntimeIdentity(entry)))
                     .Where(static item => item.Identity is not null)
                     .GroupBy(static item => item.Identity!, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            foreach (var duplicate in group
                         .OrderByDescending(static item => GetPreservationRank(item.Entry.RuntimeStatus))
                         .ThenByDescending(static item => item.Entry.RecentSortAt)
                         .ThenByDescending(static item => item.Entry.RegisteredAt)
                         .Skip(1))
            {
                duplicateIds.Add(duplicate.Entry.Id);
            }
        }

        return duplicateIds.Count == 0
            ? entries
            : entries.Where(entry => !duplicateIds.Contains(entry.Id)).ToArray();
    }

    private static string? GetImportedRuntimeIdentity(ManagerInstance entry)
    {
        var runtimeRoot = NormalizeOptionalPath(entry.RootPath);
        var importedHome = NormalizeOptionalPath(entry.ImportedFromDshHome);
        return runtimeRoot is null || importedHome is null
            ? null
            : $"{runtimeRoot}\0{importedHome}";
    }

    private static int GetPreservationRank(InstanceRuntimeStatus status) => status switch
    {
        InstanceRuntimeStatus.Running => 4,
        InstanceRuntimeStatus.Ready => 3,
        InstanceRuntimeStatus.Stopped => 2,
        InstanceRuntimeStatus.Unknown => 1,
        _ => 0
    };

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
