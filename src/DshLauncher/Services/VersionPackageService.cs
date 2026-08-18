using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Keeps version operations centered on the registered DSH_HOME. The DSh
/// runtime directory may be shared; each registered version still receives a
/// separate DSH_HOME.
/// </summary>
public sealed class VersionPackageService
{
    public const int CurrentPackageFormatVersion = 1;
    public const string DefaultPackageExtension = ".dshpack";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly Regex SensitiveInlineValue = new(
        @"(?<prefix>[""']?(?<key>[A-Za-z0-9_.-]+)[""']?\s*:\s*)(?<value>[""'][^""']*[""']|[^,\r\n}\]]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveUrlUserInfo = new(
        @"(?i)(https?://)[^/\s:@]+(?::[^/\s@]*)?@",
        RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveUrlQuery = new(
        @"(?i)([?&](?:api[-_]?key|token|secret|password|access[-_]?token|refresh[-_]?token|credential(?:s)?)=)[^&#\s]+",
        RegexOptions.CultureInvariant);

    private readonly InstanceRegistry _registry;
    private readonly LauncherPaths _paths;
    private readonly VersionSettingsService _versionSettingsService;

    public VersionPackageService(InstanceRegistry registry, LauncherPaths? paths = null)
    {
        _registry = registry;
        _paths = paths ?? new LauncherPaths();
        _versionSettingsService = new VersionSettingsService(_paths);
    }

    public string PackageExtension => ReadPackageExtension();

    public void SavePackageExtension(string extension)
    {
        var normalized = NormalizePackageExtension(extension);
        Directory.CreateDirectory(_paths.RootDirectory);
        var temporary = $"{_paths.VersionSettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(new VersionSettings(normalized), JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _paths.VersionSettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public ManagerInstance CreateCleanVersion(ManagerInstance template, string name) =>
        RegisterLike(template, name);

    public ManagerInstance CloneVersion(ManagerInstance template, string name)
    {
        var created = RegisterLike(template, name);
        try
        {
            CopyDirectoryWithoutReparsePoints(template.DshHome, created.DshHome);
            return created;
        }
        catch
        {
            _registry.Unregister(created.Id);
            TryDeleteGeneratedHome(created.DshHome);
            throw;
        }
    }

    public void DeleteVersion(ManagerInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.RuntimeStatus == InstanceRuntimeStatus.Running
            || instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            throw new InvalidOperationException("运行中或 Attached 版本不能删除，请先停止或解除外部连接。 ");
        }

        var registered = _registry.Load().FirstOrDefault(item =>
            string.Equals(item.Id, instance.Id, StringComparison.Ordinal));
        if (registered is null)
        {
            throw new InvalidOperationException("找不到要删除的版本注册记录。 ");
        }

        var expectedHome = Path.GetFullPath(_paths.GetInstanceDshHome(instance.Id));
        var registeredHome = Path.GetFullPath(registered.DshHome);
        var requestedHome = Path.GetFullPath(instance.DshHome);
        if (!string.Equals(registeredHome, expectedHome, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(requestedHome, expectedHome, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("版本 DSH_HOME 不在 Launcher 的隔离目录中，已拒绝删除。 ");
        }

        DeleteGeneratedDirectory(expectedHome, "版本 DSH_HOME");
        DeleteGeneratedDirectory(_paths.GetInstanceBackupDirectory(instance.Id), "版本备份目录");
        if (!_registry.Unregister(instance.Id))
        {
            throw new InvalidOperationException("版本数据已删除，但注册记录没有成功更新。 ");
        }
    }

    public string ExportPackage(
        ManagerInstance instance,
        string packagePath,
        VersionExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("整合包路径不能为空。", nameof(packagePath));
        }

        var destination = Path.GetFullPath(packagePath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("整合包没有父目录。");
        Directory.CreateDirectory(directory);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        var contents = new List<string>
        {
            "dsh-home/.dsh-launcher/version-settings.json"
        };
        var plugins = Array.Empty<string>();
        var skills = new List<string>();
        var agentPresets = new List<string>();
        var providers = Array.Empty<string>();

        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var settings = _versionSettingsService.Read(instance);
                settings.NodeExecutablePath = null;
                AddTextEntry(
                    archive,
                    "dsh-home/.dsh-launcher/version-settings.json",
                    JsonSerializer.Serialize(settings, JsonOptions));

                if (options.IncludeProviderConfiguration)
                {
                    var providerPath = Path.Combine(instance.DshHome, "settings.yaml");
                    if (File.Exists(providerPath))
                    {
                        AddTextEntry(
                            archive,
                            "dsh-home/settings.yaml",
                            SanitizeSettingsText(File.ReadAllText(providerPath, Encoding.UTF8)));
                        contents.Add("dsh-home/settings.yaml");
                    }

                    var providerStatePath = Path.Combine(instance.DshHome, ".dsh-launcher", "providers.json");
                    if (File.Exists(providerStatePath))
                    {
                        AddTextEntry(
                            archive,
                            "dsh-home/.dsh-launcher/providers.json",
                            SanitizeSettingsText(File.ReadAllText(providerStatePath, Encoding.UTF8)));
                        contents.Add("dsh-home/.dsh-launcher/providers.json");
                    }

                    providers = ReadProviderNames(instance);
                }

                if (options.IncludePluginConfiguration)
                {
                    var pluginPath = Path.Combine(instance.DshHome, "profiles", "web", "package.json");
                    var pluginConfiguration = CreatePluginConfiguration(pluginPath);
                    if (pluginConfiguration is not null)
                    {
                        AddTextEntry(
                            archive,
                            "dsh-home/profiles/web/package.json",
                            pluginConfiguration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        contents.Add("dsh-home/profiles/web/package.json");
                        plugins = ReadPluginNames(pluginConfiguration);
                    }
                }

                skills.AddRange(AddSafeTextDirectoryEntries(
                    archive,
                    Path.Combine(instance.DshHome, "skills"),
                    "dsh-home/skills",
                    contents));
                skills.AddRange(AddSafeTextDirectoryEntries(
                    archive,
                    Path.Combine(instance.DshHome, ".agents", "skills"),
                    "dsh-home/.agents/skills",
                    contents));
                agentPresets.AddRange(AddSafeTextDirectoryEntries(
                    archive,
                    Path.Combine(instance.DshHome, ".agent-presets"),
                    "dsh-home/.agent-presets",
                    contents));

                var manifest = new Dictionary<string, object?>
                {
                    ["formatVersion"] = CurrentPackageFormatVersion,
                    ["format"] = "dsh-launcher-design",
                    ["product"] = "DSH Launcher",
                    ["name"] = instance.Name,
                    ["description"] = $"DSH Launcher 版本“{instance.Name}”的可分享配置。",
                    ["createdAt"] = DateTimeOffset.UtcNow,
                    ["dshVersion"] = instance.DetectedVersion,
                    ["detectedVersion"] = instance.DetectedVersion,
                    ["packageManager"] = instance.PackageManager,
                    ["kind"] = instance.KindText,
                    ["plugins"] = plugins,
                    ["skills"] = skills.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    ["agentPresets"] = agentPresets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    ["providers"] = providers,
                    ["workflow"] = "standard",
                    ["contents"] = contents,
                    ["privacy"] = new Dictionary<string, bool>
                    {
                        ["apiKeys"] = false,
                        ["sessions"] = false
                    }
                };
                AddTextEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public DshPackPreview PreviewPackage(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("找不到整合包文件。", packagePath);
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var manifest = ReadPackageManifest(archive);
        return new DshPackPreview(
            string.IsNullOrWhiteSpace(manifest.Name) ? "未命名整合包" : manifest.Name,
            manifest.Description ?? "未提供说明。",
            manifest.DshVersion ?? manifest.DetectedVersion,
            manifest.CreatedAt,
            manifest.Plugins,
            manifest.Skills,
            manifest.AgentPresets,
            manifest.Providers,
            manifest.Workflow);
    }

    public ManagerInstance ImportPackage(string packagePath, ManagerInstance template)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("找不到整合包文件。", packagePath);
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), "manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            throw new InvalidDataException("整合包缺少 manifest.json。 ");
        }

        var manifest = ReadPackageManifest(archive, manifestEntry);

        var name = string.IsNullOrWhiteSpace(manifest.Name)
            ? $"{template.Name}（导入）"
            : manifest.Name.Trim();
        var runtime = ResolveImportRuntime(template);
        var created = _registry.Register(
            name,
            runtime.RootPath,
            runtime.Kind,
            runtime.DshExecutablePath,
            manifest.DshVersion ?? manifest.DetectedVersion ?? template.DetectedVersion,
            manifest.PackageManager ?? runtime.PackageManager,
            dshLaunchSpec: runtime.LaunchSpec);
        try
        {
            foreach (var entry in archive.Entries)
            {
                ExtractHomeEntry(entry, created.DshHome);
            }

            return created;
        }
        catch
        {
            _registry.Unregister(created.Id);
            TryDeleteGeneratedHome(created.DshHome);
            throw;
        }
    }

    private (string RootPath, InstanceKind Kind, string? DshExecutablePath, DshRuntimeLaunchSpec? LaunchSpec, string? PackageManager)
        ResolveImportRuntime(ManagerInstance template)
    {
        var configuredInstallDirectory = _versionSettingsService.ResolveDshInstallDirectory();
        var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(configuredInstallDirectory);
        if (packageRoot is null || !Directory.Exists(packageRoot))
        {
            throw new InvalidOperationException(
                $"当前 DSh 安装位置没有可用运行时：{configuredInstallDirectory}。请先在设置/诊断中安装或检测 DSh。 ");
        }

        var launchSpec = DshRuntimeDetector.CreateLaunchSpecForPackageRoot(packageRoot);
        if (launchSpec is null
            && string.Equals(
                DshRuntimeDetector.TryResolvePackageRoot(template.RootPath),
                packageRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            launchSpec = template.EffectiveDshLaunchSpec;
        }

        if (!DshRuntimeCommandFactory.IsUsable(launchSpec))
        {
            throw new InvalidOperationException(
                $"已找到 DSh 运行目录，但无法构造启动入口：{packageRoot}。请先重新检测或安装 DSh。 ");
        }

        return (
            packageRoot,
            InstanceKind.Installed,
            launchSpec!.HostPath,
            launchSpec,
            "npm");
    }

    private ManagerInstance RegisterLike(ManagerInstance template, string name) =>
        _registry.Register(
            name,
            template.RootPath,
            template.Kind,
            template.DshExecutablePath,
            template.DetectedVersion,
            template.PackageManager,
            dshLaunchSpec: template.DshLaunchSpec);

    private string ReadPackageExtension()
    {
        if (!File.Exists(_paths.VersionSettingsPath))
        {
            return DefaultPackageExtension;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_paths.VersionSettingsPath, Encoding.UTF8));
            var extension = ReadString(document.RootElement, "packageExtension")
                ?? ReadString(document.RootElement, "PackageExtension");
            return NormalizePackageExtension(extension ?? DefaultPackageExtension);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            return DefaultPackageExtension;
        }
    }

    private static string NormalizePackageExtension(string extension)
    {
        var normalized = extension.Trim();
        if (normalized.Length == 0 || normalized[0] != '.')
        {
            normalized = "." + normalized;
        }

        if (normalized.Length < 2
            || normalized.Length > 16
            || normalized.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException("整合包扩展名只能包含字母、数字和短横线，例如 .dshpack。", nameof(extension));
        }

        return normalized.ToLowerInvariant();
    }

    private static void ExtractHomeEntry(ZipArchiveEntry entry, string destinationRoot)
    {
        var path = entry.FullName.Replace('\\', '/');
        if (!path.StartsWith("dsh-home/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relative = path["dsh-home/".Length..];
        if (string.IsNullOrWhiteSpace(relative))
        {
            return;
        }

        if (IsForbiddenImportPath(relative))
        {
            return;
        }

        var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
        var normalizedRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("整合包包含越界路径。 ");
        }

        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destination);
            return;
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("整合包条目没有父目录。 ");
        Directory.CreateDirectory(parent);

        if (string.Equals(relative, "settings.yaml", StringComparison.OrdinalIgnoreCase))
        {
            using var settingsStream = entry.Open();
            using var reader = new StreamReader(settingsStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            File.WriteAllText(destination, SanitizeSettingsText(reader.ReadToEnd()), new UTF8Encoding(false));
            return;
        }

        if (relative.EndsWith("version-settings.json", StringComparison.OrdinalIgnoreCase))
        {
            using var versionStream = entry.Open();
            using var reader = new StreamReader(versionStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            File.WriteAllText(destination, SanitizeVersionSettingsText(reader.ReadToEnd()), new UTF8Encoding(false));
            return;
        }

        if (IsShareableTextFile(relative))
        {
            using var resourceStream = entry.Open();
            using var resourceReader = new StreamReader(resourceStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            File.WriteAllText(destination, SanitizeSettingsText(resourceReader.ReadToEnd()), new UTF8Encoding(false));
            return;
        }

        using var copyStream = entry.Open();
        using var targetStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        copyStream.CopyTo(targetStream);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: false);
        writer.Write(content);
    }

    private static JsonObject? CreatePluginConfiguration(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            if (root is null)
            {
                return null;
            }

            var result = new JsonObject();
            if (root["dependencies"] is JsonObject dependencies)
            {
                var sanitizedDependencies = new JsonObject();
                foreach (var dependency in dependencies)
                {
                    if (dependency.Value is JsonValue value
                        && value.TryGetValue<string>(out var specification)
                        && specification is not null)
                    {
                        sanitizedDependencies[dependency.Key] = SanitizePluginSpecification(specification);
                    }
                    else
                    {
                        sanitizedDependencies[dependency.Key] = dependency.Value?.DeepClone();
                    }
                }

                result["dependencies"] = sanitizedDependencies;
            }

            if (root["dsh"] is JsonObject dsh
                && dsh["profile"] is JsonObject profile
                && profile["bundles"] is JsonNode bundles)
            {
                result["dsh"] = new JsonObject
                {
                    ["profile"] = new JsonObject
                    {
                        ["bundles"] = bundles.DeepClone()
                    }
                };
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string SanitizePluginSpecification(string specification)
    {
        var sanitized = SensitiveUrlUserInfo.Replace(specification, "$1");
        return SensitiveUrlQuery.Replace(sanitized, "$1<redacted>");
    }

    private static string[] ReadPluginNames(JsonObject pluginConfiguration)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pluginConfiguration["dependencies"] is JsonObject dependencies)
        {
            foreach (var dependency in dependencies)
            {
                names.Add(dependency.Key);
            }
        }

        if (pluginConfiguration["dsh"] is JsonObject dsh
            && dsh["profile"] is JsonObject profile
            && profile["bundles"] is JsonArray bundles)
        {
            foreach (var bundle in bundles)
            {
                if (bundle is JsonValue value && value.TryGetValue<string>(out var name)
                    && !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] ReadProviderNames(ManagerInstance instance)
    {
        try
        {
            return new ModelService()
                .Read(instance)
                .Select(provider => provider.Provider)
                .Where(provider => !string.IsNullOrWhiteSpace(provider))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> AddSafeTextDirectoryEntries(
        ZipArchive archive,
        string sourceRoot,
        string archiveRoot,
        ICollection<string> contents)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return Array.Empty<string>();
        }

        RejectReparsePoint(sourceRoot, "整合包源目录");
        var names = new List<string>();
        foreach (var file in EnumerateSafeFiles(sourceRoot))
        {
            var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            if (IsForbiddenImportPath(relative) || !IsShareableTextFile(relative))
            {
                continue;
            }

            var archivePath = $"{archiveRoot}/{relative}";
            AddTextEntry(archive, archivePath, SanitizeSettingsText(File.ReadAllText(file, Encoding.UTF8)));
            contents.Add(archivePath);
            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(name, "SKILL", StringComparison.OrdinalIgnoreCase))
            {
                name = new DirectoryInfo(Path.GetDirectoryName(file) ?? sourceRoot).Name;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            // 跳过 DSh 生成的 junction 等链接，导出内容只包含实例自己的文件。
            if (IsReparsePoint(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                foreach (var nested in EnumerateSafeFiles(entry))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static bool IsShareableTextFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static PackageManifest ReadPackageManifest(ZipArchive archive, ZipArchiveEntry? knownEntry = null)
    {
        var manifestEntry = knownEntry ?? archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), "manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            throw new InvalidDataException("整合包缺少 manifest.json。 ");
        }

        using var stream = manifestEntry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var formatVersion = root.TryGetProperty("formatVersion", out var format)
            && format.TryGetInt32(out var parsedFormat)
            ? parsedFormat
            : 0;
        if (formatVersion != CurrentPackageFormatVersion)
        {
            throw new InvalidDataException($"不支持的整合包格式版本：{formatVersion}。当前支持 {CurrentPackageFormatVersion}。 ");
        }

        return new PackageManifest(
            ReadString(root, "name"),
            ReadString(root, "description"),
            ReadString(root, "dshVersion"),
            ReadString(root, "detectedVersion"),
            ReadString(root, "packageManager"),
            ReadDateTimeOffset(root, "createdAt"),
            ReadStringArray(root, "plugins"),
            ReadStringArray(root, "skills"),
            ReadStringArray(root, "agentPresets"),
            ReadStringArray(root, "providers"),
            ReadString(root, "workflow"));
    }

    private static string SanitizeSettingsText(string content)
    {
        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                var key = line[..separator].Trim().Trim('"', '\'');
                if (IsSensitiveKey(key))
                {
                    lines[index] = $"{line[..(separator + 1)]} \"<redacted>\"";
                }
            }
        }

        var sanitized = string.Join('\n', lines);
        sanitized = SensitiveInlineValue.Replace(sanitized, match =>
            IsSensitiveKey(match.Groups["key"].Value)
                ? $"{match.Groups["prefix"].Value}\"<redacted>\""
                : match.Value);
        sanitized = SensitiveUrlUserInfo.Replace(sanitized, "$1");
        return SensitiveUrlQuery.Replace(sanitized, "$1<redacted>");
    }

    private static string SanitizeVersionSettingsText(string content)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<VersionSettingsData>(content, JsonOptions)
                ?? new VersionSettingsData();
            settings.NodeExecutablePath = null;
            return JsonSerializer.Serialize(settings, JsonOptions);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new VersionSettingsData(), JsonOptions);
        }
    }

    private static bool IsForbiddenImportPath(string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                string.Equals(segment, "sessions", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, ".env", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fileName = Path.GetFileName(relative);
        return fileName.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("password", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("token", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("apikey", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Trim().Trim('"', '\'')
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        if (normalized is "apikey" or "token" or "secret" or "password"
            or "accesstoken" or "refreshtoken" or "idtoken" or "authtoken" or "bearertoken"
            or "apisecret" or "clientsecret" or "privatekey" or "secretaccesskey"
            or "credential" or "credentials" or "authorization" or "key")
        {
            return true;
        }

        var separated = key.Trim().Trim('"', '\'')
            .Replace('-', '_')
            .Replace('.', '_')
            .ToLowerInvariant();
        return separated.EndsWith("_api_key", StringComparison.Ordinal)
            || separated.EndsWith("_access_token", StringComparison.Ordinal)
            || separated.EndsWith("_refresh_token", StringComparison.Ordinal)
            || separated.EndsWith("_client_secret", StringComparison.Ordinal)
            || separated.EndsWith("_private_key", StringComparison.Ordinal)
            || separated.EndsWith("_secret_access_key", StringComparison.Ordinal)
            || separated.EndsWith("_password", StringComparison.Ordinal)
            || separated.EndsWith("_credentials", StringComparison.Ordinal)
            || IsUppercaseCredentialVariable(key);
    }

    private static bool IsUppercaseCredentialVariable(string key)
    {
        var trimmed = key.Trim().Trim('"', '\'');
        if (trimmed.Length == 0 || trimmed.Any(character => char.IsLetter(character) && !char.IsUpper(character)))
        {
            return false;
        }

        return trimmed.EndsWith("_TOKEN", StringComparison.Ordinal)
            || trimmed.EndsWith("_SECRET", StringComparison.Ordinal)
            || trimmed.EndsWith("_PASSWORD", StringComparison.Ordinal);
    }

    private static void CopyDirectoryWithoutReparsePoints(string source, string target)
    {
        RejectReparsePoint(source, "版本源 DSH_HOME");
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            // DSh 生成的 junction（如 profiles\node_modules）指向共享运行目录，
            // 不能跟随复制；新版本首次启动时 DSh 会自行重建这些链接。
            if (IsReparsePoint(entry))
            {
                continue;
            }

            var destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectoryWithoutReparsePoints(entry, destination);
            }
            else
            {
                File.Copy(entry, destination, overwrite: false);
            }
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException($"{label}不能是符号链接或重解析点。 ");
        }
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

    private static void TryDeleteGeneratedHome(string path)
    {
        try
        {
            if (Directory.Exists(path) && !IsReparsePoint(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Keep the original operation failure; a cleanup failure is not a new user action.
        }
    }

    private static void DeleteGeneratedDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        RejectReparsePoint(path, label);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToArray())
        {
            if (IsReparsePoint(entry))
            {
                DeleteReparseLink(entry);
                continue;
            }

            if (Directory.Exists(entry))
            {
                DeleteGeneratedDirectory(entry, label);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(path, recursive: false);
    }

    private static void DeleteReparseLink(string path)
    {
        // DSh 会在 profiles\node_modules 下生成指向共享运行目录的 junction。
        // RemoveDirectory/DeleteFile 作用于重解析点时只移除链接本身，不会进入
        // 目标目录，因此共享的 DSh 安装不会被误删。
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private sealed record VersionSettings(string PackageExtension);

    private sealed record PackageManifest(
        string? Name,
        string? Description,
        string? DshVersion,
        string? DetectedVersion,
        string? PackageManager,
        DateTimeOffset? CreatedAt,
        string[] Plugins,
        string[] Skills,
        string[] AgentPresets,
        string[] Providers,
        string? Workflow);
}
