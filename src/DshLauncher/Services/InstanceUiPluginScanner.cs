using System.IO;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>UI 类插件的种类（非独立应用的界面提供者）。</summary>
public enum UiPluginKind
{
    Tui,
    Gui
}

/// <summary>判定强度：spec 声明＞关键字＞包名。</summary>
public enum UiPluginConfidence
{
    Strong,
    Medium,
    Weak
}

/// <summary>扫描到的一个 UI 类插件。</summary>
public sealed record UiPluginInfo(
    string Name,
    string? Version,
    UiPluginKind Kind,
    UiPluginConfidence Confidence,
    string Evidence,
    bool Enabled);

/// <summary>实例 UI 插件扫描结果。</summary>
public sealed record UiPluginScanResult(
    string ProfileName,
    IReadOnlyList<UiPluginInfo> Plugins,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<UiPluginInfo> TuiPlugins =>
        Plugins.Where(plugin => plugin.Kind == UiPluginKind.Tui).ToList();

    public IReadOnlyList<UiPluginInfo> GuiPlugins =>
        Plugins.Where(plugin => plugin.Kind == UiPluginKind.Gui).ToList();

    public bool HasTuiProvider => TuiPlugins.Count > 0;

    public bool HasEnabledTuiProvider => TuiPlugins.Any(plugin => plugin.Enabled);

    public IReadOnlyList<UiPluginInfo> NotEnabledTuiPlugins =>
        TuiPlugins.Where(plugin => !plugin.Enabled).ToList();
}

