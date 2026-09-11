using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 中文插件源适配器：<c>deepseek1024.com</c>（借鉴 #16，评估见 work-log/63）。
/// <list type="bullet">
/// <item>接口：<c>/api/v2/plugins?page=N&amp;limit=200</c>（服务端分页，实测 <c>limit</c> 最大 200/页，
/// 约 13,656 条 / 69 页）。</item>
/// <item>字段：<c>id</c>＝<c>owner/repo[/子路径]</c>（monorepo 子目录插件）、<c>repository</c> 只是仓库名、
/// <c>description</c> 是 <c>{en, zh}</c>、<c>install</c> 是安装标识，另有安装统计（installCount / failureCount 等）。</item>
/// <item>策略：TTL 缓存（默认 30 分钟）+ 拉取失败回退旧缓存；原样缓存**原始载荷**，映射每次现算，
/// 避免把我们的模型序列化进缓存。</item>
/// <item>安全：本适配器只做**发现**——<c>install</c> 仅作为候选安装标识，安装前仍走 <c>package.json</c> 校验。</item>
/// </list>
/// </summary>
public sealed class Deepseek1024CatalogService
{
    public const string SourceName = "中文源 · deepseek1024.com";
    public const string DisplayName = "deepseek1024.com";

    private const string ApiBase = "https://deepseek1024.com/api/v2/plugins";
    private const int PageSize = 200;
    private const int MaximumPages = 80;
    private const int MaximumItems = 20_000;
    private const int MaxConcurrentPages = 4;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    private static readonly HttpClient SharedClient = CreateClient();

    private readonly LauncherPaths _paths;
    private readonly object _gate = new();
    private IReadOnlyList<MarketplaceItem>? _memoryCache;
    private DateTimeOffset _memoryCacheAt = DateTimeOffset.MinValue;

    public Deepseek1024CatalogService(LauncherPaths? paths = null) => _paths = paths ?? new LauncherPaths();

    public string CachePath => Path.Combine(_paths.RootDirectory, "deepseek1024-cache.json");

    /// <summary>最近一次拉取的来源状态（供 UI 提示）。</summary>
    public string LastStatus { get; private set; } = "尚未拉取";

