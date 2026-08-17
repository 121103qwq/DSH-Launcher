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
    public const string GitHubTopicUrl = "https://api.github.com/search/repositories?q=topic%3Adsh-plugin&per_page=50";

    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);
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
    private readonly IReadOnlyList<Uri> _customSources;
    private readonly Dictionary<string, ThemeReadmePreview> _themePreviewCache = new(StringComparer.OrdinalIgnoreCase);

    public MarketplaceService(
        LauncherPaths? paths = null,
        HttpClient? httpClient = null,
        IEnumerable<Uri>? customSources = null)
    {
        _paths = paths ?? new LauncherPaths();
        _httpClient = httpClient ?? CreateHttpClient();
        _customSources = customSources?.Where(uri => uri.IsAbsoluteUri).ToArray() ?? Array.Empty<Uri>();
    }

    public async Task<MarketplaceSearchResult> SearchAsync(
        ManagerInstance? instance,
        string? query = null,
        CancellationToken cancellationToken = default,
        MarketplaceSourceKind? sourceKind = null,
        MarketplaceSortOrder sortOrder = MarketplaceSortOrder.Relevance)
    {
        var items = new List<MarketplaceItem>();
        var warnings = new List<string>();
        var sourcesChecked = 0;

        var sourceTasks = new[]
        {
            LoadRemoteCatalogAsync(new Uri(CommunityCatalogUrl), MarketplaceSourceKind.CommunityCatalog, "社区目录", cancellationToken),
            LoadGitHubTopicAsync(cancellationToken)
        };

        foreach (var task in sourceTasks)
        {
            sourcesChecked++;
            try
            {
                items.AddRange(await task);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add("一个插件来源响应超时，已跳过。");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
            {
                warnings.Add($"一个插件来源暂时无法读取：{ex.Message}");
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
            mergedItems = MergeItems(cached.Items);
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

    public static string NormalizeCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "未分类";
        }

        var normalized = value.Trim().ToLowerInvariant();
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

        return value.Trim();
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
        GetPluginIdentities(entry.Name, entry.Name, entry.Name, null);

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
        CancellationToken cancellationToken = default)
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
            return await VerifyGitHubRepositoryAsync(item, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(item.PackageName))
        {
            return await VerifyNpmPackageAsync(item, cancellationToken);
        }

        return new MarketplaceVerificationResult(
            MarketplaceVerificationStatus.Rejected,
            "没有找到 npm 包名或 GitHub 仓库地址。",
            null,
            null,
            null);
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

        return directory;
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

    private async Task<IReadOnlyList<MarketplaceItem>> LoadGitHubTopicAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubTopicUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub 搜索结果没有 items 数组。");
        }

        return items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item =>
            {
                var fullName = ReadString(item, "full_name");
                var repositoryUrl = ReadString(item, "html_url");
                if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(repositoryUrl))
                {
                    return null;
                }

                var topics = ReadStringArray(item, "topics");
                return new MarketplaceItem(
                    $"github:{fullName}",
                    ReadString(item, "name") ?? fullName,
                    null,
                    null,
                    ReadString(item, "description") ?? "GitHub dsh-plugin 主题发现的候选项目。",
                    repositoryUrl,
                    repositoryUrl,
                    topics.FirstOrDefault(topic => !string.Equals(topic, "dsh-plugin", StringComparison.OrdinalIgnoreCase)) ?? "未分类",
                    MarketplaceSourceKind.GitHubTopic,
                    "GitHub dsh-plugin 标签",
                    MarketplaceVerificationStatus.Unverified,
                    "GitHub 标签只用于发现，安装前会读取仓库 package.json。",
                    false,
                    false,
                    false,
                    ReadInt64(item, "stargazers_count"),
                    ReadDateTimeOffset(item, "published_at") ?? ReadDateTimeOffset(item, "created_at"));
            })
            .Where(item => item is not null)
            .Cast<MarketplaceItem>()
            .ToArray();
    }

    private async Task<MarketplaceVerificationResult> VerifyNpmPackageAsync(
        MarketplaceItem item,
        CancellationToken cancellationToken)
    {
        var packageName = item.PackageName!.Trim();
        if (!IsSafePackageName(packageName))
        {
            return Rejected(item, "npm 包名格式不正确。");
        }

        var encodedName = packageName.Replace("/", "%2f", StringComparison.Ordinal);
        var uri = new Uri($"https://registry.npmjs.org/{encodedName}");
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

        return VerifyManifest(packageManifest, packageName, version, packageName);
    }

    private async Task<MarketplaceVerificationResult> VerifyGitHubRepositoryAsync(
        MarketplaceItem item,
        CancellationToken cancellationToken)
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
                var json = await GetStringAsync(uri, cancellationToken);
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
                    cancellationToken);
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

    private async Task<ThemeReadmePreview> ReadThemePreviewAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SourceTimeout);
        var readmeUri = new Uri($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/readme");
        using var readmeRequest = new HttpRequestMessage(HttpMethod.Get, readmeUri);
        using var readmeResponse = await SendAsync(readmeRequest, timeout.Token);
        using var document = JsonDocument.Parse(await readmeResponse.Content.ReadAsStringAsync(timeout.Token));
        var root = document.RootElement;
        var encodedContent = ReadString(root, "content");
        var downloadUrl = ReadString(root, "download_url");
        if (string.IsNullOrWhiteSpace(encodedContent))
        {
            return new ThemeReadmePreview(null, null, "仓库 README 中没有可读取的内容，因此没有主题预览图。");
        }

        var readme = Encoding.UTF8.GetString(Convert.FromBase64String(
            encodedContent.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)));
        var candidates = EnumerateReadmeImageUrls(readme)
            .Select(url => ResolveReadmeImageUrl(url, downloadUrl))
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
                using var imageRequest = new HttpRequestMessage(HttpMethod.Get, candidate);
                imageRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("image/*"));
                using var imageResponse = await SendAsync(imageRequest, timeout.Token);
                if (imageResponse.Content.Headers.ContentLength is > MaxThemePreviewBytes)
                {
                    continue;
                }

                var bytes = await ReadBoundedBytesAsync(
                    await imageResponse.Content.ReadAsStreamAsync(timeout.Token),
                    MaxThemePreviewBytes,
                    timeout.Token);
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
        CancellationToken cancellationToken)
    {
        try
        {
            var treeUri = new Uri(
                $"https://api.github.com/repos/{repository.Owner}/{repository.Name}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1");
            using var request = new HttpRequestMessage(HttpMethod.Get, treeUri);
            using var response = await SendAsync(request, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
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
                        installSpec);
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

    private static MarketplaceVerificationResult VerifyManifest(
        JsonElement manifest,
        string? packageName,
        string? version,
        string? installSpec)
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
                        if (entry.ValueKind == JsonValueKind.String
                            && Uri.TryCreate(entry.GetString(), UriKind.Absolute, out var uri)
                            && uri.Scheme == Uri.UriSchemeHttps)
                        {
                            result.Add((false, uri.ToString()));
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
        return new MarketplaceItem(
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
            sourceKind == MarketplaceSourceKind.CommunityCatalog
                ? "社区目录只用于发现，安装前会读取 package.json。"
                : "自定义目录只用于发现，安装前会读取 package.json。",
            false,
            false,
            false,
            ReadInt64(entry, "stars") ?? ReadInt64(entry, "starCount") ?? ReadInt64(entry, "stargazers_count"),
            ReadDateTimeOffset(entry, "publishedAt")
                ?? ReadDateTimeOffset(entry, "published_at")
                ?? ReadDateTimeOffset(entry, "releaseDate"));
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
            return normalized;
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
            Stars = PickStars(first.Stars, second.Stars),
            PublishedAt = first.PublishedAt >= second.PublishedAt ? first.PublishedAt : second.PublishedAt,
            InstalledVersion = installedVersion,
            UpdateStatus = updateStatus,
            MergedSourceText = sources.Length > 1 ? string.Join(" / ", sources) : null,
            MergedSourceKinds = sourceKinds.Length > 1 ? sourceKinds : null
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
        MarketplaceSourceKind.Custom => 3,
        MarketplaceSourceKind.GitHubTopic => 2,
        _ => 1
    };

    private static string SourceTextFor(MarketplaceItem item) => item.MergedSourceText ?? (item.SourceKind switch
    {
        MarketplaceSourceKind.Official => "DSh 官方",
        MarketplaceSourceKind.CommunityCatalog => "社区目录",
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

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        repository = repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repository[..^4]
            : repository;
        var baseIdentity = $"github:{owner}/{repository}".ToLowerInvariant();
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
            if (repositorySegments.Length != 2)
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
        if (segments.Length < 2 || string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(segments[1]))
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
