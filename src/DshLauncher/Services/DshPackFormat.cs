using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

/// <summary>
/// 整合包容器种类（规范见 repo-review-dsh-plugins/docs/PACK_MANIFEST.md；借鉴路线 #24，work-log/70）。
/// </summary>
public enum PackContainerKind
{
    Unknown,
    DspackV2,
    DspackV3,
    LegacyTgz
}

/// <summary>整合包 manifest 版本；导入兼容 2–5（导出用 4）。</summary>
public enum PackManifestVersion
{
    Unknown = 0,
    V2 = 2,
    V3 = 3,
    V4 = 4,
    V5 = 5
}

/// <summary>manifest 形态：v4 起有 type，v5 增加 dshhome（整个 $DSH_HOME 快照）。</summary>
public enum PackManifestType
{
    Profile,
    DshHome,
    Collection
}

/// <summary>重内容下载条目（v4+ 的 files[]）：模型、数据、非 npm/git 二进制。</summary>
public sealed record PackFileEntry(string Path, string Sha256, long Size, IReadOnlyList<string> Urls);

/// <summary>v5 dshhome 的重技能条目（按需下载）。</summary>
public sealed record PackSkillEntry(string Path, string? Sha256, long? Size, IReadOnlyList<string> Urls);

/// <summary>v5 dshhome 中单个 profile 的描述。</summary>
public sealed record PackHomeProfile(
    string Name,
    IReadOnlyList<string> Bundles,
    IReadOnlyDictionary<string, string> Dependencies,
    string? Patch);

/// <summary>
/// 解析后的整合包 manifest（v2–v5 归一化视图）。只做只读解析与校验，
/// 不触碰文件系统、不联网、不涉及实例数据。
/// </summary>
public sealed class PackManifest
{
    public PackManifestVersion Version { get; init; } = PackManifestVersion.Unknown;

    public PackManifestType Type { get; init; } = PackManifestType.Profile;

    public string Name { get; init; } = string.Empty;

    public string PackVersion { get; init; } = string.Empty;

    public string? Author { get; init; }

    public string? Icon { get; init; }

    public IReadOnlyDictionary<string, string> DisplayNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Descriptions { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>归一化后的精确 DSH 版本（v2 的范围取其下限）。</summary>
    public string? DshVersion { get; init; }

    /// <summary>manifest 里写的原始值（v2 可能是范围）。</summary>
    public string? DshVersionRaw { get; init; }

    /// <summary>导入时创建的 profile 名；缺省 pack。</summary>
    public string ProfileName { get; init; } = PackFormat.DefaultProfileName;

    public IReadOnlyList<string> Bundles { get; init; } = Array.Empty<string>();

    /// <summary>依赖坐标 → 固定版本（v3/v4 语义；v2 为原始 pnpm spec）。</summary>
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<PackFileEntry> Files { get; init; } = Array.Empty<PackFileEntry>();

    /// <summary>v3 的内联 patch（v4 起改为 overrides/cordis.patch.yml）。</summary>
    public string? Patch { get; init; }

    /// <summary>v5 dshhome：实例默认 profile（必需）。</summary>
    public string? DefaultProfile { get; init; }

    /// <summary>v5 dshhome：随包分发的 profile 列表（不得含 web / headless）。</summary>
    public IReadOnlyList<PackHomeProfile> HomeProfiles { get; init; } = Array.Empty<PackHomeProfile>();

    /// <summary>v5 dshhome：重技能的按需下载索引。</summary>
    public IReadOnlyList<PackSkillEntry> Skills { get; init; } = Array.Empty<PackSkillEntry>();

    /// <summary>v5 dshhome：指令文件名，缺省 AGENTS.md。</summary>
    public string? Instructions { get; init; }

    /// <summary>manifestVersion 5 必须搭配 .dspack v3 容器（配对校验）。</summary>
    public bool RequiresDspackV3 => Version == PackManifestVersion.V5;

    /// <summary>兼容性提示（如 v2 原样透传），导入界面可原样展示。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>按界面语言取显示名（缺省 zh-CN）；都没有时回退 name。</summary>
    public string ResolveDisplayName(string? preferred = null)
    {
        var value = PackFormat.ResolveLocalized(DisplayNames, preferred);
        return string.IsNullOrWhiteSpace(value) ? Name : value;
    }

    /// <summary>按界面语言取描述；没有时返回空串。</summary>
    public string ResolveDescription(string? preferred = null) =>
        PackFormat.ResolveLocalized(Descriptions, preferred) ?? string.Empty;
}

/// <summary>
/// 整合包格式的纯逻辑层：容器/manifest 版本判定、v2–v5 字段解析与校验、依赖坐标转换。
/// 规范：repo-review-dsh-plugins/docs/PACK_MANIFEST.md；语义歧义以对方实现 modpack.rs 为准。
/// </summary>
public static class PackFormat
{
    /// <summary>导入时 profile 名的缺省值（保持 web profile 干净）。</summary>
    public const string DefaultProfileName = "pack";

