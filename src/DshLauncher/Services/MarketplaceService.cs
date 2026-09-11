using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Reads plugin discovery sources and checks the selected package before the
/// official DSh CLI is allowed to change an instance. A catalog is only a
/// discovery list; it is not treated as proof that a package is installable.
/// </summary>
public sealed class MarketplaceService
{
    public const string CommunityCatalogUrl = "https://awesome-dsh-plugin.com/plugins.json";

    /// <summary>插件官网（中文站）地址：仅用于界面打开浏览，数据仍来自 plugins.json。</summary>
    public const string CommunitySiteZhUrl = "https://awesome-dsh-plugin.com/zh/";

    /// <summary>
    /// 单个来源超时。社区目录完整数据约 1.9MB，本网络实测 25-60s 波动；
    /// 预算与刷新总超时（90s）对齐，避免网络抖动导致整次刷新失败。
    /// </summary>
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(90);
    private const int MaxThemePreviewBytes = 8 * 1024 * 1024;
    private static readonly Regex MarkdownImage = new(
        @"!\[[^\]]*\]\(\s*(?:<(?<url>[^>]+)>|(?<url>[^\s\)]+))(?:\s+[\""'][^\)]*)?\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlImage = new(
        @"<img\b[^>]*?\bsrc\s*=\s*[\""'](?<url>[^\""']+)[\""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] PluginProfileFiles =
    {
        "package.json",
        "pnpm-lock.yaml",
        "pnpm-workspace.yaml",
        "package-lock.json",
        "yarn.lock",
        "cordis.patch.yml"
    };
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly HttpClient _httpClient;
    private readonly LauncherPaths _paths;
    private readonly Deepseek1024CatalogService? _chineseCatalog;
    private readonly DshfindCatalogService? _dshfindCatalog;
    private readonly Func<IReadOnlyList<string>> _pluginCustomSources;
    private readonly IReadOnlyList<Uri> _customSources;
    private readonly Dictionary<string, ThemeReadmePreview> _themePreviewCache = new(StringComparer.OrdinalIgnoreCase);

    public MarketplaceService(
        LauncherPaths? paths = null,
        HttpClient? httpClient = null,
        IEnumerable<Uri>? customSources = null,
        Deepseek1024CatalogService? chineseCatalog = null,
        DshfindCatalogService? dshfindCatalog = null,
        Func<IReadOnlyList<string>>? pluginCustomSources = null)
    {
        _paths = paths ?? new LauncherPaths();
        _httpClient = httpClient ?? CreateHttpClient();
        _customSources = customSources?.Where(uri => uri.IsAbsoluteUri).ToArray() ?? Array.Empty<Uri>();
        _chineseCatalog = chineseCatalog;
        _dshfindCatalog = dshfindCatalog;
        _pluginCustomSources = pluginCustomSources ?? (() => Array.Empty<string>());
    }

    public async Task<MarketplaceSearchResult> SearchAsync(
        ManagerInstance? instance,
        string? query = null,
        CancellationToken cancellationToken = default,
        MarketplaceSourceKind? sourceKind = null,
        MarketplaceSortOrder sortOrder = MarketplaceSortOrder.Relevance,
        IProgress<MarketplaceRefreshProgress>? progress = null)
    {
        var items = new List<MarketplaceItem>();
        var warnings = new List<string>();
        var sourcesChecked = 0;

        // 插件市场来源：GitHub（awesome-dsh-plugin 目录）。中文官网即同一仓库
        // （?lang=zh 被服务器忽略，字节级同源），不作为独立来源；界面仅保留
        // “中文官网↗” 链接按钮供浏览。
        var sourceTasks = new[]
        {
            LoadRemoteCatalogAsync(new Uri(CommunityCatalogUrl), MarketplaceSourceKind.CommunityCatalog, "GitHub", cancellationToken)
        };

        // 各来源并行拉取（各自带源级超时）：每完成一个立即上报合并结果，
        // UI 先把已到达的部分分批显示，其余继续在后台更新。
        var wrapped = sourceTasks.Select(async task =>
        {
            try
            {
                return (Ok: true, Items: await task, Error: (string?)null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (Ok: false, Items: (IReadOnlyList<MarketplaceItem>?)null, Error: "一个插件来源响应超时，已跳过。");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                return (Ok: false, Items: (IReadOnlyList<MarketplaceItem>?)null, Error: $"一个插件来源暂时无法读取：{ex.Message}");
            }
        }).ToArray();
        while (wrapped.Length > 0)
        {
            var done = await Task.WhenAny(wrapped);
            var sourceResult = await done;
            sourcesChecked++;
            if (sourceResult.Ok)
            {
                items.AddRange(sourceResult.Items!);
            }
            else
            {
                warnings.Add(sourceResult.Error!);
            }

            progress?.Report(new MarketplaceRefreshProgress(
                MergeItems(items),
                warnings.ToArray(),
                sourcesChecked));
            wrapped = wrapped.Where(item => !ReferenceEquals(item, done)).ToArray();
        }

        // 中文插件源（默认关闭；见设置 →「插件与技能来源」）：第三方源不可用只记一条告警。
        var configuredPluginSources = _pluginCustomSources();
        if (_chineseCatalog is not null
            && configuredPluginSources.Contains(MarketSourceSettingsService.AdapterZh1024, StringComparer.OrdinalIgnoreCase))
        {
            sourcesChecked++;
            try
            {
                var chineseItems = await _chineseCatalog.LoadAsync(cancellationToken);
                if (chineseItems.Count > 0)
                {
                    items.AddRange(chineseItems);
                }
                else
                {
                    warnings.Add($"中文插件源不可用：{_chineseCatalog.LastStatus}");
                }

                progress?.Report(new MarketplaceRefreshProgress(
                    MergeItems(items),
                    warnings.ToArray(),
                    sourcesChecked));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
            {
                warnings.Add($"中文插件源读取失败：{ex.Message}");
            }
        }

        // 第二个中文源 dshfind.com（同样默认关闭；8 MB 全量载荷由服务内部单飞 + TTL 处理）。
        if (_dshfindCatalog is not null
            && configuredPluginSources.Contains(MarketSourceSettingsService.AdapterDshfind, StringComparer.OrdinalIgnoreCase))
        {
            sourcesChecked++;
            try
            {
                var dshfindItems = await _dshfindCatalog.LoadAsync(cancellationToken);
                if (dshfindItems.Count > 0)
                {
                    items.AddRange(dshfindItems);
                }
                else
                {
                    warnings.Add($"dshfind 中文源不可用：{_dshfindCatalog.LastStatus}");
                }

                progress?.Report(new MarketplaceRefreshProgress(
                    MergeItems(items),
                    warnings.ToArray(),
                    sourcesChecked));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
            {
                warnings.Add($"dshfind 中文源读取失败：{ex.Message}");
            }
        }

        IReadOnlyList<(bool IsFile, string Value)> customSources;
        try
        {
            customSources = await ReadCustomSourcesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            customSources = Array.Empty<(bool IsFile, string Value)>();
            warnings.Add($"自定义目录设置无法读取：{ex.Message}");
        }

        foreach (var customSource in customSources)
        {
            sourcesChecked++;
            try
            {
                items.AddRange(customSource.IsFile
                    ? ParseCatalog(File.ReadAllText(customSource.Value, Encoding.UTF8), MarketplaceSourceKind.Custom, customSource.Value)
                    : await LoadRemoteCatalogAsync(new Uri(customSource.Value), MarketplaceSourceKind.Custom, customSource.Value, cancellationToken));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"自定义目录超时：{customSource.Value}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                warnings.Add($"自定义目录无法读取：{customSource.Value}（{ex.Message}）");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cached = TryReadCache();
        var mergedItems = MergeItems(items);
        var remoteItems = mergedItems.ToArray();
        if (remoteItems.Length > 0)
        {
            TryWriteCache(remoteItems, sourcesChecked, DateTimeOffset.UtcNow, warnings);
        }
        else if (cached is not null)
        {
            mergedItems = MergeItems(cached.Items).ToArray();
            sourcesChecked = Math.Max(sourcesChecked, cached.SourcesChecked);
            warnings.Add("在线来源暂时没有返回结果，已显示上次缓存。 ");
        }

        var retrievedAt = DateTimeOffset.UtcNow;
        return new MarketplaceSearchResult(
            FilterAndSortMerged(mergedItems, query, sourceKind, sortOrder),
            warnings,
            sourcesChecked,
            retrievedAt);
    }

    public MarketplaceSearchResult? ReadCached(
        ManagerInstance? instance,
        string? query = null,
        MarketplaceSourceKind? sourceKind = null,
        MarketplaceSortOrder sortOrder = MarketplaceSortOrder.Relevance)
    {
        var cached = TryReadCache();
        if (cached is null)
        {
            return null;
        }

        var mergedItems = MergeItems(cached.Items);
        return new MarketplaceSearchResult(
            FilterAndSortMerged(mergedItems, query, sourceKind, sortOrder),
            new[] { $"正在使用上次缓存（{cached.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm}）。" },
            cached.SourcesChecked,
            cached.RetrievedAt);
    }

    public static IReadOnlyList<MarketplaceItem> FilterAndSort(
        IEnumerable<MarketplaceItem> items,
        string? query = null,
        MarketplaceSourceKind? sourceKind = null,
        MarketplaceSortOrder sortOrder = MarketplaceSortOrder.Relevance,
        string? category = null) =>
        FilterAndSortMerged(MergeItems(items), query, sourceKind, sortOrder, category);

    internal static IReadOnlyList<MarketplaceItem> FilterAndSortMerged(
        IEnumerable<MarketplaceItem> items,
        string? query = null,
        MarketplaceSourceKind? sourceKind = null,
        MarketplaceSortOrder sortOrder = MarketplaceSortOrder.Relevance,
        string? category = null)
    {
        var normalizedQuery = query?.Trim();
        var filtered = items
            .Where(item => sourceKind is null || HasSourceKind(item, sourceKind.Value))
            .Where(item => string.IsNullOrWhiteSpace(category)
                || string.Equals(NormalizeCategory(item.Category), NormalizeCategory(category), StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(normalizedQuery) || Matches(item, normalizedQuery!))
            .ToArray();

        return sortOrder switch
        {
            MarketplaceSortOrder.PublishedAt => filtered
                .OrderBy(item => item.PublishedAt is null)
                .ThenByDescending(item => item.PublishedAt)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MarketplaceSortOrder.Stars => filtered
                .OrderBy(item => item.Stars is null)
                .ThenByDescending(item => item.Stars)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => filtered
                .OrderBy(item => item.VerificationStatus == MarketplaceVerificationStatus.Rejected)
                .ThenByDescending(item => MatchRank(item, normalizedQuery))
                .ThenByDescending(item => item.Stars ?? -1)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    /// <summary>
    /// 社区目录与常见仓库声明的规范分类键 → 市场分类 Tab。
    /// 社区目录自带 21 个分类（ui/tools/dev/workflow/skill/usage/memory/…），
    /// 仅靠中文/英文关键词匹配会让大半条目跌入“未分类”、多数 Tab 为空。
    /// </summary>
    private static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ui"] = "UI", ["sidebar"] = "UI", ["interface"] = "UI",
        ["tool"] = "工具", ["tools"] = "工具", ["utility"] = "工具", ["utilities"] = "工具",
        ["browser"] = "工具", ["vision"] = "工具", ["voice"] = "工具", ["docs"] = "工具",
        ["notify"] = "工具", ["security"] = "工具", ["market"] = "工具", ["remote"] = "工具",
        ["workflow"] = "工作流", ["automation"] = "工作流", ["session"] = "工作流", ["memory"] = "工作流",
        ["agent"] = "Agent", ["agents"] = "Agent", ["skill"] = "Agent", ["skills"] = "Agent", ["mcp"] = "Agent",
        ["model"] = "模型", ["models"] = "模型", ["provider"] = "模型", ["providers"] = "模型",
        ["theme"] = "主题", ["themes"] = "主题", ["skin"] = "主题", ["wallpaper"] = "主题",
        ["dev"] = "开发", ["developer"] = "开发", ["development"] = "开发", ["git"] = "开发", ["code"] = "开发"
    };

    public static string NormalizeCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "未分类";
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (CategoryAliases.TryGetValue(normalized, out var aliased))
        {
            return aliased;
        }

        if (normalized.Contains("ui") || normalized.Contains("界面") || normalized.Contains("sidebar"))
        {
            return "UI";
        }

        if (normalized.Contains("workflow") || normalized.Contains("工作流"))
        {
            return "工作流";
        }

        if (normalized.Contains("agent") || normalized.Contains("代理"))
        {
            return "Agent";
        }

        if (normalized.Contains("model") || normalized.Contains("模型") || normalized.Contains("provider"))
        {
            return "模型";
        }

        if (normalized.Contains("theme") || normalized.Contains("主题") || normalized.Contains("wallpaper") || normalized.Contains("皮肤"))
        {
            return "主题";
        }

        if (normalized.Contains("dev") || normalized.Contains("开发") || normalized.Contains("tooling") || normalized.Contains("developer"))
        {
            return "开发";
        }

        if (normalized.Contains("tool") || normalized.Contains("工具"))
        {
            return "工具";
        }

        return "未分类";
    }

    public static MarketplaceUpdateStatus GetUpdateStatus(string? availableVersion, string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(availableVersion) || string.IsNullOrWhiteSpace(installedVersion))
        {
            return MarketplaceUpdateStatus.Unknown;
        }

        if (TryParseVersion(availableVersion, out var available)
            && TryParseVersion(installedVersion, out var installed))
        {
            return available > installed
                ? MarketplaceUpdateStatus.Available
                : MarketplaceUpdateStatus.UpToDate;
        }

        return string.Equals(NormalizeVersionText(availableVersion), NormalizeVersionText(installedVersion), StringComparison.OrdinalIgnoreCase)
            ? MarketplaceUpdateStatus.UpToDate
            : MarketplaceUpdateStatus.Unavailable;
    }

    public static IReadOnlySet<string> GetPluginIdentities(MarketplaceItem item) =>
        GetPluginIdentities(item.PackageName, item.Name, item.InstallSpec, item.RepositoryUrl);

    public static string? GetGitHubRepositoryUrl(MarketplaceItem item)
    {
        foreach (var value in new[] { item.RepositoryUrl, item.InstallSpec })
        {
            if (!string.IsNullOrWhiteSpace(value)
                && TryGetGitHubRepository(value, out var repository))
            {
                return $"https://github.com/{repository.Owner}/{repository.Name}";
            }
        }

        return null;
    }

    public static string? GetDeveloperAvatarUrl(MarketplaceItem item)
    {
        return TryGetGitHubRepository(item.RepositoryUrl ?? item.InstallSpec, out var repository)
            ? $"https://github.com/{repository.Owner}.png?size=96"
            : null;
    }

    public async Task<ThemeReadmePreview> GetThemeReadmePreviewAsync(
        MarketplaceItem item,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetGitHubRepository(item.RepositoryUrl ?? item.InstallSpec, out var repository))
        {
            return new ThemeReadmePreview(null, null, "这个主题条目没有可读取的 GitHub 仓库。");
        }

        var cacheKey = $"{repository.Owner}/{repository.Name}";
        lock (_themePreviewCache)
        {
            if (_themePreviewCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        ThemeReadmePreview preview;
        try
        {
            preview = await ReadThemePreviewAsync(repository, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            preview = new ThemeReadmePreview(null, null, "读取 GitHub README 超时，请稍后重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or InvalidDataException)
        {
            preview = new ThemeReadmePreview(null, null, $"无法读取 GitHub README：{ex.Message}");
        }

        lock (_themePreviewCache)
        {
            _themePreviewCache[cacheKey] = preview;
        }

        return preview;
    }

    public static IReadOnlySet<string> GetPluginIdentities(ExtensionEntry entry) =>
        GetPluginIdentities(entry.Name, entry.Name, entry.Version, null);

    public static ExtensionEntry? FindInstalledPlugin(
        MarketplaceItem item,
        IEnumerable<ExtensionEntry> installedPlugins)
    {
        var identities = GetPluginIdentities(item);
        return installedPlugins.FirstOrDefault(entry =>
            entry.Kind == ExtensionKind.Plugin
            && GetPluginIdentities(entry).Any(identities.Contains));
    }

    public async Task<MarketplaceVerificationResult> VerifyAsync(
        MarketplaceItem item,
        CancellationToken cancellationToken = default,
        ManagerInstance? instance = null)
    {
        if (item.SourceKind == MarketplaceSourceKind.Official
            && item.VerificationStatus == MarketplaceVerificationStatus.Verified)
        {
            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Verified,
                "已从当前 DSh 运行环境读取到有效的 bundle 配置。",
                item.PackageName,
                item.Version,
                item.InstallSpec);
        }

        var installTargetsGitHub = TryGetGitHubRepository(item.InstallSpec, out _)
            || (string.IsNullOrWhiteSpace(item.PackageName)
                && !string.IsNullOrWhiteSpace(item.RepositoryUrl));
        if (installTargetsGitHub)
        {
            return await VerifyGitHubRepositoryAsync(item, cancellationToken, instance);
        }

        if (!string.IsNullOrWhiteSpace(item.PackageName))
        {
            return await VerifyNpmPackageAsync(item, cancellationToken, instance: instance);
        }

        return new MarketplaceVerificationResult(
            MarketplaceVerificationStatus.Rejected,
            "没有找到 npm 包名或 GitHub 仓库地址。",
            null,
            null,
            null);
    }

    /// <summary>
    /// 手动安装框的安装前校验：确认目标确实是 DSH Plugin（有 dsh.bundle.patch +
    /// 可加载入口），避免把普通 npm 包装进 profile（只会变成“已安装（默认禁用）”，
    /// 点“启用”还会把非插件写进 bundles）。
    /// 返回 Verified=确认是插件；Rejected=确定不是；Unverified=无法判定（网络
    /// 不可达/无法识别的 spec），调用方应放行但提示。
    /// </summary>
    public async Task<MarketplaceVerificationResult> VerifyManualInstallAsync(
        string installSpec,
        CancellationToken cancellationToken = default,
        ManagerInstance? instance = null)
    {
        var text = installSpec?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Unverified,
                "没有填写安装目标。",
                null,
                null,
                text);
        }

        try
        {
            // 本地目录 / package.json：直接读清单判定，不联网。
            var localManifest = TryResolveLocalManifest(text);
            if (localManifest is not null)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(localManifest, Encoding.UTF8));
                return VerifyManifest(
                    document.RootElement,
                    ReadString(document.RootElement, "name"),
                    ReadString(document.RootElement, "version"),
                    text,
                    instance);
            }

            if (TrySplitNpmSpec(text, out var packageName, out var version))
            {
                // 只有精确版本才按版本查；范围/标签一律按 latest 判定。
                var exactVersion = version is { Length: > 0 } && char.IsDigit(version[0]) ? version : null;
                var item = CreateManualItem(text, packageName, exactVersion);
                MarketplaceVerificationResult? lastFailure = null;
                foreach (var registryBase in new[]
                         {
                             "https://registry.npmmirror.com",
                             "https://registry.npmjs.org"
                         })
                {
                    try
                    {
                        return await VerifyNpmPackageAsync(item, cancellationToken, registryBase, instance);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return new MarketplaceVerificationResult(
                            MarketplaceVerificationStatus.Rejected,
                            $"npm 上找不到这个包：{packageName}（确认包名拼写，或换一个包）。",
                            packageName,
                            null,
                            text);
                    }
                    catch (HttpRequestException ex)
                    {
                        lastFailure = new MarketplaceVerificationResult(
                            MarketplaceVerificationStatus.Unverified,
                            $"无法联网确认这个包是否为 DSH 插件（{ex.Message}）。",
                            packageName,
                            null,
                            text);
                    }
                    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // 镜像超时：换下一个源。
                    }
                }

                return lastFailure ?? new MarketplaceVerificationResult(
                    MarketplaceVerificationStatus.Unverified,
                    "无法联网确认这个包是否为 DSH 插件（registry 不可达）。",
                    packageName,
                    null,
                    text);
            }

            if (TryGetGitHubRepository(text, out _))
            {
                var item = CreateManualItem(text, null, null) with { RepositoryUrl = text };
                return await VerifyAsync(item, cancellationToken, instance);
            }

            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Unverified,
                "无法识别安装目标类型（既不是 npm 包名、GitHub 仓库，也不是本地目录），跳过插件校验。",
                null,
                null,
                text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Unverified,
                $"无法确认这个目标是否为 DSH 插件：{ex.Message}",
                null,
                null,
                text);
        }
    }

    /// <summary>
    /// 更新前兼容性检查：读 registry 最新版本的清单，只跑“是否 DSh 插件 + 核心 peerDependencies
    /// 是否与实例运行时兼容”。返回 Incompatible 时调用方应警告/跳过。
    /// </summary>
    public async Task<MarketplaceVerificationResult> CheckPluginCompatibilityAsync(
        string packageName,
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        var name = packageName?.Trim() ?? string.Empty;
        if (!IsSafePackageName(name))
        {
            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Unverified,
                "npm 包名格式不正确，无法检查兼容性。",
                name,
                null,
                name);
        }

        try
        {
            return await VerifyNpmPackageAsync(CreateManualItem(name, name, null), cancellationToken, instance: instance);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or IOException)
        {
            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Unverified,
                $"无法联网检查兼容性（{ex.Message}）。",
                name,
                null,
                name);
        }
    }

    private static MarketplaceItem CreateManualItem(string installSpec, string? packageName, string? version) =>
        new(
            "manual:" + installSpec,
            installSpec,
            packageName,
            version,
            string.Empty,
            installSpec,
            null,
            string.Empty,
            MarketplaceSourceKind.CommunityCatalog,
            "手动安装校验",
            MarketplaceVerificationStatus.Unverified,
            string.Empty);

    private static string? TryResolveLocalManifest(string text)
    {
        try
        {
            if (Directory.Exists(text))
            {
                var candidate = Path.Combine(text, "package.json");
                return File.Exists(candidate) ? candidate : null;
            }

            if (File.Exists(text)
                && string.Equals(Path.GetFileName(text), "package.json", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException)
        {
            // 不是可读取的本地路径：按其它 spec 类型继续。
        }

        return null;
    }

    private static bool TryGetNpmPackageName(string value, out string packageName) =>
        TrySplitNpmSpec(value, out packageName, out _);

    internal static bool TrySplitNpmSpec(string value, out string packageName, out string? version)
    {
        packageName = string.Empty;
        version = null;
        var text = value.Trim();
        if (text.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["npm:".Length..];
        }

        var versionMarker = text.StartsWith('@')
            ? text.IndexOf('@', text.IndexOf('/') + 1)
            : text.IndexOf('@');
        packageName = versionMarker > 0 ? text[..versionMarker] : text;
        version = versionMarker > 0 ? text[(versionMarker + 1)..] : null;
        if (!IsSafePackageName(packageName)
            || version is not null
                && (version.Length == 0
                    || version.Any(character => !(char.IsLetterOrDigit(character)
                        || character is '.' or '-' or '_' or '~' or '^' or '*' or '<' or '>' or '='))))
        {
            packageName = string.Empty;
            version = null;
            return false;
        }

        return true;
    }

    public string CreatePluginSnapshot(ManagerInstance instance)
    {
        var profileDirectory = Path.Combine(instance.DshHome, "profiles", "web");
        var existing = PluginProfileFiles
            .Select(file => Path.Combine(profileDirectory, file))
            .Where(File.Exists)
            .ToArray();
        if (existing.Length == 0)
        {
            return "当前实例还没有可备份的 web profile 配置";
        }

        var directory = Path.Combine(
            _paths.GetInstanceBackupDirectory(instance.Id),
            "plugins",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        foreach (var source in existing)
        {
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), overwrite: false);
        }

        PrunePluginSnapshots(Path.GetDirectoryName(directory)!, directory);
        return directory;
    }

    /// <summary>插件回滚点保留上限（与自动快照 10 份同口径，work-log/61）。</summary>
    private const int MaximumPluginSnapshots = 10;

    /// <summary>只保留最近 N 份插件回滚点；正在新建的那份永远保留。</summary>
    private static void PrunePluginSnapshots(string root, string newestDirectory)
    {
        try
        {
            var snapshots = Directory.EnumerateDirectories(root)
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.Ordinal)
                .ToArray();
            var kept = 0;
            foreach (var snapshot in snapshots)
            {
                if (++kept <= MaximumPluginSnapshots
                    || string.Equals(snapshot.FullName, newestDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                snapshot.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回收失败不影响本次插件操作。
        }
    }

    public bool RestorePluginSnapshot(ManagerInstance instance, string snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            return false;
        }

        var backupRoot = Path.GetFullPath(_paths.GetInstanceBackupDirectory(instance.Id));
        var snapshot = Path.GetFullPath(snapshotPath);
        if (!Directory.Exists(snapshot)
            || !IsWithinPath(snapshot, backupRoot)
            || string.Equals(snapshot, backupRoot, StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(snapshot))
        {
            return false;
        }

        var profileDirectory = Path.Combine(instance.DshHome, "profiles", "web");
        if (Directory.Exists(profileDirectory) && IsReparsePoint(profileDirectory))
        {
            throw new IOException("当前实例的 web profile 目录不能是重解析点。");
        }

        Directory.CreateDirectory(profileDirectory);
        foreach (var file in PluginProfileFiles)
        {
            var source = Path.Combine(snapshot, file);
            var target = Path.Combine(profileDirectory, file);
            if (File.Exists(source))
            {
                if (IsReparsePoint(source) || (File.Exists(target) && IsReparsePoint(target)))
                {
                    throw new IOException($"无法安全恢复 Plugin 配置：{file}");
                }

                File.Copy(source, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                if (IsReparsePoint(target))
                {
                    throw new IOException($"无法安全删除失败操作留下的配置：{file}");
                }

                File.Delete(target);
            }
        }

        return true;
    }

    public static IReadOnlyList<MarketplaceItem> ParseCatalog(
        string json,
        MarketplaceSourceKind sourceKind,
        string sourceName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array
                ? plugins.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();

        if (entries.Length == 0 && root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("目录没有 plugins 数组。");
        }

        return entries
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => ParseCatalogEntry(entry, sourceKind, sourceName))
            .Where(item => item is not null)
            .Cast<MarketplaceItem>()
            .ToArray();
    }

    private async Task<IReadOnlyList<MarketplaceItem>> LoadRemoteCatalogAsync(
        Uri uri,
        MarketplaceSourceKind sourceKind,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(uri, cancellationToken);
        return ParseCatalog(json, sourceKind, sourceName);
    }

    private async Task<MarketplaceVerificationResult> VerifyNpmPackageAsync(
        MarketplaceItem item,
        CancellationToken cancellationToken,
        string registryBase = "https://registry.npmjs.org",
        ManagerInstance? instance = null)
    {
        var packageName = item.PackageName!.Trim();
        if (!IsSafePackageName(packageName))
        {
            return Rejected(item, "npm 包名格式不正确。");
        }

        var encodedName = packageName.Replace("/", "%2f", StringComparison.Ordinal);
        var uri = new Uri($"{registryBase.TrimEnd('/')}/{encodedName}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var version = item.Version;
        if (string.IsNullOrWhiteSpace(version)
            && root.TryGetProperty("dist-tags", out var tags)
            && tags.TryGetProperty("latest", out var latest)
            && latest.ValueKind == JsonValueKind.String)
        {
            version = latest.GetString();
        }

        if (string.IsNullOrWhiteSpace(version)
            || !root.TryGetProperty("versions", out var versions)
            || !versions.TryGetProperty(version, out var packageManifest))
        {
            return Rejected(item, "npm 仓库没有找到可读取的版本信息。");
        }

        return VerifyManifest(packageManifest, packageName, version, packageName, instance);
    }

    private async Task<MarketplaceVerificationResult> VerifyGitHubRepositoryAsync(
        MarketplaceItem item,
        CancellationToken cancellationToken,
        ManagerInstance? instance = null)
    {
        var normalizedInstallSpec = NormalizeInstallSpec(item.InstallSpec);
        var githubSource = normalizedInstallSpec.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
            || normalizedInstallSpec.Contains("github.com/", StringComparison.OrdinalIgnoreCase)
            ? item.InstallSpec
            : item.RepositoryUrl!;
        if (!TryGetGitHubRepository(githubSource, out var repository))
        {
            return Rejected(item, "GitHub 地址格式不正确，无法定位仓库。", item.RepositoryUrl);
        }

        var branches = new List<string>();
        string? defaultBranch = null;
        if (!string.IsNullOrWhiteSpace(repository.Branch))
        {
            branches.Add(repository.Branch);
            defaultBranch = repository.Branch;
        }
        else
        {
            try
            {
                var metadataUri = new Uri($"https://api.github.com/repos/{repository.Owner}/{repository.Name}");
                using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUri);
                using var metadataResponse = await SendAsync(metadataRequest, cancellationToken);
                using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(cancellationToken));
                defaultBranch = ReadString(metadata.RootElement, "default_branch");
                if (!string.IsNullOrWhiteSpace(defaultBranch))
                {
                    branches.Add(defaultBranch);
                }
            }
            catch (HttpRequestException)
            {
                // Public API metadata can be rate limited. Keep the legacy
                // fallbacks so verification still works for common repos.
            }
        }

        foreach (var fallback in new[] { "main", "master" })
        {
            if (!branches.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                branches.Add(fallback);
            }
        }

        var packagePath = string.IsNullOrWhiteSpace(repository.Subpath)
            ? "package.json"
            : repository.Subpath.EndsWith("package.json", StringComparison.OrdinalIgnoreCase)
                ? repository.Subpath.TrimStart('/')
                : $"{repository.Subpath.Trim('/')}/package.json";
        MarketplaceVerificationResult? manifestRejection = null;
        foreach (var branch in branches)
        {
            var encodedPath = string.Join(
                "/",
                packagePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));
            var encodedBranch = Uri.EscapeDataString(branch);
            var uri = new Uri($"https://raw.githubusercontent.com/{repository.Owner}/{repository.Name}/{encodedBranch}/{encodedPath}");
            try
            {
                var json = await GetStringWithMirrorAsync(uri, cancellationToken);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var packageName = ReadString(root, "name");
                var version = ReadString(root, "version");
                var verification = VerifyManifest(root, packageName, version, item.InstallSpec);
                if (verification.Status == MarketplaceVerificationStatus.Verified
                    || !string.IsNullOrWhiteSpace(repository.Subpath))
                {
                    return verification;
                }

                manifestRejection ??= verification;
            }
            catch (HttpRequestException)
            {
                // Try the next branch or the next fallback below.
            }
            catch (JsonException ex)
            {
                return Rejected(item, $"仓库 package.json 格式无效：{ex.Message}", item.InstallSpec);
            }
        }

        if (string.IsNullOrWhiteSpace(repository.Subpath))
        {
            foreach (var discoveryBranch in branches.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var discovered = await FindPluginInRepositoryTreeAsync(
                    repository,
                    discoveryBranch,
                    cancellationToken,
                    instance);
                if (discovered is not null)
                {
                    return discovered;
                }
            }
        }

        if (manifestRejection is not null)
        {
            return manifestRejection;
        }

        var location = string.IsNullOrWhiteSpace(repository.Subpath)
            ? "仓库"
            : $"仓库子目录 /{repository.Subpath.Trim('/')}/";
        return Rejected(item, $"{location}没有找到 package.json。", item.InstallSpec);
    }

    private async Task<string?> ReadReadmeTextAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken)
    {
        var rawUri = new Uri($"https://raw.githubusercontent.com/{repository.Owner}/{repository.Name}/HEAD/README.md");
        try
        {
            return await GetStringWithMirrorAsync(rawUri, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // raw 直连与镜像均不可用：回退 api /readme 端点。
        }

        try
        {
            var readmeUri = new Uri($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/readme");
            using var readmeRequest = new HttpRequestMessage(HttpMethod.Get, readmeUri);
            using var readmeResponse = await SendAsync(readmeRequest, cancellationToken);
            using var document = JsonDocument.Parse(await readmeResponse.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var encodedContent = ReadString(root, "content");
            return string.IsNullOrWhiteSpace(encodedContent)
                ? null
                : Encoding.UTF8.GetString(Convert.FromBase64String(
                    encodedContent.Replace("\r", string.Empty, StringComparison.Ordinal)
                        .Replace("\n", string.Empty, StringComparison.Ordinal)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            return null;
        }
    }

    private async Task<ThemeReadmePreview> ReadThemePreviewAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SourceTimeout);
        // 优先 raw README（不走 api 核心配额、可走国内镜像），失败再回退 api /readme。
        var readme = await ReadReadmeTextAsync(repository, timeout.Token);
        if (readme is null)
        {
            return new ThemeReadmePreview(null, null, "仓库 README 中没有可读取的内容，因此没有主题预览图。");
        }

        var candidates = EnumerateReadmeImageUrls(readme)
            .Select(url => ResolveReadmeImageUrl(url, $"https://raw.githubusercontent.com/{repository.Owner}/{repository.Name}/HEAD/README.md"))
            .Where(static url => url is not null)
            .Cast<Uri>()
            .DistinctBy(static uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static uri => ThemeImageRank(uri.AbsoluteUri))
            .ToArray();
        if (candidates.Length == 0)
        {
            return new ThemeReadmePreview(null, null, "仓库 README 中没有图片，暂时无法提供主题预览。");
        }

        foreach (var candidate in candidates)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (candidate.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var bytes = await TryDownloadImageAsync(candidate, timeout.Token);
                if (bytes.Length > 0)
                {
                    return new ThemeReadmePreview(bytes, candidate.AbsoluteUri, "预览图来自该主题仓库的 README。");
                }
            }
            catch (HttpRequestException)
            {
                // Try the next README image instead of failing the whole preview.
            }
            catch (InvalidDataException)
            {
                // Oversized or empty images are skipped.
            }
        }

        return new ThemeReadmePreview(
            null,
            null,
            "README 中找到了图片，但没有可显示的 PNG/JPG/WebP 预览图（SVG 或超大图片不会载入）。");
    }

    private async Task<byte[]> TryDownloadImageAsync(Uri candidate, CancellationToken cancellationToken)
    {
        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, candidate);
        imageRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("image/*"));
        using var imageResponse = await SendWithMirrorFirstAsync(imageRequest, cancellationToken);
        if (imageResponse.Content.Headers.ContentLength is > MaxThemePreviewBytes)
        {
            throw new InvalidDataException("README 预览图过大。");
        }

        return await ReadBoundedBytesAsync(
            await imageResponse.Content.ReadAsStreamAsync(cancellationToken),
            MaxThemePreviewBytes,
            cancellationToken);
    }

    /// <summary>
    /// 镜像优先的 GitHub 请求（请求头保持不变）：镜像失败时直连一次。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithMirrorFirstAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return await GithubMirror.SendWithMirrorFirstAsync(_httpClient, request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    internal static IReadOnlyList<string> EnumerateReadmeImageUrls(string readme)
    {
        var urls = new List<string>();
        urls.AddRange(MarkdownImage.Matches(readme)
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value.Trim())));
        urls.AddRange(HtmlImage.Matches(readme)
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value.Trim())));
        return urls.Where(static url => !string.IsNullOrWhiteSpace(url)).ToArray();
    }

    internal static Uri? ResolveReadmeImageUrl(string value, string? readmeDownloadUrl)
    {
        var normalized = value.Trim().Trim('<', '>');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = "https:" + normalized;
        }

        Uri? resolved;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute))
        {
            resolved = absolute;
        }
        else if (Uri.TryCreate(readmeDownloadUrl, UriKind.Absolute, out var readmeUri)
            && Uri.TryCreate(readmeUri, normalized, out var relative))
        {
            resolved = relative;
        }
        else
        {
            return null;
        }

        if (resolved.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (string.Equals(resolved.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = resolved.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 5 && string.Equals(segments[2], "blob", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(
                    $"https://raw.githubusercontent.com/{segments[0]}/{segments[1]}/{string.Join('/', segments.Skip(3))}");
            }
        }

        return resolved;
    }

    private static int ThemeImageRank(string url)
    {
        var rank = 0;
        if (url.Contains("preview", StringComparison.OrdinalIgnoreCase)
            || url.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
            || url.Contains("theme", StringComparison.OrdinalIgnoreCase)
            || url.Contains("demo", StringComparison.OrdinalIgnoreCase))
        {
            rank += 4;
        }

        if (url.Contains("badge", StringComparison.OrdinalIgnoreCase)
            || url.Contains("shields.io", StringComparison.OrdinalIgnoreCase))
        {
            rank -= 6;
        }

        return rank;
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > maximumBytes)
            {
                throw new InvalidDataException("README 预览图过大。");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return memory.ToArray();
    }

    private async Task<MarketplaceVerificationResult?> FindPluginInRepositoryTreeAsync(
        GitHubRepository repository,
        string branch,
        CancellationToken cancellationToken,
        ManagerInstance? instance = null)
    {
        try
        {
            var treeUri = new Uri(
                $"https://api.github.com/repos/{repository.Owner}/{repository.Name}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1");
            var treeJson = await GetStringWithMirrorAsync(treeUri, cancellationToken);
            using var document = JsonDocument.Parse(treeJson);
            if (!document.RootElement.TryGetProperty("tree", out var tree)
                || tree.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var packagePaths = tree.EnumerateArray()
                .Where(entry => string.Equals(ReadString(entry, "type"), "blob", StringComparison.OrdinalIgnoreCase))
                .Select(entry => ReadString(entry, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path)
                    && path.EndsWith("/package.json", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .OrderBy(path => RepositoryPackagePathRank(path))
                .ThenBy(path => path.Count(character => character == '/'))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray();

            foreach (var packagePath in packagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var encodedPath = string.Join(
                    "/",
                    packagePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
                var rawUri = new Uri(
                    $"https://raw.githubusercontent.com/{repository.Owner}/{repository.Name}/{Uri.EscapeDataString(branch)}/{encodedPath}");
                try
                {
                    var json = await GetStringAsync(rawUri, cancellationToken);
                    using var packageDocument = JsonDocument.Parse(json);
                    var manifest = packageDocument.RootElement;
                    if (!HasDshBundlePatch(manifest))
                    {
                        continue;
                    }

                    var directory = packagePath[..^"/package.json".Length];
                    var installSpec = $"github:{repository.Owner}/{repository.Name}#path:/{directory}";
                    var verification = VerifyManifest(
                        manifest,
                        ReadString(manifest, "name"),
                        ReadString(manifest, "version"),
                        installSpec,
                        instance);
                    if (verification.Status == MarketplaceVerificationStatus.Verified)
                    {
                        return verification with
                        {
                            Message = $"已在仓库子目录 /{directory} 找到有效的 DSh Plugin package.json。"
                        };
                    }
                }
                catch (HttpRequestException)
                {
                    // A stale tree entry must not prevent checking the next candidate.
                }
                catch (JsonException)
                {
                    // Ignore unrelated or malformed package manifests in a monorepo.
                }
            }
        }
        catch (HttpRequestException)
        {
            // GitHub tree discovery is a fallback after the normal package path.
        }
        catch (JsonException)
        {
            // A malformed tree response falls back to the normal validation error.
        }

        return null;
    }

    private static int RepositoryPackagePathRank(string path)
    {
        var normalized = path.ToLowerInvariant();
        if (normalized.Contains("plugin", StringComparison.Ordinal)
            || normalized.Contains("theme", StringComparison.Ordinal))
        {
            return 0;
        }

        return normalized.StartsWith("packages/", StringComparison.Ordinal)
            || normalized.StartsWith("plugins/", StringComparison.Ordinal)
            || normalized.StartsWith("apps/", StringComparison.Ordinal)
            ? 1
            : 2;
    }

    internal static MarketplaceVerificationResult VerifyManifest(
        JsonElement manifest,
        string? packageName,
        string? version,
        string? installSpec,
        ManagerInstance? instance = null)
    {
        if (!HasDshBundlePatch(manifest))
        {
            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Rejected,
                "package.json 没有 dsh.bundle.patch，这个项目不能按 DSh Plugin 安装。",
                packageName,
                version,
                installSpec);
        }

        var hasEntry = HasString(manifest, "main")
            || HasString(manifest, "module")
            || HasString(manifest, "exports")
            || HasDshClient(manifest);
        if (!hasEntry)
        {
            return new MarketplaceVerificationResult(
                MarketplaceVerificationStatus.Rejected,
                "package.json 声明了 DSh bundle，但没有找到可加载入口（main、module、exports 或 dsh.client）。",
                packageName,
                version,
                installSpec);
        }

        // 兼容性预检（借鉴上游 v1.1.2 F-06）：核心 peerDependencies 与实例实际版本不符时
        // 明确警告——“装得上、一启动就崩”的插件（如 computer-user 依赖已被移除的 dsh-settings 导出）
        // 正是靠这一层拦住。
        if (instance is not null)
        {
            var issues = PluginCompatibility.Check(manifest.GetRawText(), instance);
            if (issues.Count > 0)
            {
                return new MarketplaceVerificationResult(
                    MarketplaceVerificationStatus.Incompatible,
                    $"声明的核心依赖与当前实例的 DSh 运行时不兼容：{PluginCompatibility.Describe(issues)}。安装/更新后可能导致实例无法启动。",
                    packageName,
                    version,
                    installSpec);
            }
        }

        return new MarketplaceVerificationResult(
            MarketplaceVerificationStatus.Verified,
            "已读取 package.json，确认它声明了 DSh bundle 和可加载入口。",
            packageName,
            version,
            installSpec);
    }

    private async Task<IReadOnlyList<(bool IsFile, string Value)>> ReadCustomSourcesAsync(CancellationToken cancellationToken)
    {
        var result = new List<(bool, string)>();
        foreach (var source in _customSources)
        {
            result.Add((false, source.ToString()));
        }

        if (File.Exists(_paths.MarketplaceCatalogPath))
        {
            result.Add((true, _paths.MarketplaceCatalogPath));
        }

        if (File.Exists(_paths.MarketplaceSourcesPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_paths.MarketplaceSourcesPath, Encoding.UTF8));
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in document.RootElement.EnumerateArray())
                    {
                        // 兼容两种写法：纯字符串（旧）或 {"value":…,"enabled":false}（带开关）。
                        var value = entry.ValueKind == JsonValueKind.String
                            ? entry.GetString()
                            : entry.ValueKind == JsonValueKind.Object
                                && entry.TryGetProperty("value", out var valueElement)
                                && valueElement.ValueKind == JsonValueKind.String
                                    ? valueElement.GetString()
                                    : null;
                        var enabled = !entry.TryGetProperty("enabled", out var enabledElement)
                            || enabledElement.ValueKind != JsonValueKind.False;
                        if (string.IsNullOrWhiteSpace(value) || !enabled)
                        {
                            continue;
                        }

                        var trimmed = value.Trim();
                        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                        {
                            result.Add((false, uri.ToString()));
                        }
                        else if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add((true, trimmed));
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // The normal source results remain useful when custom settings are malformed.
            }
        }

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SourceTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, timeout.Token);
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    /// <summary>
    /// GitHub 主机（api/raw/codeload）的抓取：镜像优先，失败回退直连一次。
    /// 注意读取超时抛 OperationCanceledException，与 HttpRequestException 一并回退。
    /// </summary>
    private async Task<string> GetStringWithMirrorAsync(Uri uri, CancellationToken cancellationToken)
    {
        var mirror = GithubMirror.TryMirror(uri);
        if (mirror is not null)
        {
            try
            {
                return await GetStringAsync(mirror, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // 镜像失败/超时：回退直连。
            }
        }

        return await GetStringAsync(uri, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DSH-Launcher", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static MarketplaceItem? ParseCatalogEntry(
        JsonElement entry,
        MarketplaceSourceKind sourceKind,
        string sourceName)
    {
        var name = ReadString(entry, "name")
            ?? ReadString(entry, "title")
            ?? ReadString(entry, "slug");
        var packageName = ReadString(entry, "packageName")
            ?? ReadString(entry, "package")
            ?? ReadString(entry, "npm");
        var repository = ReadRepositoryUrl(entry);
        var rawCatalogUrl = ReadString(entry, "url")?.Trim();
        var dshMarketUrl = sourceKind is MarketplaceSourceKind.CommunityCatalog or MarketplaceSourceKind.ZhCatalog
            && Uri.TryCreate(rawCatalogUrl, UriKind.Absolute, out var catalogUri)
            && (string.Equals(catalogUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(catalogUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                ? rawCatalogUrl
                : null;
        repository ??= NormalizeRepositoryUrl(dshMarketUrl);
        var rawInstallSpec = ReadString(entry, "installSpec")
            ?? ReadString(entry, "install")
            ?? (IsSafePackageName(packageName) ? packageName : null)
            ?? repository;
        var installSpec = NormalizeInstallSpec(rawInstallSpec ?? string.Empty);
        repository ??= GetGitHubRepositoryUrl(installSpec);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installSpec))
        {
            return null;
        }

        if (sourceKind == MarketplaceSourceKind.Official
            && !IsExplicitOfficialPackage(packageName, repository))
        {
            return null;
        }

        var category = ReadString(entry, "category")
            ?? ReadStringArray(entry, "categories").FirstOrDefault()
            ?? ReadStringArray(entry, "tags").FirstOrDefault()
            ?? "未分类";
        var idTarget = packageName ?? repository ?? installSpec;
        var item = new MarketplaceItem(
            $"{sourceKind}:{idTarget}",
            name,
            IsSafePackageName(packageName) ? packageName : null,
            ReadString(entry, "version") ?? ReadString(entry, "latestVersion"),
            ReadDescription(entry) ?? "目录未提供说明。",
            installSpec,
            repository,
            category,
            sourceKind,
            sourceName,
            MarketplaceVerificationStatus.Unverified,
            sourceKind is MarketplaceSourceKind.CommunityCatalog or MarketplaceSourceKind.ZhCatalog
                ? "GitHub / 中文官网来源只用于发现，安装前会读取 package.json。"
                : "自定义目录只用于发现，安装前会读取 package.json。",
            false,
            false,
            false,
            ReadInt64(entry, "stars") ?? ReadInt64(entry, "starCount") ?? ReadInt64(entry, "stargazers_count"),
            ReadDateTimeOffset(entry, "publishedAt")
                ?? ReadDateTimeOffset(entry, "published_at")
                ?? ReadDateTimeOffset(entry, "releaseDate"),
            DshMarketUrl: dshMarketUrl);
        return item;
    }

    private static string? ReadDescription(JsonElement entry)
    {
        if (!entry.TryGetProperty("description", out var description))
        {
            return null;
        }

        if (description.ValueKind == JsonValueKind.String)
        {
            return description.GetString();
        }

        if (description.ValueKind == JsonValueKind.Object)
        {
            return ReadString(description, "zh") ?? ReadString(description, "en");
        }

        return null;
    }

    private static string? ReadRepositoryUrl(JsonElement element)
    {
        if (element.TryGetProperty("repository", out var repository))
        {
            if (repository.ValueKind == JsonValueKind.String)
            {
                return NormalizeRepositoryUrl(repository.GetString());
            }

            if (repository.ValueKind == JsonValueKind.Object)
            {
                var url = ReadString(repository, "url") ?? ReadString(repository, "directory");
                var normalized = NormalizeRepositoryUrl(url);
                if (normalized is not null)
                {
                    return normalized;
                }
            }
        }

        var github = ReadString(element, "github") ?? ReadString(element, "repo");
        return NormalizeRepositoryUrl(github);
    }

    private static string? NormalizeRepositoryUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimEnd('/');
        if (normalized.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["github:".Length..];
        }

        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var repositoryUri)
                && string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                var segments = repositoryUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2
                    || !IsGitHubOwner(segments[0])
                    || !IsGitHubRepositoryName(segments[1]))
                {
                    return null;
                }
            }

            return normalized;
        }

        // Scoped npm package names also contain exactly one slash, but
        // @scope/package is not a GitHub owner/repository shorthand.
        if (normalized.StartsWith('@'))
        {
            return null;
        }

        return normalized.Count(character => character == '/') == 1
            && !normalized.Contains(' ')
            ? $"https://github.com/{normalized}"
            : null;
    }

    private static bool Matches(MarketplaceItem item, string query) =>
        item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.PackageName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
        || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
        || item.SourceText.Contains(query, StringComparison.OrdinalIgnoreCase);

    private MarketplaceCacheDocument? TryReadCache()
    {
        if (!File.Exists(_paths.MarketplaceCachePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MarketplaceCacheDocument>(
                File.ReadAllText(_paths.MarketplaceCachePath, Encoding.UTF8),
                CacheJsonOptions);
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

    private void TryWriteCache(
        IReadOnlyList<MarketplaceItem> items,
        int sourcesChecked,
        DateTimeOffset retrievedAt,
        ICollection<string> warnings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_paths.MarketplaceCachePath)
                ?? throw new InvalidOperationException("插件市场缓存没有父目录。 ");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_paths.MarketplaceCachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var document = new MarketplaceCacheDocument(items.ToArray(), sourcesChecked, retrievedAt);
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(document, CacheJsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, _paths.MarketplaceCachePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"插件市场缓存无法保存：{ex.Message}");
        }
    }

    public static IReadOnlyList<MarketplaceItem> MergeItems(IEnumerable<MarketplaceItem> items)
    {
        var merged = new List<MarketplaceItem?>();
        var identitiesByIndex = new List<HashSet<string>?>();
        var identityIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in items)
        {
            var item = NormalizeSourceKind(raw) with { Category = NormalizeCategory(raw.Category) };
            var itemIdentities = new HashSet<string>(GetPluginIdentities(item), StringComparer.OrdinalIgnoreCase);
            var matchingIndexes = itemIdentities
                .Select(identity => identityIndex.TryGetValue(identity, out var index) ? index : -1)
                .Where(index => index >= 0 && merged[index] is not null)
                .Distinct()
                .Order()
                .ToArray();
            if (matchingIndexes.Length == 0)
            {
                var index = merged.Count;
                merged.Add(item);
                identitiesByIndex.Add(itemIdentities);
                foreach (var identity in itemIdentities)
                {
                    identityIndex[identity] = index;
                }

                continue;
            }

            var combined = matchingIndexes
                .Select(index => merged[index]!)
                .Aggregate(MergeTwoItems);
            var combinedIdentities = new HashSet<string>(itemIdentities, StringComparer.OrdinalIgnoreCase);
            foreach (var index in matchingIndexes)
            {
                if (identitiesByIndex[index] is { } identities)
                {
                    combinedIdentities.UnionWith(identities);
                }

                merged[index] = null;
                identitiesByIndex[index] = null;
            }

            var mergedItem = MergeTwoItems(combined, item);
            combinedIdentities.UnionWith(GetPluginIdentities(mergedItem));
            var mergedIndex = merged.Count;
            merged.Add(mergedItem);
            identitiesByIndex.Add(combinedIdentities);
            foreach (var identity in combinedIdentities)
            {
                identityIndex[identity] = mergedIndex;
            }
        }

        return merged.Where(item => item is not null).Select(item => item!).ToArray();
    }

    private static MarketplaceItem NormalizeSourceKind(MarketplaceItem item)
    {
        if (item.SourceKind != MarketplaceSourceKind.Official
            || IsExplicitOfficialPackage(item.PackageName, item.RepositoryUrl))
        {
            return item;
        }

        return item with
        {
            SourceKind = MarketplaceSourceKind.Custom,
            SourceName = "历史目录缓存",
            VerificationStatus = MarketplaceVerificationStatus.Unverified,
            VerificationMessage = "历史缓存不能证明这是 DSh 官方 Plugin，安装前仍会重新检查。"
        };
    }

    private static MarketplaceItem MergeTwoItems(MarketplaceItem first, MarketplaceItem second)
    {
        var primary = SourceRank(first.SourceKind) >= SourceRank(second.SourceKind) ? first : second;
        var secondary = ReferenceEquals(primary, first) ? second : first;
        var sources = new[] { SourceTextFor(first), SourceTextFor(second) }
            .SelectMany(value => value.Split(new[] { " / " }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceKinds = new[] { first, second }
            .SelectMany(item => item.MergedSourceKinds is { Count: > 0 }
                ? item.MergedSourceKinds
                : new[] { item.SourceKind })
            .Distinct()
            .ToArray();
        var version = PickVersion(first.Version, second.Version);
        var installedVersion = first.InstalledVersion ?? second.InstalledVersion;
        var isInstalled = first.IsInstalled || second.IsInstalled;
        var updateStatus = isInstalled
            ? GetUpdateStatus(version, installedVersion)
            : MarketplaceUpdateStatus.Unknown;

        return primary with
        {
            Name = PickText(primary.Name, secondary.Name),
            PackageName = primary.PackageName ?? secondary.PackageName,
            Version = version,
            Description = PickDescription(primary.Description, secondary.Description),
            InstallSpec = PickText(primary.InstallSpec, secondary.InstallSpec),
            RepositoryUrl = primary.RepositoryUrl ?? secondary.RepositoryUrl,
            Category = !string.Equals(primary.Category, "未分类", StringComparison.OrdinalIgnoreCase)
                ? primary.Category
                : secondary.Category,
            VerificationStatus = VerificationRank(first.VerificationStatus) >= VerificationRank(second.VerificationStatus)
                ? first.VerificationStatus
                : second.VerificationStatus,
            VerificationMessage = VerificationRank(first.VerificationStatus) >= VerificationRank(second.VerificationStatus)
                ? first.VerificationMessage
                : second.VerificationMessage,
            IsInstalled = isInstalled,
            IsManaged = first.IsManaged || second.IsManaged,
            CanMutate = first.CanMutate || second.CanMutate,
            CanInstallOrUpdate = first.CanInstallOrUpdate || second.CanInstallOrUpdate,
            Stars = PickStars(first.Stars, second.Stars),
            PublishedAt = first.PublishedAt >= second.PublishedAt ? first.PublishedAt : second.PublishedAt,
            InstalledVersion = installedVersion,
            UpdateStatus = updateStatus,
            MergedSourceText = sources.Length > 1 ? string.Join(" / ", sources) : null,
            MergedSourceKinds = sourceKinds.Length > 1 ? sourceKinds : null,
            DshMarketUrl = primary.DshMarketUrl ?? secondary.DshMarketUrl
        };
    }

    private static bool HasSourceKind(MarketplaceItem item, MarketplaceSourceKind sourceKind) =>
        item.SourceKind == sourceKind
        || (item.MergedSourceKinds?.Contains(sourceKind) ?? false);

    private static string PickText(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;

    private static string PickDescription(string first, string second)
    {
        var firstPlaceholder = first.Contains("未提供", StringComparison.OrdinalIgnoreCase)
            || first.Contains("未说明", StringComparison.OrdinalIgnoreCase);
        var secondPlaceholder = second.Contains("未提供", StringComparison.OrdinalIgnoreCase)
            || second.Contains("未说明", StringComparison.OrdinalIgnoreCase);
        return firstPlaceholder && !secondPlaceholder ? second : first;
    }

    private static long? PickStars(long? first, long? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return Math.Max(first.Value, second.Value);
    }

    private static string? PickVersion(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        if (TryParseVersion(first, out var firstVersion) && TryParseVersion(second, out var secondVersion))
        {
            return secondVersion > firstVersion ? second : first;
        }

        return first;
    }

    private static int SourceRank(MarketplaceSourceKind sourceKind) => sourceKind switch
    {
        MarketplaceSourceKind.Official => 5,
        MarketplaceSourceKind.CommunityCatalog => 4,
        MarketplaceSourceKind.ZhCatalog => 4,
        MarketplaceSourceKind.Custom => 3,
        MarketplaceSourceKind.GitHubTopic => 2,
        _ => 1
    };

    private static string SourceTextFor(MarketplaceItem item) => item.MergedSourceText ?? (item.SourceKind switch
    {
        MarketplaceSourceKind.Official => "DSh 官方",
        MarketplaceSourceKind.CommunityCatalog => "GitHub",
        MarketplaceSourceKind.ZhCatalog => "中文官网",
        MarketplaceSourceKind.GitHubTopic => "GitHub 发现",
        _ => item.SourceName
    });

    private static int MatchRank(MarketplaceItem item, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        var normalized = query.Trim();
        var values = new[] { item.Name, item.PackageName, item.InstallSpec };
        if (values.Any(value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))) return 5;
        if (values.Any(value => value?.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) == true)) return 4;
        if (values.Any(value => value?.Contains(normalized, StringComparison.OrdinalIgnoreCase) == true)) return 3;
        if (item.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    private static IEnumerable<string> EnumeratePluginIdentities(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var text = NormalizeInstallSpec(value);
        if (text.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["npm:".Length..];
        }

        if (TryGetGitHubIdentity(text, out var githubIdentity))
        {
            yield return githubIdentity;
            yield break;
        }

        if (IsSafePackageName(text))
        {
            yield return $"npm:{text.ToLowerInvariant()}";
        }
    }

    private static bool TryGetGitHubIdentity(string value, out string identity)
    {
        identity = string.Empty;
        var text = NormalizeInstallSpec(value);
        if (text.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["github:".Length..];
        }

        string? owner = null;
        string? repository = null;
        string? subpath = null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                owner = segments[0];
                repository = segments[1];
                if (segments.Length >= 5 && (segments[2] is "tree" or "blob"))
                {
                    subpath = string.Join('/', segments.Skip(4));
                }

                if (string.IsNullOrWhiteSpace(subpath) && uri.Fragment.StartsWith("#path:", StringComparison.OrdinalIgnoreCase))
                {
                    subpath = uri.Fragment["#path:".Length..];
                }
            }
        }
        else
        {
            var pathMarker = text.IndexOf("#path:", StringComparison.OrdinalIgnoreCase);
            var repositoryText = pathMarker >= 0 ? text[..pathMarker] : text;
            subpath = pathMarker >= 0 ? text[(pathMarker + "#path:".Length)..] : null;
            var segments = repositoryText.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2)
            {
                owner = segments[0];
                repository = segments[1];
            }
        }

        if (!IsGitHubOwner(owner) || !IsGitHubRepositoryName(repository))
        {
            return false;
        }

        var repositoryName = repository!;
        repositoryName = repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repositoryName[..^4]
            : repositoryName;
        var baseIdentity = $"github:{owner}/{repositoryName}".ToLowerInvariant();
        identity = string.IsNullOrWhiteSpace(subpath)
            ? baseIdentity
            : $"{baseIdentity}#path:/{subpath.Trim('/')}";
        return true;
    }

    private static IReadOnlySet<string> GetPluginIdentities(
        string? packageName,
        string? name,
        string? installSpec,
        string? repositoryUrl)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { packageName, installSpec, repositoryUrl })
        {
            identities.UnionWith(EnumeratePluginIdentities(value));
        }

        // GitHub install specs are saved by DSh under the package name declared
        // by the repository. Include a package-like display name as an alias so
        // an installed GitHub plugin is recognized immediately after refresh.
        identities.UnionWith(EnumeratePluginIdentities(name));

        if (identities.Count == 0 && !string.IsNullOrWhiteSpace(name))
        {
            identities.Add($"name:{name.Trim().ToLowerInvariant()}");
        }

        return identities;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = NormalizeVersionText(value);
        return Version.TryParse(normalized, out version!);
    }

    private static string NormalizeVersionText(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V', '^', '~', '>', '<', '=');
        normalized = normalized.Split(new[] { '-', '+' }, 2)[0];
        return normalized;
    }

    private static int VerificationRank(MarketplaceVerificationStatus status) => status switch
    {
        MarketplaceVerificationStatus.Verified => 3,
        MarketplaceVerificationStatus.Unverified => 2,
        _ => 1
    };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)
            ? number
            : null;
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

    private static string? ReadValueString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

    private static bool HasDshBundlePatch(JsonElement manifest) =>
        manifest.TryGetProperty("dsh.bundle.patch", out _)
        || manifest.TryGetProperty("dsh", out var dsh)
            && dsh.ValueKind == JsonValueKind.Object
            && dsh.TryGetProperty("bundle", out var bundle)
            && bundle.ValueKind == JsonValueKind.Object
            && bundle.TryGetProperty("patch", out _);

    private static bool IsExplicitOfficialPackage(string? packageName, string? repositoryUrl)
    {
        if (!string.IsNullOrWhiteSpace(packageName)
            && packageName.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryGetGitHubIdentity(repositoryUrl ?? string.Empty, out var identity)
            && identity.StartsWith("github:deepseek-ai/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDshClient(JsonElement manifest) =>
        manifest.TryGetProperty("dsh.client", out _)
        || manifest.TryGetProperty("dsh", out var dsh)
            && dsh.ValueKind == JsonValueKind.Object
            && dsh.TryGetProperty("client", out _);

    private static bool HasString(JsonElement manifest, string propertyName) =>
        manifest.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array;

    private static bool IsSafePackageName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 214 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return value.All(character => char.IsLetterOrDigit(character) || character is '@' or '/' or '-' or '_' or '.' or '~');
    }

    private static MarketplaceVerificationResult Rejected(MarketplaceItem item, string message, string? installSpec = null) =>
        new(MarketplaceVerificationStatus.Rejected, message, item.PackageName, item.Version, installSpec ?? item.InstallSpec);

    private static bool IsWithinPath(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

    private static bool TryGetGitHubRepository(string url, out GitHubRepository repository)
    {
        repository = default;
        var text = NormalizeInstallSpec(url);
        if (text.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["github:".Length..];
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            var pathMarker = text.IndexOf("#path:", StringComparison.OrdinalIgnoreCase);
            var repositoryText = pathMarker >= 0 ? text[..pathMarker] : text;
            var repositorySegments = repositoryText.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (repositorySegments.Length != 2
                || !IsGitHubOwner(repositorySegments[0])
                || !IsGitHubRepositoryName(repositorySegments[1]))
            {
                return false;
            }

            var pathSubpath = pathMarker >= 0 ? text[(pathMarker + "#path:".Length)..].Trim('/') : null;
            repository = new GitHubRepository(
                repositorySegments[0],
                repositorySegments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? repositorySegments[1][..^4]
                    : repositorySegments[1],
                null,
                pathSubpath);
            return true;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !IsGitHubOwner(segments[0])
            || !IsGitHubRepositoryName(segments[1]))
        {
            return false;
        }

        var branch = default(string);
        var subpath = default(string);
        if (segments.Length >= 4 && (segments[2] is "tree" or "blob"))
        {
            branch = segments[3];
            subpath = segments.Length > 4
                ? string.Join('/', segments.Skip(4))
                : null;
        }
        else if (uri.Fragment.StartsWith("#path:", StringComparison.OrdinalIgnoreCase))
        {
            subpath = uri.Fragment["#path:".Length..].Trim('/');
        }

        repository = new GitHubRepository(segments[0], segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1],
            branch,
            subpath);
        return true;
    }

    private static bool IsGitHubOwner(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 39
            || value[0] == '-'
            || value[^1] == '-')
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static bool IsGitHubRepositoryName(string? value)
    {
        var normalized = value?.EndsWith(".git", StringComparison.OrdinalIgnoreCase) == true
            ? value[..^4]
            : value;
        return !string.IsNullOrWhiteSpace(normalized)
            && normalized is not "." and not ".."
            && normalized.Length <= 100
            && normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static string NormalizeInstallSpec(string value)
    {
        var trimmed = value.Trim();
        var tokens = SplitCommandArguments(trimmed);
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (tokens[index].Equals("add", StringComparison.OrdinalIgnoreCase)
                || tokens[index].Equals("update", StringComparison.OrdinalIgnoreCase)
                || tokens[index].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                var target = tokens[index + 1].Trim();
                if (!target.StartsWith("-", StringComparison.Ordinal))
                {
                    return target;
                }
            }
        }

        return trimmed;
    }

    private static IReadOnlyList<string> SplitCommandArguments(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (var character in value)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string? GetGitHubRepositoryUrl(string value)
    {
        return TryGetGitHubRepository(value, out var repository)
            ? $"https://github.com/{repository.Owner}/{repository.Name}"
            : null;
    }

    private sealed record MarketplaceCacheDocument(
        MarketplaceItem[] Items,
        int SourcesChecked,
        DateTimeOffset RetrievedAt);

    private readonly record struct GitHubRepository(
        string Owner,
        string Name,
        string? Branch,
        string? Subpath);
}
