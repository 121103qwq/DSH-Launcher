using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed partial class VersionPackageService
{
    private DshPackPreview PreviewDspackPackage(string path)
    {
        var profile = ReadDspackProfile(path);
        return new DshPackPreview(profile.DisplayName, profile.Description, profile.DshVersion, null,
            ReadPluginNames(profile.PackageJson), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), "profile", VersionPackageKind.Dspack, profile.Warnings);
    }

    // DSH-PackForge's ZIP profile format is distinct from both Launcher ZIPs
    // and legacy gzip ModPacks. Reuse only the validated portable-profile writer.
    private static PortableProfile ReadDspackProfile(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        ValidatePackageArchive(archive);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var entryPath = NormalizeTarPath(entry.FullName);
            if (entryPath.Split('/').Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || part != part.TrimEnd(' ', '.')))
                throw new InvalidDataException($".dspack 包含无效 Windows 路径：{entryPath}");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($".dspack 不允许链接条目：{entryPath}");
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entries.TryAdd(entryPath, entry))
                throw new InvalidDataException($".dspack 包含重复路径：{entryPath}");
        }

        long actualBytes = 0;
        byte[] ReadEntryBytes(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            var bytes = ReadLimitedBytes(stream, MaximumPackageEntryBytes, entry.FullName);
            actualBytes += bytes.LongLength;
            if (actualBytes > MaximumPackageUncompressedBytes)
                throw new InvalidDataException(".dspack 实际解压内容超过 256 MB。");
            return bytes;
        }

        JsonDocument ReadJson(string name)
        {
            if (!entries.TryGetValue(name, out var entry))
                throw new InvalidDataException($".dspack 缺少 {name}。");
            return JsonDocument.Parse(ReadEntryBytes(entry));
        }

        using var marker = ReadJson("dspack.json");
        var markerRoot = marker.RootElement;
        if (markerRoot.ValueKind != JsonValueKind.Object
            || ReadString(markerRoot, "format") != "dspack"
            || !markerRoot.TryGetProperty("version", out var containerVersion)
            || containerVersion.ValueKind != JsonValueKind.Number
            || !containerVersion.TryGetInt32(out var container) || container is not (2 or 3))
            throw new InvalidDataException("不支持的 .dspack 容器版本；当前支持 v2/v3 Profile 包。");

        using var manifest = ReadJson("manifest.json");
        var root = manifest.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("manifestVersion", out var manifestVersion)
            || manifestVersion.ValueKind != JsonValueKind.Number
            || !manifestVersion.TryGetInt32(out var version)
            || !(version == 4 || container == 3 && version == 5)
            || ReadString(root, "type") != "profile")
            throw new NotSupportedException("此 .dspack 不是支持的 Profile 包（manifest v4/v5）；dshhome 整机包暂不支持。");
        if (root.TryGetProperty("files", out var files)
            && (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 0))
            throw new NotSupportedException("此 .dspack 带有 files[] 外部资源，当前不能完整安装，已拒绝导入；请使用 PackForge 安装器。");

        var name = ReadString(root, "name");
        var packVersion = ReadString(root, "version");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(packVersion))
            throw new InvalidDataException(".dspack 缺少 name 或 version。");
        if (!root.TryGetProperty("bundles", out var bundles) || bundles.ValueKind != JsonValueKind.Array
            || bundles.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            throw new InvalidDataException(".dspack bundles 必须是非空名称的字符串数组。");
        if (!root.TryGetProperty("dependencies", out var dependencyMap) || dependencyMap.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(".dspack dependencies 必须是对象。");

        var snapshot = entries.TryGetValue("package.json", out var packageEntry)
            ? ReadSanitizedPackageJson(Encoding.UTF8.GetString(ReadEntryBytes(packageEntry))) : new JsonObject();
        var dependencies = new JsonObject();
        foreach (var dependency in dependencyMap.EnumerateObject())
        {
            if (dependency.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dependency.Value.GetString()))
                throw new InvalidDataException($".dspack 依赖版本无效：{dependency.Name}");
            var value = dependency.Value.GetString()!;
            var packageName = dependency.Name;
            if (dependency.Name.StartsWith("github:", StringComparison.Ordinal))
            {
                var repository = dependency.Name[7..];
                if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
                    || !Regex.IsMatch(value, @"\A[0-9a-fA-F]{7,40}\z"))
                    throw new InvalidDataException($".dspack GitHub 依赖必须固定到 commit：{dependency.Name}");
                // The snapshot only supplies the npm package identity. The
                // authoritative source and revision always come from manifest.
                var matches = (snapshot["dependencies"] as JsonObject ?? new JsonObject())
                    .Where(item => item.Value is JsonValue source && source.TryGetValue<string>(out var spec)
                        && MatchesDspackGitRepository(spec, repository)).Select(item => item.Key).ToArray();
                if (matches.Length != 1)
                    throw new NotSupportedException($"无法从 package.json 唯一确定 {dependency.Name} 的插件名称，已拒绝不完整导入。");
                packageName = matches[0];
                value = $"git+https://github.com/{repository}.git#{value}";
            }
            else if (!Regex.IsMatch(value, @"\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z"))
            {
                throw new InvalidDataException($".dspack npm 依赖必须使用精确版本：{dependency.Name}");
            }
            if (!Regex.IsMatch(packageName, @"\A(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+\z")
                || dependencies.ContainsKey(packageName))
                throw new InvalidDataException($".dspack 包含无效或重复的插件名称：{packageName}");
            dependencies[packageName] = value;
        }

        var displayName = ReadDspackLocalizedText(root, "displayName", name!);
        var packageJson = CreateEmptyProfilePackage(name!);
        packageJson["dependencies"] = dependencies;
        packageJson["dsh"]!["profile"]!["bundles"] = JsonNode.Parse(bundles.GetRawText());
        var extras = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var patch = ReadString(root, "patch") ?? string.Empty;
        var excluded = 0;
        foreach (var (entryName, entry) in entries)
        {
            if (!entryName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = entryName[10..];
            if (relative.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)
                || relative.Equals("pnpm-workspace.yaml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($".dspack overrides 不得覆盖机器配置：{relative}");
            if (!IsSafeModPackResource(relative) || IsForbiddenImportPath(relative))
            {
                excluded++;
                continue;
            }
            var bytes = ReadEntryBytes(entry);
            if (relative.Equals("cordis.patch.yml", StringComparison.OrdinalIgnoreCase))
                patch = Encoding.UTF8.GetString(bytes);
            else extras[relative] = SanitizePortableBytes(relative, bytes);
        }

        var warnings = new List<string>
        {
            "此 Profile 会映射到独立版本的 profiles/web；不会覆盖原版本或自动启动。",
            "使用 Launcher 当前安装位置的 DSh；不会自动安装包声明的精确 DSh 版本，请确认兼容性。",
            "Plugin 直接依赖按 manifest 固定到版本或 commit；通过官方 CLI 恢复，不导入旧 pnpm 锁文件。"
        };
        if (excluded > 0) warnings.Add($"已排除 {excluded} 个运行依赖、私密或不支持的资源文件。");
        return new PortableProfile(Slugify(name!), displayName, ReadDspackLocalizedText(root, "description", "未提供说明。"),
            packVersion!, ReadString(root, "dshVersion") ?? "未标记", ReadString(root, "profileName") ?? "web",
            packageJson, SanitizeSettingsText(patch), extras, warnings);
    }

    private static bool MatchesDspackGitRepository(string? specification, string repository)
    {
        if (specification is null) return false;
        var source = specification.Split('#')[0];
        if (source.StartsWith("github:", StringComparison.Ordinal))
            return source[7..].Equals(repository, StringComparison.OrdinalIgnoreCase);
        if (source.StartsWith("git+", StringComparison.Ordinal)) source = source[4..];
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "github.com" || uri.UserInfo.Length != 0 || uri.Query.Length != 0) return false;
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path.Equals(repository, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadDspackLocalizedText(JsonElement root, string key, string fallback)
    {
        if (!root.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? fallback;
        if (value.ValueKind != JsonValueKind.Object) return fallback;
        foreach (var language in new[] { "zh-CN", "en-US" })
            if (value.TryGetProperty(language, out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? fallback;
        return value.EnumerateObject().FirstOrDefault(item => item.Value.ValueKind == JsonValueKind.String)
            .Value is { ValueKind: JsonValueKind.String } first ? first.GetString() ?? fallback : fallback;
    }
}