    public const int MinimumSupportedManifestVersion = 2;

    public const int MaximumSupportedManifestVersion = 5;

    /// <summary>容器标记文件与 manifest 文件名（.dspack 与旧 .tgz 通用 manifest 名）。</summary>
    public const string ContainerMarkerFileName = "dspack.json";

    public const string ManifestFileName = "manifest.json";

    public const string DspackFormatName = "dspack";

    public const string OverridesDirectoryName = "overrides";

    public const string HomeDirectoryName = "home";

    public const string DefaultInstructionsFileName = "AGENTS.md";

    /// <summary>v5 dshhome 里 profile 名不得占用的基线名。</summary>
    public static readonly IReadOnlyList<string> ReservedProfileNames = new[] { "web", "headless" };

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>按文件头判定归档类型（不解析内容）。</summary>
    public static PackContainerKind DetectFromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
        {
            // ZIP：具体是 v2 还是 v3 由根 dspack.json 决定，这里先判"是 .dspack 容器"。
            return PackContainerKind.Unknown;
        }

        if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
        {
            return PackContainerKind.LegacyTgz;
        }

        return PackContainerKind.Unknown;
    }

    /// <summary>是否是标准 ZIP（.dspack 的文件头）。</summary>
    public static bool HasZipHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;

    /// <summary>是否是 gzip（旧 .tgz 的文件头）。</summary>
    public static bool HasGzipHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B;

    /// <summary>
    /// 解析 .dspack 根部的 dspack.json 标记；版本必须在 2–3，format 必须是 dspack。
    /// </summary>
    public static bool TryParseContainerMarker(string json, out PackContainerKind kind, out string? error)
    {
        kind = PackContainerKind.Unknown;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "dspack.json 不是对象，无法识别为整合包容器。";
                return false;
            }

            var format = ReadString(root, "format");
            if (!string.Equals(format, DspackFormatName, StringComparison.OrdinalIgnoreCase))
            {
                error = $"dspack.json 的 format 不是 “{DspackFormatName}”，拒绝加载。";
                return false;
            }

            var version = ReadInt(root, "version");
            kind = version switch
            {
                2 => PackContainerKind.DspackV2,
                3 => PackContainerKind.DspackV3,
                _ => PackContainerKind.Unknown
            };

