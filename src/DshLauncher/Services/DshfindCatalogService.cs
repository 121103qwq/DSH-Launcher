using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 中文插件源适配器 ②：<c>dshfind.com</c>（借鉴 #16，评估见 work-log/63）。
/// <list type="bullet">
/// <item>接口：<c>/api/plugins-data</c>——**一次拉全量**（约 8.2 MB / 14,117 条）。</item>
/// <item>字段：<c>fullName</c>＝<c>owner/repo</c>、<c>description</c>（多为英文）、<c>tags</c>/<c>language</c>/
/// <c>stars</c>/<c>starGrowth</c>/<c>pushedAt</c>/<c>archived</c>/<c>isFeatured</c>/<c>isOfficial</c>；
/// 顶层另有 <c>i18nDescriptions</c>（按 <c>owner/repo</c> 的小型翻译表，含 <c>zh</c> 时优先用）。</item>
/// <item>策略：TTL 缓存（30 分钟）+ **单飞**（并发刷新只发一次请求，8 MB 不能重复拉）+ 失败回退旧缓存；
/// <c>archived</c> 条目直接排除。</item>
/// <item>安全：只做**发现**——安装标识由 <c>owner/repo</c> 生成，安装前仍走 <c>package.json</c> 校验。</item>
/// </list>
/// </summary>
public sealed class DshfindCatalogService
{
    public const string SourceName = "中文源 · dshfind.com";
    public const string DisplayName = "dshfind.com";

    private const string ApiUrl = "https://dshfind.com/api/plugins-data";
    private const int MaximumItems = 30_000;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    private static readonly HttpClient SharedClient = CreateClient();

    private readonly LauncherPaths _paths;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly object _gate = new();
    private IReadOnlyList<MarketplaceItem>? _memoryCache;
    private DateTimeOffset _memoryCacheAt = DateTimeOffset.MinValue;

    public DshfindCatalogService(LauncherPaths? paths = null) => _paths = paths ?? new LauncherPaths();

    public string CachePath => Path.Combine(_paths.RootDirectory, "dshfind-cache.json");

    public string LastStatus { get; private set; } = "尚未拉取";