    /// <summary>
    /// 拉取（或复用缓存）中文源插件条目。失败时返回旧缓存而不是抛异常——
    /// 一个第三方源不可用不应拖垮整个插件市场。
    /// </summary>
    public async Task<IReadOnlyList<MarketplaceItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_memoryCache is not null && DateTimeOffset.UtcNow - _memoryCacheAt < CacheTtl)
            {
                return _memoryCache;
            }
        }

        var disk = TryReadDiskCache(out var diskItems, out var diskAt);
        if (disk && DateTimeOffset.UtcNow - diskAt < CacheTtl)
        {
            LastStatus = $"缓存（{diskItems.Count} 条，{diskAt.ToLocalTime():HH:mm} 更新）";
            lock (_gate)
            {
                _memoryCache = diskItems;
                _memoryCacheAt = diskAt;
            }

            return diskItems;
        }

        try
        {
            var raw = await FetchRawAsync(cancellationToken);
            var items = raw.Select(TryMap).Where(item => item is not null).Select(item => item!).ToArray();
            TryWriteDiskCache(raw);
            lock (_gate)
            {
                _memoryCache = items;
                _memoryCacheAt = DateTimeOffset.UtcNow;
            }

            LastStatus = $"已更新 {items.Length} 条（{DateTimeOffset.Now:HH:mm}）";
            return items;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            if (disk)
            {
                LastStatus = $"更新失败（{ex.Message}），沿用旧缓存 {diskItems.Count} 条";
                lock (_gate)
                {
                    _memoryCache = diskItems;
                    _memoryCacheAt = diskAt;
                }

                return diskItems;
            }

            LastStatus = $"拉取失败：{ex.Message}";
            return Array.Empty<MarketplaceItem>();
        }
    }

    private static async Task<List<JsonElement>> FetchRawAsync(CancellationToken cancellationToken)
    {
        var first = await FetchPageAsync(1, cancellationToken);
        var items = new List<JsonElement>(ExtractItems(first));
        var totalPages = Math.Min(ReadInt(first, "totalPages", 1), MaximumPages);
        var pages = Enumerable.Range(2, Math.Max(0, totalPages - 1)).ToArray();
        using var gate = new SemaphoreSlim(MaxConcurrentPages);
        var results = new JsonElement[pages.Length];
        await Task.WhenAll(pages.Select(async (page, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[index] = await FetchPageAsync(page, cancellationToken);
            }
            catch (Exception)
            {
                // 单页失败不放弃整次拉取：其余页仍然有用。
                results[index] = default;
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var page in results)
        {
            if (page.ValueKind != JsonValueKind.Undefined)
            {
                items.AddRange(ExtractItems(page));
            }

            if (items.Count >= MaximumItems)
            {
                break;
            }
        }

        return items.Take(MaximumItems).ToList();
    }

    private static async Task<JsonElement> FetchPageAsync(int page, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await SharedClient.GetAsync($"{ApiBase}?page={page}&limit={PageSize}", timeout.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> ExtractItems(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("plugins", out var plugins)
        && plugins.ValueKind == JsonValueKind.Array
            ? plugins.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static int ReadInt(JsonElement payload, string name, int fallback) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    /// <summary>把一条原始条目映射成市场条目；信息不足（没仓库、没安装标识）时返回 null。</summary>
    public static MarketplaceItem? TryMap(JsonElement raw)
    {
        var id = ReadString(raw, "id");
        var repositoryUrl = ReadString(raw, "url");
        var owner = ReadString(raw, "owner");
        var repository = ReadString(raw, "repository");
        var name = ReadString(raw, "name");
        var installSpec = ReadString(raw, "install");

        // id 形如 owner/repo[/子路径]：取前两段作为仓库标识（repository 字段只有仓库名）。
        var parts = (id ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            owner ??= parts[0];
            repository ??= parts[1];
        }

        if (string.IsNullOrWhiteSpace(name) && parts.Length > 0)
        {
            name = parts[^1];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(repositoryUrl) && !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repository))
        {
            repositoryUrl = $"https://github.com/{owner}/{repository}";
        }

        installSpec = string.IsNullOrWhiteSpace(installSpec)
            ? string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository)
                ? null
                : $"github:{owner}/{repository}"
            : installSpec.Trim();

        if (string.IsNullOrWhiteSpace(installSpec))
        {
            // 既没有安装标识、也拼不出仓库标识的条目没法安装，直接丢弃（不往市场塞垃圾）。
            return null;
        }

        var description = ReadLocalized(raw, "description");
        var stars = raw.TryGetProperty("stars", out var starsValue) && starsValue.TryGetInt64(out var starCount)
            ? starCount
            : (long?)null;
        var installs = raw.TryGetProperty("installCount", out var installValue) && installValue.TryGetInt64(out var installCount)
            ? installCount
            : (long?)null;
        var failures = raw.TryGetProperty("failureCount", out var failureValue) && failureValue.TryGetInt64(out var failureCount)
            ? failureCount
            : (long?)null;
        var publishedAt = ReadTimestamp(raw, "added") ?? ReadTimestamp(raw, "pushedAt");

        var stats = installs is null
            ? string.Empty
            : $" · 安装 {installs} 次" + (failures is > 0 ? $"，失败 {failures} 次" : string.Empty);

        return new MarketplaceItem(
            Id: $"zh1024:{id ?? name}",
            Name: name,
            PackageName: installSpec is null
                ? null
                : installSpec.StartsWith("npm:", StringComparison.OrdinalIgnoreCase)
                    ? installSpec[4..]
                    : installSpec.Contains(':') ? null : installSpec,
            Version: null,
            Description: string.IsNullOrWhiteSpace(description) ? "（该源未提供中文描述）" : description,
            InstallSpec: installSpec ?? string.Empty,
            RepositoryUrl: repositoryUrl,
            Category: ReadString(raw, "category") ?? "其他",
            SourceKind: MarketplaceSourceKind.ZhCatalog,
            SourceName: SourceName + stats,
            VerificationStatus: MarketplaceVerificationStatus.Unverified,
            VerificationMessage: "来源：deepseek1024.com（安装前仍会读取 package.json 校验）",
            IsInstalled: false,
            IsManaged: false,
            CanMutate: false,
            Stars: stars,
            PublishedAt: publishedAt);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null!;

    private static string ReadLocalized(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim() ?? string.Empty;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "zh", "zh-CN", "en" })
        {
            if (value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return text.GetString()!.Trim();
            }
        }

        return string.Empty;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        var text = ReadString(element, name);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private bool TryReadDiskCache(out IReadOnlyList<MarketplaceItem> items, out DateTimeOffset savedAt)
    {
        items = Array.Empty<MarketplaceItem>();
        savedAt = DateTimeOffset.MinValue;
        try
        {
            if (!File.Exists(CachePath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(CachePath, Encoding.UTF8));
            var root = document.RootElement;
            if (!root.TryGetProperty("savedAt", out var savedValue)
                || !DateTimeOffset.TryParse(savedValue.GetString(), out savedAt)
                || !root.TryGetProperty("items", out var rawItems)
                || rawItems.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            items = rawItems.EnumerateArray()
                .Select(TryMap)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            return items.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryWriteDiskCache(IReadOnlyList<JsonElement> rawItems)
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            using var stream = File.Create(CachePath);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString("savedAt", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteString("source", ApiBase);
            writer.WriteStartArray("items");
            foreach (var item in rawItems)
            {
                item.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 缓存写不进去不影响本次结果。
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0 (+zh-plugin-source)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
        return client;
    }
}