            if (kind == PackContainerKind.Unknown)
            {
                error = $"不支持的 dspack 容器版本：{version.ToString(CultureInfo.InvariantCulture)}（支持 2-3）。";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"dspack.json 解析失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>容器与 manifest 的配对校验：manifestVersion 5 必须使用 .dspack v3。</summary>
    public static bool ValidatePairing(PackManifest manifest, PackContainerKind container, out string? error)
    {
        error = null;
        if (manifest.Version == PackManifestVersion.Unknown)
        {
            error = "manifestVersion 无法识别。";
            return false;
        }

        if (manifest.RequiresDspackV3 && container != PackContainerKind.DspackV3)
        {
            error = "manifestVersion 5 必须使用 .dspack v3 容器（配对校验失败）。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 解析 manifest.json（v2–v5）。失败时给出可直接展示给用户的原因。
    /// </summary>
    public static bool TryParseManifest(string json, out PackManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"manifest.json 解析失败：{ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "manifest.json 不是对象。";
                return false;
            }

            var versionNumber = ReadInt(root, "manifestVersion");
            if (versionNumber is < MinimumSupportedManifestVersion or > MaximumSupportedManifestVersion)
            {
                error = $"不支持的 manifestVersion {versionNumber.ToString(CultureInfo.InvariantCulture)}（支持 {MinimumSupportedManifestVersion}-{MaximumSupportedManifestVersion}）。";
                return false;
            }

            var version = (PackManifestVersion)versionNumber;
            var typeName = ReadString(root, "type");
            var type = typeName?.Trim().ToLowerInvariant() switch
            {
                "dshhome" => PackManifestType.DshHome,
                "collection" => PackManifestType.Collection,
                _ => PackManifestType.Profile // 缺省按 profile 兜底（规范）
            };

            if (type == PackManifestType.Collection)
            {
                error = "type=collection 暂未支持。";
                return false;
            }

            if (type == PackManifestType.DshHome && version != PackManifestVersion.V5)
            {
                error = $"dshhome 形态需要 manifestVersion 5（实际为 {versionNumber.ToString(CultureInfo.InvariantCulture)}）。";
                return false;
            }

            var notes = new List<string>();
            var name = ReadString(root, "name")?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                error = "manifest.json 缺少 name。";
                return false;
            }

            var packVersion = ReadString(root, "version")?.Trim() ?? string.Empty;

            if (!ReadLocalized(root, "displayName", out var displayNames, out var displayError))
            {
                error = displayError;
                return false;
            }

            if (!ReadLocalized(root, "description", out var descriptions, out var descriptionError))
            {
                error = descriptionError;
                return false;
            }

            var rawDshVersion = ReadString(root, "dshVersion")?.Trim();
            var dshVersion = NormalizeDshVersion(rawDshVersion);
            if (version == PackManifestVersion.V2 && rawDshVersion is not null && dshVersion != rawDshVersion)
            {
                notes.Add($"v2 的 dshVersion 是版本范围（{rawDshVersion}），按规范取其下限 {dshVersion}。");
            }

            if (!TryReadStringArray(root, "bundles", out var bundles, out var bundlesError))
            {
                error = bundlesError;
                return false;
            }

            if (!TryReadDependencies(root, version, out var dependencies, out var dependenciesError))
            {
                error = dependenciesError;
                return false;
            }

            if (!TryReadFiles(root, out var files, out var filesError))
            {
                error = filesError;
                return false;
            }

            if (!TryReadStringArray(root, "presets", out _, out _))
            {
                // presets 目前只用于展示索引，解析失败不影响导入判定。
                notes.Add("presets 字段格式非字符串数组，已忽略。");
            }

            var homeProfiles = Array.Empty<PackHomeProfile>();
            var skills = Array.Empty<PackSkillEntry>();
            string? defaultProfile = null;
            string? instructions = null;

            if (type == PackManifestType.DshHome)
            {
                if (!TryReadHomePayload(root, out homeProfiles, out skills, out defaultProfile, out instructions, out var homeError))
                {
                    error = homeError;
                    return false;
                }
            }

            if (version == PackManifestVersion.V2)
            {
                notes.Add("v2 的 dependencies 为 pnpm 原始 spec，导入时原样透传。");
            }

            manifest = new PackManifest
            {
                Version = version,
                Type = type,
                Name = name,
                PackVersion = packVersion,
                Author = ReadString(root, "author"),
                Icon = ReadString(root, "icon"),
                DisplayNames = displayNames,
                Descriptions = descriptions,
                DshVersion = dshVersion,
                DshVersionRaw = rawDshVersion,
                ProfileName = ResolveProfileName(ReadString(root, "profileName")),
                Bundles = bundles,
                Dependencies = dependencies,
                Files = files,
                Patch = ReadString(root, "patch"),
                DefaultProfile = defaultProfile,
                HomeProfiles = homeProfiles,
                Skills = skills,
                Instructions = instructions,
                Notes = notes
            };

            return true;
        }
    }