    /// <summary>拉取（或复用缓存）dshfind 插件条目；失败回退旧缓存，不抛给市场。</summary>
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
            SetMemory(diskItems, diskAt);
            LastStatus = $"缓存（{diskItems.Count} 条，{diskAt.ToLocalTime():HH:mm} 更新）";
            return diskItems;
        }

        // 单飞：并发刷新只发一次请求（8.2 MB 的全量载荷不能重复拉）。
        await _fetchGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_memoryCache is not null && DateTimeOffset.UtcNow - _memoryCacheAt < CacheTtl)
                {
                    return _memoryCache;
                }
            }

            var raw = await FetchAsync(cancellationToken);
            var items = MapAll(raw);
            TryWriteDiskCache(raw);
            SetMemory(items, DateTimeOffset.UtcNow);
            LastStatus = $"已更新 {items.Count} 条（{DateTimeOffset.Now:HH:mm}）";
            return items;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            if (disk)
            {
                SetMemory(diskItems, diskAt);
                LastStatus = $"更新失败（{ex.Message}），沿用旧缓存 {diskItems.Count} 条";
                return diskItems;
            }

            LastStatus = $"拉取失败：{ex.Message}";
            return Array.Empty<MarketplaceItem>();
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private static async Task<List<JsonElement>> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await SharedClient.GetAsync(ApiUrl, timeout.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("dshfind 返回里没有 plugins 数组");
        }

        JsonElement? i18n = root.TryGetProperty("i18nDescriptions", out var table) && table.ValueKind == JsonValueKind.Object
            ? table.Clone()
            : null;
        var items = new List<JsonElement>(plugins.GetArrayLength() + 1);
        foreach (var plugin in plugins.EnumerateArray())
        {
            var clone = plugin.Clone();
            items.Add(clone);
            if (i18n is null)
            {
                continue;
            }

            var fullName = clone.TryGetProperty("fullName", out var fullNameValue) ? fullNameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(fullName) || !i18n.Value.TryGetProperty(fullName, out var translation))
            {
                continue;
            }

            // 把翻译表并入条目（映射时优先用 zh/en），保持缓存自洽。
            var merged = new Dictionary<string, object?>();
            using var writer = JsonDocument.Parse(clone.GetRawText());
            foreach (var property in writer.RootElement.EnumerateObject())
            {
                merged[property.Name] = property.Value.Clone();
            }

            merged["i18n"] = translation.Clone();
            items[^1] = JsonSerializer.SerializeToElement(merged);
        }

        return items.Take(MaximumItems).ToList();
    }

    /// <summary>批量映射（排除 archived）。</summary>
    public static IReadOnlyList<MarketplaceItem> MapAll(IEnumerable<JsonElement> raw) =>
        raw.Select(TryMap).Where(item => item is not null).Select(item => item!).ToArray();

    /// <summary>把一条 dshfind 条目映射成市场条目；archived 或信息不足返回 null。</summary>
    public static MarketplaceItem? TryMap(JsonElement raw)
    {
        var fullName = ReadString(raw, "fullName");
        if (string.IsNullOrWhiteSpace(fullName))
        {
            var ownerFallback = ReadString(raw, "owner");
            var nameFallback = ReadString(raw, "name");
            fullName = string.IsNullOrWhiteSpace(ownerFallback) || string.IsNullOrWhiteSpace(nameFallback)
                ? null
                : $"{ownerFallback}/{nameFallback}";
        }

        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        if (raw.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var name = ReadString(raw, "name") ?? fullName.Split('/')[^1];
        var url = ReadString(raw, "url") ?? $"https://github.com/{fullName}";
        var description = ReadTranslated(raw, "i18n", "zh")
            ?? ReadTranslated(raw, "i18n", "en")
            ?? ReadString(raw, "description")
            ?? string.Empty;
        var category = ReadString(raw, "category");
        if (string.IsNullOrWhiteSpace(category))
        {
            category = ReadStringArray(raw, "tags").FirstOrDefault() ?? "其他";
        }

        var stars = raw.TryGetProperty("stars", out var starsValue) && starsValue.TryGetInt64(out var starCount)
            ? starCount
            : (long?)null;
        var tags = ReadStringArray(raw, "tags");
        var badges = new List<string>();
        if (raw.TryGetProperty("isOfficial", out var official) && official.ValueKind == JsonValueKind.True)
        {
            badges.Add("官方");
        }

        if (raw.TryGetProperty("isFeatured", out var featured) && featured.ValueKind == JsonValueKind.True)
        {
            badges.Add("精选");
        }

        if (stars is > 0)
        {
            badges.Add($"★{stars}");
        }

        if (tags.Count > 0)
        {
            badges.Add(string.Join('/', tags.Take(3)));
        }

        return new MarketplaceItem(
            Id: $"dshfind:{fullName}",
            Name: name,
            PackageName: null,
            Version: null,
            Description: string.IsNullOrWhiteSpace(description) ? "（该源未提供描述）" : description,
            InstallSpec: $"github:{fullName}",
            RepositoryUrl: url,
            Category: category,
            SourceKind: MarketplaceSourceKind.ZhCatalog,
            SourceName: SourceName + (badges.Count == 0 ? string.Empty : " · " + string.Join(" · ", badges)),
            VerificationStatus: MarketplaceVerificationStatus.Unverified,
            VerificationMessage: "来源：dshfind.com（安装前仍会读取 package.json 校验）",
            IsInstalled: false,
            IsManaged: false,
            CanMutate: false,
            Stars: stars,
            PublishedAt: ReadTimestamp(raw, "pushedAt"));
    }

    private void SetMemory(IReadOnlyList<MarketplaceItem> items, DateTimeOffset at)
    {
        lock (_gate)
        {
            _memoryCache = items;
            _memoryCacheAt = at;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static string? ReadTranslated(JsonElement element, string tableName, string language)
    {
        if (!element.TryGetProperty(tableName, out var table) || table.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return table.TryGetProperty(language, out var text) && text.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(text.GetString())
                ? text.GetString()!.Trim()
                : null;
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

            items = MapAll(rawItems.EnumerateArray());
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
            writer.WriteString("source", ApiUrl);
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
        // 实测该站会 403 拒掉默认 UA。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0 (+zh-plugin-source)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*");
        return client;
    }
}