/// <summary>
/// 扫描实例里"非独立应用的 TUI/GUI 插件"（work-log/89，变更集 106）。
///
/// 事实约束（work-log/89 §一）：
///   * 官方 dsh 核心里**没有** `dsh-ecosystem-spec`；它是社区约定；
///   * 只有旗舰 `@deepseek-harness-tui/dsh-tui` 在 `package.json` 的 `imports` 里声明
///     `#dsh-ecosystem-spec/tui-channel|tui-contributions|profile-definitions`；
///   * 其它 TUI 插件（抽查 4 个）只有 `keywords`（tui/terminal/cli）与包名；
///   * 因此只能用**多层启发式**（spec 声明 = Strong；keywords = Medium；包名 = Weak），
///     GUI 没有官方通道，只作展示、不据此造启动方式。
///
/// 纯逻辑 + 安全只读 IO（FileShare.ReadWrite、坏 JSON 跳过并计数），无 WPF 依赖，便于自测。
/// </summary>
public static class InstanceUiPluginScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static UiPluginScanResult Scan(ManagerInstance instance, string? profileName)
    {
        var name = string.IsNullOrWhiteSpace(profileName) ? "web" : profileName!;
        var warnings = new List<string>();
        var plugins = new List<UiPluginInfo>();

        var profileDirectory = Path.Combine(instance.DshHome, "profiles", name);
        var (bundles, dependencies) = ReadProfileManifest(profileDirectory, warnings);
        var enabled = new HashSet<string>(bundles, StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(bundles, StringComparer.OrdinalIgnoreCase);
        candidates.UnionWith(dependencies);

        // 两种布局都要扫：0.1.5+ 用共享 profiles/node_modules；0.1.2 等旧版把依赖装在 profiles/<名>/node_modules。
        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodeModulesDirectories = new[]
        {
            Path.Combine(profileDirectory, "node_modules"),
            Path.Combine(instance.DshHome, "profiles", "node_modules")
        };

        foreach (var nodeModulesDirectory in nodeModulesDirectories)
        foreach (var packageDirectory in EnumeratePackageDirectories(nodeModulesDirectory))
        {
            var manifestPath = Path.Combine(packageDirectory, "package.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(ReadAllTextShared(manifestPath));
                var root = document.RootElement;
                var packageName = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : Path.GetFileName(packageDirectory);
                if (string.IsNullOrWhiteSpace(packageName) || !candidates.Contains(packageName!))
                {
                    continue; // 只算该 profile 的直接插件；传递依赖库（picocolors/node-pty 等）不算
                }

                if (!seenPackages.Add(packageName!))
                {
                    continue; // 两处布局可能都有同名包，只取第一次遇到的
                }

                if (!IsDshPlugin(root))
                {
                    continue; // 没有 dsh 插件声明（dsh 字段 / 生态 spec imports）——普通库不算
                }

                var version = root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
                var keywords = ReadStringArray(root, "keywords");
                var specImports = ReadSpecImports(root);

                if (Classify(packageName!, version, keywords, specImports, enabled.Contains(packageName!)) is { } info)
                {
                    plugins.Add(info);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                if (warnings.Count < 5)
                {
                    warnings.Add($"跳过无法解析的包：{Path.GetFileName(packageDirectory)}（{ex.GetType().Name}）");
                }
            }
        }

        plugins.Sort((left, right) =>
        {
            var kind = left.Kind.CompareTo(right.Kind); // Tui(0) 在前
            if (kind != 0)
            {
                return kind;
            }

            var enabledOrder = right.Enabled.CompareTo(left.Enabled); // 已启用在前
            if (enabledOrder != 0)
            {
                return enabledOrder;
            }

            var confidence = left.Confidence.CompareTo(right.Confidence);
            if (confidence != 0)
            {
                return confidence;
            }

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new UiPluginScanResult(name, plugins, warnings);
    }

    /// <summary>分类（可自测）：spec 声明 &gt; keywords &gt; 包名；两者都不命中返回 null。
    /// 官方核心 scope（`@deepseek-ai/`）是运行时内部包，不算 UI 插件（`dsh-terminal*` 等带 terminal 关键字会误报）；
    /// 唯一的例外是自带桌面面（`dsh-desktop*`，将来可能出现的官方 GUI 面）。</summary>
    public static UiPluginInfo? Classify(
        string packageName,
        string? version,
        IReadOnlyCollection<string> keywords,
        IReadOnlyCollection<string> specImports,
        bool enabled)
    {
        var keywordSet = new HashSet<string>(keywords ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var specSet = new HashSet<string>(specImports ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var leaf = LeafName(packageName);
        var officialScope = packageName.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase);
        var officialDesktop = officialScope && leaf.StartsWith("dsh-desktop", StringComparison.OrdinalIgnoreCase);
        if (officialScope && !officialDesktop)
        {
            return null;
        }

        if (specSet.Any(item => item.StartsWith("#dsh-ecosystem-spec/tui", StringComparison.OrdinalIgnoreCase)))
        {
            return new UiPluginInfo(packageName, version, UiPluginKind.Tui, UiPluginConfidence.Strong,
                "#dsh-ecosystem-spec/tui 声明", enabled);
        }

        if (keywordSet.Contains("tui") || keywordSet.Contains("terminal"))
        {
            return new UiPluginInfo(packageName, version, UiPluginKind.Tui, UiPluginConfidence.Medium,
                "keywords: tui/terminal", enabled);
        }

        if (leaf.Contains("tui", StringComparison.OrdinalIgnoreCase))
        {
            return new UiPluginInfo(packageName, version, UiPluginKind.Tui, UiPluginConfidence.Weak,
                "包名含 tui", enabled);
        }

        if (specSet.Any(item => item.StartsWith("#dsh-ecosystem-spec/gui", StringComparison.OrdinalIgnoreCase)))
        {
            return new UiPluginInfo(packageName, version, UiPluginKind.Gui, UiPluginConfidence.Strong,
                "#dsh-ecosystem-spec/gui 声明", enabled);
        }

        if (keywordSet.Contains("gui") || keywordSet.Contains("desktop"))
        {
            return new UiPluginInfo(packageName, version, UiPluginKind.Gui, UiPluginConfidence.Medium,
                "keywords: gui/desktop", enabled);
        }

        if (leaf.Contains("gui", StringComparison.OrdinalIgnoreCase) || leaf.Contains("desktop", StringComparison.OrdinalIgnoreCase))
        {
            return new UiPluginInfo(packageName, version, UiPluginKind.Gui, UiPluginConfidence.Weak,
                "包名含 gui/desktop", enabled);
        }

        return null;
    }

    private static string LeafName(string packageName)
    {
        var slash = packageName.LastIndexOf('/');
        return slash >= 0 ? packageName[(slash + 1)..] : packageName;
    }

    private static (List<string> Bundles, HashSet<string> Dependencies) ReadProfileManifest(
        string profileDirectory,
        List<string> warnings)
    {
        var bundles = new List<string>();
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(profileDirectory, "package.json");
        if (!File.Exists(manifestPath))
        {
            return (bundles, dependencies);
        }

        try
        {
            using var document = JsonDocument.Parse(ReadAllTextShared(manifestPath));
            var root = document.RootElement;
            if (root.TryGetProperty("dsh", out var dsh)
                && dsh.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("bundles", out var bundlesElement)
                && bundlesElement.ValueKind == JsonValueKind.Array)
            {
                bundles.AddRange(bundlesElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            if (root.TryGetProperty("dependencies", out var dependenciesElement)
                && dependenciesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in dependenciesElement.EnumerateObject())
                {
                    dependencies.Add(property.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add("无法读取 profile 的 package.json（bundles/dependencies 视为空）。");
        }

        return (bundles, dependencies);
    }

    /// <summary>是否声明为 dsh 插件：有 `dsh` 字段（如 <c>dsh.bundle</c>）或生态 spec imports。</summary>
    private static bool IsDshPlugin(JsonElement root)
    {
        if (root.TryGetProperty("dsh", out _))
        {
            return true;
        }

        return ReadSpecImports(root).Any(item =>
            item.StartsWith("#dsh-ecosystem-spec/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>枚举 node_modules 下的包目录（含 @scope 一层）。</summary>
    private static IEnumerable<string> EnumeratePackageDirectories(string nodeModulesDirectory)
    {
        if (!Directory.Exists(nodeModulesDirectory))
        {
            yield break;
        }

        string[] entries;
        try
        {
            entries = Directory.GetDirectories(nodeModulesDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            var entryName = Path.GetFileName(entry);
            if (entryName.StartsWith('.'))
            {
                continue;
            }

            if (entryName.StartsWith('@'))
            {
                string[] scoped;
                try
                {
                    scoped = Directory.GetDirectories(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var package in scoped)
                {
                    if (!Path.GetFileName(package).StartsWith('.'))
                    {
                        yield return package;
                    }
                }

                continue;
            }

            yield return entry;
        }
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    private static List<string> ReadSpecImports(JsonElement root)
    {
        if (!root.TryGetProperty("imports", out var imports) || imports.ValueKind != JsonValueKind.Object)
        {
            return new List<string>();
        }

        return imports.EnumerateObject().Select(property => property.Name).ToList();
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
