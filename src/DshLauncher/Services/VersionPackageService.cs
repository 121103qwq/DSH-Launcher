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
        @"(?i)([""']?(?:api[-_]?key|token|secret|password|access[-_]?token|refresh[-_]?token|credential(?:s)?|key)[""']?\s*:\s*)([""'][^""']*[""']|[^,\r\n}\]]+)",
        RegexOptions.CultureInvariant);

    private readonly InstanceRegistry _registry;
    private readonly LauncherPaths _paths;
    private readonly VersionSettingsService _versionSettingsService = new();

    public VersionPackageService(InstanceRegistry registry, LauncherPaths? paths = null)
    {
        _registry = registry;
        _paths = paths ?? new LauncherPaths();
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
                    }
                }

                var manifest = new Dictionary<string, object?>
                {
                    ["formatVersion"] = CurrentPackageFormatVersion,
                    ["format"] = "dsh-launcher-design",
                    ["product"] = "DSH Launcher",
                    ["name"] = instance.Name,
                    ["detectedVersion"] = instance.DetectedVersion,
                    ["packageManager"] = instance.PackageManager,
                    ["kind"] = instance.KindText,
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

        PackageManifest manifest;
        using (var stream = manifestEntry.Open())
        using (var document = JsonDocument.Parse(stream))
        {
            var root = document.RootElement;
            var formatVersion = root.TryGetProperty("formatVersion", out var format)
                && format.TryGetInt32(out var parsedFormat)
                ? parsedFormat
                : 0;
            if (formatVersion != CurrentPackageFormatVersion)
            {
                throw new InvalidDataException($"不支持的整合包格式版本：{formatVersion}。当前支持 {CurrentPackageFormatVersion}。 ");
            }

            manifest = new PackageManifest(
                ReadString(root, "name"),
                ReadString(root, "detectedVersion"),
                ReadString(root, "packageManager"));
        }

        var name = string.IsNullOrWhiteSpace(manifest.Name)
            ? $"{template.Name}（导入）"
            : manifest.Name.Trim();
        var created = _registry.Register(
            name,
            template.RootPath,
            template.Kind,
            template.DshExecutablePath,
            manifest.DetectedVersion ?? template.DetectedVersion,
            manifest.PackageManager ?? template.PackageManager);
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

    private ManagerInstance RegisterLike(ManagerInstance template, string name) =>
        _registry.Register(
            name,
            template.RootPath,
            template.Kind,
            template.DshExecutablePath,
            template.DetectedVersion,
            template.PackageManager);

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
                result["dependencies"] = dependencies.DeepClone();
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
        return SensitiveInlineValue.Replace(sanitized, "$1\"<redacted>\"");
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
                || string.Equals(segment, ".env", StringComparison.OrdinalIgnoreCase)))
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
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "apikey" or "token" or "secret" or "password"
            or "accesstoken" or "refreshtoken" or "credential" or "credentials" or "key";
    }

    private static void CopyDirectoryWithoutReparsePoints(string source, string target)
    {
        RejectReparsePoint(source, "版本源 DSH_HOME");
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectReparsePoint(entry, "版本源 DSH_HOME 中的文件");
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

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record VersionSettings(string PackageExtension);

    private sealed record PackageManifest(
        string? Name,
        string? DetectedVersion,
        string? PackageManager);
}
