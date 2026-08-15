using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly HttpClient _httpClient;
    private readonly LauncherPaths _paths;
    private readonly IReadOnlyList<Uri> _customSources;

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

        if (instance is not null)
        {
            try
            {
                items.AddRange(ReadOfficialItems(instance));
                sourcesChecked++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                warnings.Add($"官方运行环境：{ex.Message}");
            }
        }

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
        var remoteItems = items
            .Where(item => item.SourceKind != MarketplaceSourceKind.Official)
            .ToArray();
        if (remoteItems.Length > 0)
        {
            TryWriteCache(remoteItems, sourcesChecked, DateTimeOffset.UtcNow, warnings);
        }
        else if (cached is not null)
        {
            items.AddRange(cached.Items);
            sourcesChecked = Math.Max(sourcesChecked, cached.SourcesChecked);
            warnings.Add("在线来源暂时没有返回结果，已显示上次缓存。 ");
        }

        var retrievedAt = DateTimeOffset.UtcNow;
        return new MarketplaceSearchResult(
            FilterAndSort(items, query, sourceKind, sortOrder),
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

        var items = cached.Items.ToList();
        if (instance is not null)
        {
            try
            {
                items.AddRange(ReadOfficialItems(instance));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // Cached remote sources remain usable when the current profile is incomplete.
            }
        }

        return new MarketplaceSearchResult(
            FilterAndSort(items, query, sourceKind, sortOrder),
            new[] { $"正在使用上次缓存（{cached.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm}）。" },
            cached.SourcesChecked,
            cached.RetrievedAt);
    }

    public static IReadOnlyList<MarketplaceItem> FilterAndSort(
        IEnumerable<MarketplaceItem> items,
        string? query = null,
        MarketplaceSourceKind? sourceKind = null,
        MarketplaceSortOrder sortOrder = MarketplaceSortOrder.Relevance)
    {
        var normalizedQuery = query?.Trim();
        var filtered = items
            .Where(item => sourceKind is null || item.SourceKind == sourceKind)
            .Where(item => string.IsNullOrWhiteSpace(normalizedQuery) || Matches(item, normalizedQuery!))
            .GroupBy(GetDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => VerificationRank(item.VerificationStatus))
                .ThenByDescending(item => item.SourceKind == MarketplaceSourceKind.Official)
                .First());

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
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
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

        if (!string.IsNullOrWhiteSpace(item.PackageName))
        {
            return await VerifyNpmPackageAsync(item, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(item.RepositoryUrl))
        {
            return await VerifyGitHubRepositoryAsync(item, cancellationToken);
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
        var files = new[]
        {
            "package.json",
            "pnpm-lock.yaml",
            "package-lock.json",
            "yarn.lock",
            "cordis.patch.yml"
        };
        var existing = files
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
        if (!TryGetGitHubRepository(item.RepositoryUrl!, out var repository))
        {
            return Rejected(item, "GitHub 地址格式不正确，无法定位仓库。", item.RepositoryUrl);
        }

        foreach (var branch in new[] { "main", "master" })
        {
            var uri = new Uri($"https://raw.githubusercontent.com/{repository.Owner}/{repository.Name}/{branch}/package.json");
            try
            {
                var json = await GetStringAsync(uri, cancellationToken);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var packageName = ReadString(root, "name");
                var version = ReadString(root, "version");
                return VerifyManifest(root, packageName, version, item.InstallSpec);
            }
            catch (HttpRequestException) when (branch == "main")
            {
                // Try master below for older repositories.
            }
            catch (JsonException ex)
            {
                return Rejected(item, $"仓库 package.json 格式无效：{ex.Message}", item.InstallSpec);
            }
        }

        return Rejected(item, "仓库根目录没有找到 package.json。", item.InstallSpec);
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

    private IReadOnlyList<MarketplaceItem> ReadOfficialItems(ManagerInstance instance)
    {
        var profilePath = Path.Combine(instance.DshHome, "profiles", "web", "package.json");
        if (!File.Exists(profilePath))
        {
            return Array.Empty<MarketplaceItem>();
        }

        using var document = JsonDocument.Parse(File.ReadAllText(profilePath, Encoding.UTF8));
        if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<MarketplaceItem>();
        }

        var result = new List<MarketplaceItem>();
        foreach (var dependency in dependencies.EnumerateObject())
        {
            var manifestPath = FindDependencyManifest(instance, dependency.Name);
            if (manifestPath is null)
            {
                continue;
            }

            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var manifest = manifestDocument.RootElement;
            if (!HasDshBundlePatch(manifest))
            {
                continue;
            }

            result.Add(new MarketplaceItem(
                $"official:{dependency.Name}",
                dependency.Name,
                dependency.Name,
                ReadString(manifest, "version") ?? ReadValueString(dependency.Value),
                ReadString(manifest, "description") ?? "当前 DSh 运行环境提供的 Plugin。",
                dependency.Name,
                ReadRepositoryUrl(manifest),
                "官方",
                MarketplaceSourceKind.Official,
                "当前 DSh 运行环境",
                MarketplaceVerificationStatus.Verified,
                "已从当前实例的依赖和 package.json 读取。",
                true,
                false,
                false));
        }

        return result;
    }

    private string? FindDependencyManifest(ManagerInstance instance, string packageName)
    {
        var candidates = new[]
        {
            Path.Combine(instance.DshHome, "profiles", "web", "node_modules", packageName, "package.json"),
            Path.Combine(instance.RootPath, "node_modules", packageName, "package.json")
        };
        return candidates.FirstOrDefault(File.Exists);
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
        var installSpec = ReadString(entry, "installSpec")
            ?? ReadString(entry, "install")
            ?? (IsSafePackageName(packageName) ? packageName : null)
            ?? repository;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installSpec))
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

    private static string GetDedupeKey(MarketplaceItem item) =>
        item.PackageName?.Trim().ToLowerInvariant()
        ?? item.RepositoryUrl?.Trim().TrimEnd('/').ToLowerInvariant()
        ?? item.InstallSpec.Trim().ToLowerInvariant();

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

    private static bool TryGetGitHubRepository(string url, out GitHubRepository repository)
    {
        repository = default;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(segments[1]))
        {
            return false;
        }

        repository = new GitHubRepository(segments[0], segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1]);
        return true;
    }

    private sealed record MarketplaceCacheDocument(
        MarketplaceItem[] Items,
        int SourcesChecked,
        DateTimeOffset RetrievedAt);

    private readonly record struct GitHubRepository(string Owner, string Name);
}