    /// <summary>profile 名归一化：空/非法回退 pack；不允许路径分隔符与保留名。</summary>
    public static string ResolveProfileName(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !IsSimpleName(trimmed))
        {
            return DefaultProfileName;
        }

        return trimmed;
    }

    /// <summary>是否是安全的简单名字（无路径分隔、无 .. 、非点开头）。</summary>
    public static bool IsSimpleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains('/') || value.Contains('\\') || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return !value.StartsWith('.');
    }

    /// <summary>
    /// 归一化 DSH 版本：范围（如 &gt;=0.1.0、^0.1.0）取其下限；已是精确版本时原样返回。
    /// </summary>
    public static string? NormalizeDshVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var match = Regex.Match(value, @"\d+\.\d+(\.\d+)?(-[0-9A-Za-z.\-]+)?", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : value;
    }

    /// <summary>按界面语言选择条目：preferred（缺省 zh-CN）→ zh → en-US → en → 第一个。</summary>
    public static string? ResolveLocalized(IReadOnlyDictionary<string, string> map, string? preferred = null)
    {
        if (map.Count == 0)
        {
            return null;
        }

        var order = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            order.Add(preferred.Trim());
        }

        order.Add("zh-CN");
        order.Add("zh");
        order.Add("en-US");
        order.Add("en");

        foreach (var key in order)
        {
            foreach (var pair in map)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value;
                }
            }
        }

        return map.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// 依赖坐标 → package.json 条目（规范三条规则）：
    /// npm <c>"pkg":"1.2.3"</c>；git <c>"github:owner/repo":"&lt;sha&gt;"</c> → <c>"repo":"github:owner/repo#&lt;sha&gt;"</c>；
    /// 子目录 <c>"github:owner/repo#path:/pkg"</c> → <c>"pkg":"github:owner/repo#&lt;sha&gt;&amp;path:pkg"</c>。
    /// </summary>
    public static bool TryConvertToPackageJsonEntry(string coordinate, string pinned, out string name, out string spec)
    {
        name = string.Empty;
        spec = string.Empty;
        if (string.IsNullOrWhiteSpace(coordinate) || string.IsNullOrWhiteSpace(pinned))
        {
            return false;
        }

        var key = coordinate.Trim();
        var version = pinned.Trim();
        if (!key.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            name = key;
            spec = version;
            return true;
        }

        var body = key["github:".Length..];
        var subPath = string.Empty;
        var marker = body.IndexOf("#path:", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            subPath = body[(marker + "#path:".Length)..].Trim().Trim('/');
            body = body[..marker];
        }

        var repo = body.Trim().Trim('/');
        if (repo.Length == 0)
        {
            return false;
        }

        if (subPath.Length == 0)
        {
            name = repo.Split('/').Last();
            spec = $"github:{repo}#{version}";
            return true;
        }

        name = subPath.Split('/').Last();
        spec = $"github:{repo}#{version}&path:{subPath}";
        return true;
    }

    /// <summary>
    /// package.json 条目 → 依赖坐标（导出的反方向转换）。
    /// </summary>
    public static bool TryParsePackageJsonEntry(string name, string spec, out string coordinate, out string pinned)
    {
        coordinate = string.Empty;
        pinned = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var cleanName = name.Trim();
        var cleanSpec = spec.Trim();
        if (!cleanSpec.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            coordinate = cleanName;
            pinned = cleanSpec;
            return true;
        }

        var body = cleanSpec["github:".Length..];
        var hash = body.IndexOf('#');
        if (hash < 0)
        {
            return false;
        }

        var repo = body[..hash].Trim().Trim('/');
        var rest = body[(hash + 1)..];
        var subPath = string.Empty;
        var ampersand = rest.IndexOf('&');
        var version = ampersand < 0 ? rest : rest[..ampersand];
        if (ampersand >= 0)
        {
            foreach (var part in rest[(ampersand + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                {
                    subPath = part["path:".Length..].Trim().Trim('/');
                }
            }
        }

        if (repo.Length == 0 || version.Length == 0)
        {
            return false;
        }

        coordinate = subPath.Length == 0 ? $"github:{repo}" : $"github:{repo}#path:/{subPath}";
        pinned = version;
        return true;
    }

    /// <summary>相对路径安全校验（zip-slip 防护）：非空、相对、无 ../、无反斜杠、无盘符。</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();
        if (value.StartsWith('/') || value.StartsWith('\\') || value.Contains('\\') || value.Contains(':'))
        {
            return false;
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>sha256 是否为 64 位小写十六进制（规范要求）。</summary>
    public static bool IsValidSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Sha256Pattern.IsMatch(value.Trim());

    private static bool TryReadHomePayload(
        JsonElement root,
        out PackHomeProfile[] profiles,
        out PackSkillEntry[] skills,
        out string? defaultProfile,
        out string? instructions,
        out string? error)
    {
        profiles = Array.Empty<PackHomeProfile>();
        skills = Array.Empty<PackSkillEntry>();
        defaultProfile = null;
        instructions = null;
        error = null;

        defaultProfile = ReadString(root, "defaultProfile")?.Trim();
        if (string.IsNullOrEmpty(defaultProfile))
        {
            error = "dshhome 形态缺少必需字段 defaultProfile。";
            return false;
        }

        if (root.TryGetProperty("profiles", out var profilesElement) && profilesElement.ValueKind == JsonValueKind.Object)
        {
            var list = new List<PackHomeProfile>();
            foreach (var property in profilesElement.EnumerateObject())
            {
                var profileName = property.Name.Trim();
                if (ReservedProfileNames.Contains(profileName, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"dshhome 的 profiles 不得包含基线 profile “{profileName}”。";
                    return false;
                }

                if (!IsSimpleName(profileName))
                {
                    error = $"dshhome 的 profile 名非法：{profileName}";
                    return false;
                }

                if (!TryReadStringArray(property.Value, "bundles", out var bundles, out var bundlesError))
                {
                    error = bundlesError;
                    return false;
                }

                if (!TryReadDependencies(property.Value, PackManifestVersion.V5, out var dependencies, out var dependenciesError))
                {
                    error = dependenciesError;
                    return false;
                }

                list.Add(new PackHomeProfile(profileName, bundles, dependencies, ReadString(property.Value, "patch")));
            }

            if (list.Count == 0)
            {
                error = "dshhome 形态的 profiles 至少需要 1 个。";
                return false;
            }

            profiles = list.ToArray();
        }
        else
        {
            error = "dshhome 形态缺少必需字段 profiles。";
            return false;
        }

        if (root.TryGetProperty("skills", out var skillsElement) && skillsElement.ValueKind == JsonValueKind.Array)
        {
            var list = new List<PackSkillEntry>();
            foreach (var item in skillsElement.EnumerateArray())
            {
                var path = ReadString(item, "path")?.Trim();
                if (!IsSafeRelativePath(path))
                {
                    error = $"skills[] 的 path 非法或缺失：{path}";
                    return false;
                }

                var sha = ReadString(item, "sha256")?.Trim();
                if (sha is not null && !IsValidSha256(sha))
                {
                    error = $"skills[] 的 sha256 不是 64 位十六进制：{sha}";
                    return false;
                }

                var size = ReadLong(item, "size");
                var urls = ReadUrls(item);
                list.Add(new PackSkillEntry(path!, sha, size, urls));
            }

            skills = list.ToArray();
        }

        instructions = ReadString(root, "instructions")?.Trim();
        return true;
    }

    private static bool TryReadDependencies(
        JsonElement root,
        PackManifestVersion version,
        out IReadOnlyDictionary<string, string> dependencies,
        out string? error)
    {
        dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        if (!root.TryGetProperty("dependencies", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "dependencies 必须是对象。";
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var key = property.Name.Trim();
            if (key.Length == 0)
            {
                error = "dependencies 存在空键。";
                return false;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = (property.Value.GetString() ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    error = $"dependencies[{key}] 的值为空。";
                    return false;
                }

                map[key] = value;
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("version", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String)
            {
                // v4+ 允许对象形态（额外携带 mirrors 等信息）；只取固定版本。
                var value = (versionElement.GetString() ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    error = $"dependencies[{key}].version 为空。";
                    return false;
                }

                map[key] = value;
                continue;
            }

            error = $"dependencies[{key}] 的值必须是字符串" + (version == PackManifestVersion.V4 || version == PackManifestVersion.V5 ? "或含 version 的对象。" : "。");
            return false;
        }

        dependencies = map;
        return true;
    }

    private static bool TryReadFiles(JsonElement root, out PackFileEntry[] files, out string? error)
    {
        files = Array.Empty<PackFileEntry>();
        error = null;
        if (!root.TryGetProperty("files", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = "files 必须是数组。";
            return false;
        }

        var list = new List<PackFileEntry>();
        foreach (var item in element.EnumerateArray())
        {
            var path = ReadString(item, "path")?.Trim();
            if (!IsSafeRelativePath(path))
            {
                error = $"files[].path 非法或缺失：{path}";
                return false;
            }

            var sha = ReadString(item, "sha256")?.Trim();
            if (!IsValidSha256(sha))
            {
                error = $"files[].sha256 必须是 64 位小写十六进制：{path}";
                return false;
            }

            var size = ReadLong(item, "size");
            if (size is null or <= 0)
            {
                error = $"files[].size 必须是正整数：{path}";
                return false;
            }

            var urls = ReadUrls(item);
            if (urls.Count == 0)
            {
                error = $"files[].urls 不能为空：{path}";
                return false;
            }

            list.Add(new PackFileEntry(path!, sha!, size.Value, urls));
        }

        files = list.ToArray();
        return true;
    }

    private static IReadOnlyList<string> ReadUrls(JsonElement element)
    {
        if (!element.TryGetProperty("urls", out var urlsElement) || urlsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in urlsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = (item.GetString() ?? string.Empty).Trim();
            if (value.Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            {
                continue;
            }

            list.Add(value);
        }

        return list;
    }

    private static bool ReadLocalized(
        JsonElement root,
        string propertyName,
        out IReadOnlyDictionary<string, string> map,
        out string? error)
    {
        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var single = (element.GetString() ?? string.Empty).Trim();
            if (single.Length > 0)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["default"] = single };
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"{propertyName} 必须是字符串或语言映射。";
            return false;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = $"{propertyName}.{property.Name} 必须是字符串。";
                return false;
            }

            var value = (property.Value.GetString() ?? string.Empty).Trim();
            if (value.Length > 0)
            {
                result[property.Name.Trim()] = value;
            }
        }

        map = result;
        return true;
    }

    private static bool TryReadStringArray(JsonElement root, string propertyName, out string[] values, out string? error)
    {
        values = Array.Empty<string>();
        error = null;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = $"{propertyName} 必须是字符串数组。";
            return false;
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"{propertyName} 必须是字符串数组。";
                return false;
            }

            var value = (item.GetString() ?? string.Empty).Trim();
            if (value.Length > 0)
            {
                list.Add(value);
            }
        }

        values = list.ToArray();
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
