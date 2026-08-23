using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed partial class VersionPackageService
{
    private const int CurrentModPackManifestVersion = 2;
    private static readonly string[] ShareableImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".ico", ".svg"
    ];

    public static VersionPackageKind DetectPackageKind(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("找不到整合包文件。", packagePath);
        }

        Span<byte> signature = stackalloc byte[4];
        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.Read(signature);
        if (read >= 2 && signature[0] == 0x1F && signature[1] == 0x8B)
        {
            return VersionPackageKind.ModPack;
        }

        if (read >= 4
            && signature[0] == 0x50
            && signature[1] == 0x4B
            && signature[2] is 0x03 or 0x05 or 0x07
            && signature[3] is 0x04 or 0x06 or 0x08)
        {
            return VersionPackageKind.DshPack;
        }

        throw new InvalidDataException("无法识别整合包格式；当前支持 .dshpack（ZIP）和 DSH ModPack .tgz（gzip tar）。 ");
    }

    private static bool IsModPackPath(string path) =>
        path.EndsWith(ModPackPackageExtension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    private string ExportModPackPackage(
        ManagerInstance instance,
        string destination,
        VersionExportOptions options)
    {
        var profile = BuildPortableProfileFromInstance(instance, options);
        WriteModPack(destination, profile);
        return destination;
    }

    private DshPackPreview PreviewModPackPackage(string packagePath)
    {
        var profile = ReadModPack(packagePath);
        return new DshPackPreview(
            profile.DisplayName,
            profile.Description,
            profile.DshVersion,
            null,
            ReadPluginNames(profile.PackageJson),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "profile",
            VersionPackageKind.ModPack,
            profile.Warnings);
    }

    private ManagerInstance ImportModPackPackage(string packagePath, ManagerInstance template)
    {
        var profile = ReadModPack(packagePath);
        var runtime = ResolveImportRuntime(template);
        var runtimeVersion = DshRuntimeDetector.TryReadPackageVersion(runtime.RootPath)
            ?? template.DetectedVersion;
        var created = _registry.Register(
            profile.DisplayName,
            runtime.RootPath,
            runtime.Kind,
            runtime.DshExecutablePath,
            runtimeVersion,
            runtime.PackageManager,
            dshLaunchSpec: runtime.LaunchSpec);
        try
        {
            WritePortableProfileToDshHome(profile, created.DshHome);
            return created;
        }
        catch
        {
            _registry.Unregister(created.Id);
            TryDeleteGeneratedHome(created.DshHome);
            throw;
        }
    }

    private VersionPackageConversionResult ConvertModPackToDshPack(
        string sourcePath,
        string destination)
    {
        var profile = ReadModPack(sourcePath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("转换目标没有父目录。 ");
        Directory.CreateDirectory(directory);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var settings = new VersionSettingsData();
                AddTextEntry(
                    archive,
                    "dsh-home/.dsh-launcher/version-settings.json",
                    JsonSerializer.Serialize(settings, JsonOptions));
                AddTextEntry(
                    archive,
                    "dsh-home/profiles/web/package.json",
                    profile.PackageJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                AddTextEntry(archive, "dsh-home/profiles/web/cordis.patch.yml", profile.Patch);
                AddTextEntry(
                    archive,
                    "dsh-home/profiles/web/manifest.json",
                    JsonSerializer.Serialize(CreateModPackManifest(profile), JsonOptions));

                var contents = new List<string>
                {
                    "dsh-home/.dsh-launcher/version-settings.json",
                    "dsh-home/profiles/web/package.json",
                    "dsh-home/profiles/web/cordis.patch.yml",
                    "dsh-home/profiles/web/manifest.json"
                };
                foreach (var file in profile.ExtraFiles)
                {
                    var archivePath = $"dsh-home/profiles/web/{file.Key}";
                    AddBinaryEntry(archive, archivePath, file.Value);
                    contents.Add(archivePath);
                }

                var manifest = new Dictionary<string, object?>
                {
                    ["formatVersion"] = CurrentPackageFormatVersion,
                    ["format"] = "dsh-launcher-design",
                    ["product"] = "DSH Launcher",
                    ["name"] = profile.DisplayName,
                    ["description"] = profile.Description,
                    ["createdAt"] = DateTimeOffset.UtcNow,
                    ["dshVersion"] = profile.DshVersion,
                    ["detectedVersion"] = profile.DshVersion,
                    ["packageManager"] = "npm",
                    ["kind"] = "installed",
                    ["plugins"] = ReadPluginNames(profile.PackageJson),
                    ["skills"] = Array.Empty<string>(),
                    ["agentPresets"] = Array.Empty<string>(),
                    ["providers"] = Array.Empty<string>(),
                    ["workflow"] = "profile",
                    ["contents"] = contents,
                    ["sourceFormat"] = "dsh-packforge-modpack-v2",
                    ["privacy"] = new Dictionary<string, bool>
                    {
                        ["apiKeys"] = false,
                        ["sessions"] = false
                    }
                };
                AddTextEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
            }

            File.Move(temporary, destination, overwrite: true);
            return new VersionPackageConversionResult(
                destination,
                VersionPackageKind.ModPack,
                VersionPackageKind.DshPack,
                profile.Warnings);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private VersionPackageConversionResult ConvertDshPackToModPack(
        string sourcePath,
        string destination)
    {
        using var archive = ZipFile.OpenRead(sourcePath);
        ValidatePackageArchive(archive);
        var manifest = ReadPackageManifest(archive);
        var packageEntry = FindZipEntry(archive, "dsh-home/profiles/web/package.json")
            ?? throw new InvalidDataException(".dshpack 没有包含 web Profile 的 package.json，无法转换为 ModPack。 ");
        var packageJson = ReadSanitizedPackageJson(ReadZipText(packageEntry));
        var patchEntry = FindZipEntry(archive, "dsh-home/profiles/web/cordis.patch.yml");
        var patch = patchEntry is null ? string.Empty : SanitizeSettingsText(ReadZipText(patchEntry));
        var extraFiles = ReadDshPackProfileExtras(archive);
        var warnings = new List<string>
        {
            "ModPack 只表达一个 DSh Profile；Launcher 版本设置和会话同步策略不会写入 .tgz。"
        };
        if (manifest.Skills.Length > 0 || manifest.AgentPresets.Length > 0 || manifest.Providers.Length > 0)
        {
            warnings.Add("Skill、Agent Preset 和 Provider 属于 DSH_HOME 级数据，转换到标准 ModPack 时已省略。 ");
        }

        var profile = new PortableProfile(
            Slugify(manifest.Name ?? "dsh-profile"),
            string.IsNullOrWhiteSpace(manifest.Name) ? "DSH Profile" : manifest.Name.Trim(),
            manifest.Description ?? "由 DSH Launcher 转换的 DSh Profile。",
            "1.0.0",
            manifest.DshVersion ?? manifest.DetectedVersion ?? ">=0.1.0",
            "web",
            packageJson,
            patch,
            extraFiles,
            warnings);
        WriteModPack(destination, profile);
        return new VersionPackageConversionResult(
            destination,
            VersionPackageKind.DshPack,
            VersionPackageKind.ModPack,
            warnings);
    }

    private static PortableProfile BuildPortableProfileFromInstance(
        ManagerInstance instance,
        VersionExportOptions options)
    {
        var profileRoot = Path.Combine(instance.DshHome, "profiles", "web");
        var packagePath = Path.Combine(profileRoot, "package.json");
        var packageJson = options.IncludePluginConfiguration
            ? CreatePluginConfiguration(packagePath) ?? CreateEmptyProfilePackage(instance.Name)
            : CreateEmptyProfilePackage(instance.Name);
        EnsureProfileIdentity(packageJson, instance.Name);
        var patchPath = Path.Combine(profileRoot, "cordis.patch.yml");
        var patch = options.IncludePluginConfiguration && File.Exists(patchPath)
            ? SanitizeSettingsText(File.ReadAllText(patchPath, Encoding.UTF8))
            : string.Empty;
        var extras = options.IncludePluginConfiguration
            ? ReadSafeProfileFiles(profileRoot)
            : new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        extras.Remove("package.json");
        extras.Remove("cordis.patch.yml");
        extras.Remove("manifest.json");
        return new PortableProfile(
            Slugify(instance.Name),
            instance.Name,
            $"DSH Launcher 版本“{instance.Name}”的 DSh Profile。",
            "1.0.0",
            instance.DetectedVersion ?? ">=0.1.0",
            "web",
            packageJson,
            patch,
            extras,
            Array.Empty<string>());
    }

    private static PortableProfile ReadModPack(string packagePath)
    {
        var entries = ReadTarGzipEntries(packagePath);
        if (!entries.TryGetValue("manifest.json", out var manifestBytes))
        {
            throw new InvalidDataException("ModPack 缺少根目录 manifest.json。 ");
        }

        if (!entries.TryGetValue("package.json", out var packageBytes))
        {
            throw new InvalidDataException("ModPack 缺少根目录 package.json，不是有效的 DSh Profile 包。 ");
        }

        var manifest = ReadModPackManifest(Encoding.UTF8.GetString(manifestBytes));
        var packageJson = ReadSanitizedPackageJson(Encoding.UTF8.GetString(packageBytes));
        packageJson["dependencies"] = manifest.Dependencies.DeepClone();
        packageJson["dsh"] = new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["bundles"] = new JsonArray(
                    manifest.Bundles
                        .Select(static value => (JsonNode?)JsonValue.Create(value))
                        .ToArray())
            }
        };
        EnsureProfileIdentity(packageJson, manifest.DisplayName);

        var warnings = new List<string>();
        if (!string.Equals(manifest.ProfileName, "web", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Launcher 当前运行 Web Profile；原 Profile“{manifest.ProfileName}”会映射为 web。 ");
        }

        var extras = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var excluded = 0;
        foreach (var entry in entries)
        {
            if (entry.Key is "manifest.json" or "package.json" or "cordis.patch.yml")
            {
                continue;
            }

            if (!IsSafeModPackResource(entry.Key) || IsForbiddenImportPath(entry.Key))
            {
                excluded++;
                continue;
            }

            extras[entry.Key] = SanitizePortableBytes(entry.Key, entry.Value);
        }

        if (excluded > 0)
        {
            warnings.Add($"为避免导入运行依赖或私密内容，已忽略 {excluded} 个非配置文件。 ");
        }

        var patch = manifest.Patch;
        if (string.IsNullOrWhiteSpace(patch)
            && entries.TryGetValue("cordis.patch.yml", out var patchBytes))
        {
            patch = Encoding.UTF8.GetString(patchBytes);
        }

        return new PortableProfile(
            Slugify(manifest.Name),
            manifest.DisplayName,
            manifest.Description,
            manifest.Version,
            manifest.DshVersion,
            manifest.ProfileName,
            packageJson,
            SanitizeSettingsText(patch),
            extras,
            warnings);
    }

    private static Dictionary<string, byte[]> ReadTarGzipEntries(string packagePath)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzip, leaveOpen: false);
        long totalLength = 0;
        var count = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (++count > MaximumPackageEntries)
            {
                throw new InvalidDataException($"ModPack 条目过多（最多 {MaximumPackageEntries} 个）。 ");
            }

            var path = NormalizeTarPath(entry.Name);
            if (entry.EntryType is TarEntryType.Directory
                or TarEntryType.GlobalExtendedAttributes
                or TarEntryType.ExtendedAttributes)
            {
                continue;
            }

            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
            {
                throw new InvalidDataException($"ModPack 包含不允许的链接或特殊条目：{path}。 ");
            }

            if (entry.Length > MaximumPackageEntryBytes)
            {
                throw new InvalidDataException($"ModPack 条目过大：{path}。 ");
            }

            if (!entries.TryAdd(path, ReadLimitedBytes(entry.DataStream, MaximumPackageEntryBytes, path)))
            {
                throw new InvalidDataException($"ModPack 包含重复路径：{path}。 ");
            }

            checked
            {
                totalLength += entries[path].LongLength;
            }

            if (totalLength > MaximumPackageUncompressedBytes)
            {
                throw new InvalidDataException("ModPack 解压后的总大小超过 256 MB。 ");
            }
        }

        return entries;
    }

    private static byte[] ReadLimitedBytes(Stream? stream, long maximumBytes, string path)
    {
        if (stream is null)
        {
            return Array.Empty<byte>();
        }

        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"ModPack 条目过大：{path}。 ");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string NormalizeTarPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || (segments[0].Length >= 2 && segments[0][1] == ':')
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"ModPack 包含越界路径：{path}。 ");
        }

        return string.Join('/', segments);
    }

    private static ModPackManifest ReadModPackManifest(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var version = root.TryGetProperty("manifestVersion", out var manifestVersion)
            && manifestVersion.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        if (version != CurrentModPackManifestVersion)
        {
            throw new InvalidDataException($"不支持的 ModPack manifest 版本：{version}；当前支持 v{CurrentModPackManifestVersion}。 ");
        }

        var name = ReadString(root, "name")?.Trim();
        var packageVersion = ReadString(root, "version")?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(packageVersion))
        {
            throw new InvalidDataException("ModPack manifest.name 或 manifest.version 缺失。 ");
        }

        var dependencies = new JsonObject();
        if (root.TryGetProperty("dependencies", out var dependencyElement))
        {
            if (dependencyElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("ModPack manifest.dependencies 必须是对象。 ");
            }

            foreach (var dependency in dependencyElement.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"ModPack 依赖 {dependency.Name} 的版本必须是字符串。 ");
                }

                dependencies[dependency.Name] = SanitizePluginSpecification(dependency.Value.GetString() ?? string.Empty);
            }
        }

        var bundles = ReadStringArray(root, "bundles")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (root.TryGetProperty("bundles", out var bundleElement)
            && bundleElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("ModPack manifest.bundles 必须是字符串数组。 ");
        }

        return new ModPackManifest(
            name,
            ReadString(root, "displayName")?.Trim() is { Length: > 0 } displayName ? displayName : name,
            packageVersion,
            ReadString(root, "description") ?? string.Empty,
            ReadString(root, "dshVersion") ?? ">=0.1.0",
            ReadString(root, "profileName") ?? "web",
            bundles,
            dependencies,
            ReadString(root, "patch") ?? string.Empty);
    }

    private static JsonObject ReadSanitizedPackageJson(string content)
    {
        JsonObject? source;
        try
        {
            source = JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            source = null;
        }

        var result = new JsonObject();
        if (source is not null)
        {
            foreach (var property in new[] { "name", "version", "description", "private", "license", "author" })
            {
                if (source[property] is JsonNode value)
                {
                    result[property] = value.DeepClone();
                }
            }
        }

        var configuration = source is null ? null : CreatePluginConfigurationFromJson(source);
        if (configuration?["dependencies"] is JsonNode dependencies)
        {
            result["dependencies"] = dependencies.DeepClone();
        }

        if (configuration?["dsh"] is JsonNode dsh)
        {
            result["dsh"] = dsh.DeepClone();
        }

        return result;
    }

    private static JsonObject? CreatePluginConfigurationFromJson(JsonObject root)
    {
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
            }

            result["dependencies"] = sanitizedDependencies;
        }

        if (root["dsh"] is JsonObject dsh
            && dsh["profile"] is JsonObject profile
            && profile["bundles"] is JsonArray bundles)
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

    private static void WritePortableProfileToDshHome(PortableProfile profile, string dshHome)
    {
        var profileRoot = Path.Combine(dshHome, "profiles", "web");
        Directory.CreateDirectory(profileRoot);
        File.WriteAllText(
            Path.Combine(profileRoot, "package.json"),
            profile.PackageJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(profileRoot, "cordis.patch.yml"),
            profile.Patch,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(profileRoot, "manifest.json"),
            JsonSerializer.Serialize(CreateModPackManifest(profile), JsonOptions),
            new UTF8Encoding(false));
        foreach (var file in profile.ExtraFiles)
        {
            var destination = ResolveContainedPath(profileRoot, file.Key, "ModPack Profile");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, file.Value);
        }
    }

    private static void WriteModPack(string destination, PortableProfile profile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidOperationException("ModPack 目标没有父目录。 ");
        Directory.CreateDirectory(directory);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: false))
            using (var writer = new TarWriter(gzip, leaveOpen: false))
            {
                WriteTarText(
                    writer,
                    "manifest.json",
                    JsonSerializer.Serialize(CreateModPackManifest(profile), JsonOptions));
                WriteTarText(
                    writer,
                    "package.json",
                    profile.PackageJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                WriteTarText(writer, "cordis.patch.yml", profile.Patch);
                foreach (var extra in profile.ExtraFiles)
                {
                    WriteTarBytes(writer, extra.Key, extra.Value);
                }
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteTarText(TarWriter writer, string path, string content) =>
        WriteTarBytes(writer, path, new UTF8Encoding(false).GetBytes(content));

    private static void WriteTarBytes(TarWriter writer, string path, byte[] content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, NormalizeTarPath(path))
        {
            DataStream = new MemoryStream(content, writable: false),
            ModificationTime = DateTimeOffset.UtcNow
        };
        writer.WriteEntry(entry);
        entry.DataStream.Dispose();
    }

    private static Dictionary<string, byte[]> ReadSafeProfileFiles(string profileRoot)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(profileRoot))
        {
            return result;
        }

        RejectReparsePoint(profileRoot, "Profile 目录");
        foreach (var file in EnumerateSafeFiles(profileRoot))
        {
            var relative = Path.GetRelativePath(profileRoot, file).Replace('\\', '/');
            if (IsSafeModPackResource(relative) && !IsForbiddenImportPath(relative))
            {
                result[relative] = SanitizePortableBytes(relative, File.ReadAllBytes(file));
            }
        }

        return result;
    }

    private static Dictionary<string, byte[]> ReadDshPackProfileExtras(ZipArchive archive)
    {
        const string prefix = "dsh-home/profiles/web/";
        var extras = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = path[prefix.Length..];
            if (relative is "package.json" or "cordis.patch.yml" or "manifest.json"
                || !IsSafeModPackResource(relative)
                || IsForbiddenImportPath(relative))
            {
                continue;
            }

            using var stream = entry.Open();
            extras[relative] = SanitizePortableBytes(
                relative,
                ReadLimitedBytes(stream, MaximumPackageEntryBytes, relative));
        }

        return extras;
    }

    private static Dictionary<string, object?> CreateModPackManifest(PortableProfile profile) => new()
    {
        ["manifestVersion"] = CurrentModPackManifestVersion,
        ["name"] = profile.Name,
        ["displayName"] = profile.DisplayName,
        ["version"] = profile.Version,
        ["description"] = profile.Description,
        ["author"] = string.Empty,
        ["icon"] = FindPortableIcon(profile.ExtraFiles.Keys),
        ["dshVersion"] = profile.DshVersion,
        ["profileName"] = profile.ProfileName,
        ["bundles"] = ReadPluginBundles(profile.PackageJson),
        ["dependencies"] = profile.PackageJson["dependencies"] as JsonObject ?? new JsonObject(),
        ["patch"] = profile.Patch
    };

    private static byte[] SanitizePortableBytes(string path, byte[] content)
    {
        if (!IsShareableTextFile(path)
            && !Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        return new UTF8Encoding(false).GetBytes(SanitizeSettingsText(Encoding.UTF8.GetString(content)));
    }

    private static bool IsSafeModPackResource(string path)
    {
        var normalized = NormalizeTarPath(path);
        var segments = normalized.Split('/');
        if (segments.Any(segment => string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var extension = Path.GetExtension(normalized);
        return IsShareableTextFile(normalized)
            || ShareableImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveContainedPath(string root, string relative, string label)
    {
        var normalizedRelative = NormalizeTarPath(relative).Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label} 包含越界路径：{relative}。 ");
        }

        return destination;
    }

    private static JsonObject CreateEmptyProfilePackage(string name) => new()
    {
        ["name"] = $"dsh-profile-{Slugify(name)}",
        ["version"] = "1.0.0",
        ["private"] = true,
        ["dependencies"] = new JsonObject(),
        ["dsh"] = new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["bundles"] = new JsonArray()
            }
        }
    };

    private static void EnsureProfileIdentity(JsonObject packageJson, string name)
    {
        packageJson["name"] ??= $"dsh-profile-{Slugify(name)}";
        packageJson["version"] ??= "1.0.0";
        packageJson["private"] ??= true;
        packageJson["dependencies"] ??= new JsonObject();
        packageJson["dsh"] ??= new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["bundles"] = new JsonArray()
            }
        };
    }

    private static string[] ReadPluginBundles(JsonObject packageJson)
    {
        if (packageJson["dsh"] is not JsonObject dsh
            || dsh["profile"] is not JsonObject profile
            || profile["bundles"] is not JsonArray bundles)
        {
            return Array.Empty<string>();
        }

        return bundles
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        var previousDash = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                previousDash = false;
            }
            else if (!previousDash && builder.Length > 0)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        var result = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "dsh-profile" : result;
    }

    private static string? FindPortableIcon(IEnumerable<string> paths) =>
        paths.FirstOrDefault(path =>
            path.StartsWith("icon/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("icons/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(path).Equals("icon", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(path).Equals("logo", StringComparison.OrdinalIgnoreCase));

    private static ZipArchiveEntry? FindZipEntry(ZipArchive archive, string path) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static string ReadZipText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void AddBinaryEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private sealed record PortableProfile(
        string Name,
        string DisplayName,
        string Description,
        string Version,
        string DshVersion,
        string ProfileName,
        JsonObject PackageJson,
        string Patch,
        Dictionary<string, byte[]> ExtraFiles,
        IReadOnlyList<string> Warnings);

    private sealed record ModPackManifest(
        string Name,
        string DisplayName,
        string Version,
        string Description,
        string DshVersion,
        string ProfileName,
        string[] Bundles,
        JsonObject Dependencies,
        string Patch);
}
