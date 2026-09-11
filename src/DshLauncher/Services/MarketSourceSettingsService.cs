using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

/// <summary>自定义来源的两类：插件市场与 Skill 市场（设置里的大项，两者结构平行）。</summary>
public enum MarketSourceKind
{
    Plugin,
    Skill
}

/// <summary>一条自定义来源（值 + 是否启用）。</summary>
public sealed record MarketSourceSetting(string Value, bool Enabled);

/// <summary>一条自定义来源在界面上的展示信息。</summary>
public sealed record MarketSourceEntry(string Value, string TypeText, bool IsUrl, bool IsGitHubRepository)
{
    public string DisplayText => $"{Value}    · {TypeText}";
}

/// <summary>连通性探测结果。</summary>
public sealed record MarketSourceProbeResult(bool Ok, string Message);

/// <summary>
/// 自定义来源的读写与探测。
/// <list type="bullet">
/// <item>插件：<c>&lt;数据根&gt;/marketplace-sources.json</c>（JSON 数组；元素＝本地目录文件路径或 URL，
/// 由 <c>MarketplaceService</c> 读取，格式为我们自己的目录 JSON）。</item>
/// <item>Skill：<c>&lt;数据根&gt;/skill-market-sources.json</c>（JSON 数组；元素＝GitHub <c>owner/repo</c>、
/// 本地目录文件路径或 URL；由 <c>SkillMarketService</c> 读取）。</item>
/// </list>
/// 只负责"发现层"：来源里的文本一律不当作安装命令（安装仍走包元数据 + <c>package.json</c> 校验）。
/// </summary>
public sealed partial class MarketSourceSettingsService
{
    public const int MaximumSources = 50;
    private const int MaximumValueLength = 2048;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient SharedClient = CreateClient();

    private readonly LauncherPaths _paths;

    public MarketSourceSettingsService(LauncherPaths? paths = null) => _paths = paths ?? new LauncherPaths();

    public string FilePath(MarketSourceKind kind) => kind == MarketSourceKind.Plugin
        ? _paths.MarketplaceSourcesPath
        : Path.Combine(_paths.RootDirectory, "skill-market-sources.json");

    /// <summary>读取自定义来源（含启用状态）；缺失/损坏按"没有来源"。</summary>
    public IReadOnlyList<MarketSourceSetting> ReadEntries(MarketSourceKind kind)
    {
        SeedBuiltInAdaptersIfMissing(kind);
        var path = FilePath(kind);
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<MarketSourceSetting>();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<MarketSourceSetting>();
            }

            var result = new List<MarketSourceSetting>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                string? value;
                var enabled = true;
                if (entry.ValueKind == JsonValueKind.String)
                {
                    value = entry.GetString();
                }
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    value = entry.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
                        ? valueElement.GetString()
                        : null;
                    enabled = !entry.TryGetProperty("enabled", out var enabledElement)
                        || enabledElement.ValueKind != JsonValueKind.False;
                }
                else
                {
                    continue;
                }

                if (Normalize(kind, value) is { } normalized && seen.Add(normalized))
                {
                    result.Add(new MarketSourceSetting(normalized, enabled));
                }

                if (result.Count >= MaximumSources)
                {
                    break;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<MarketSourceSetting>();
        }
    }

    /// <summary>只取启用的来源（市场与技能扫描消费这个）。</summary>
    public IReadOnlyList<string> ReadEnabled(MarketSourceKind kind) =>
        ReadEntries(kind).Where(entry => entry.Enabled).Select(entry => entry.Value).ToArray();

    /// <summary>开关一条来源（不删除）。</summary>
    public bool TrySetEnabled(MarketSourceKind kind, string value, bool enabled, out string message)
    {
        var entries = ReadEntries(kind).ToList();
        var index = entries.FindIndex(entry => string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            message = "这条来源不在列表里。";
            return false;
        }

        entries[index] = entries[index] with { Enabled = enabled };
        return TryWriteEntries(kind, entries, out message);
    }

    /// <summary>读取自定义来源的值（含停用项）；缺失/损坏按"没有来源"。</summary>
    public IReadOnlyList<string> Read(MarketSourceKind kind) =>
        ReadEntries(kind).Select(entry => entry.Value).ToArray();

    /// <summary>添加一条自定义来源；非法或重复时返回 false 并给出原因。</summary>
    public bool TryAdd(MarketSourceKind kind, string? value, out string message)
    {
        var normalized = Normalize(kind, value);
        if (normalized is null)
        {
            message = kind == MarketSourceKind.Plugin
                ? "只接受本地目录 JSON 文件路径或 https/http 网址。"
                : "只接受 GitHub 仓库（owner/repo）、本地目录 JSON 文件路径或 https/http 网址。";
            return false;
        }

        var current = ReadEntries(kind).ToList();
        if (current.Any(item => string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            message = "这条来源已经在列表里。";
            return false;
        }

        if (current.Count >= MaximumSources)
        {
            message = $"最多 {MaximumSources} 条来源。";
            return false;
        }

        current.Add(new MarketSourceSetting(normalized, true));
        return TryWriteEntries(kind, current, out message);
    }

    public bool TryRemove(MarketSourceKind kind, string value, out string message)
    {
        var current = ReadEntries(kind)
            .Where(item => !string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return TryWriteEntries(kind, current, out message);
    }

    private bool TryWriteEntries(MarketSourceKind kind, IReadOnlyList<MarketSourceSetting> entries, out string message)
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            // 写成对象数组（{"value":…,"enabled":…}）；读侧兼容旧的纯字符串数组。
            var payload = entries.Select(entry => new { value = entry.Value, enabled = entry.Enabled }).ToArray();
            File.WriteAllText(
                FilePath(kind),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            var enabledCount = entries.Count(entry => entry.Enabled);
            message = entries.Count == 0
                ? "已保存（列表为空）。"
                : $"已保存 {entries.Count} 条来源（启用 {enabledCount} 条）；市场下次刷新时生效。";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = $"保存失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>内置中文源适配器令牌（唯一固定来源是 GitHub；这两个由用户按需开关）。</summary>
    public const string AdapterZh1024 = "adapter:zh1024";

    public const string AdapterDshfind = "adapter:dshfind";

    public static readonly IReadOnlyList<string> BuiltInAdapterTokens = new[] { AdapterZh1024, AdapterDshfind };

    public static bool IsAdapterToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && BuiltInAdapterTokens.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>首次使用时把两个中文适配器写进自定义列表（默认停用，由用户自己开）。</summary>
    private void SeedBuiltInAdaptersIfMissing(MarketSourceKind kind)
    {
        if (kind != MarketSourceKind.Plugin || File.Exists(FilePath(kind)))
        {
            return;
        }

        TryWriteEntries(
            kind,
            BuiltInAdapterTokens.Select(token => new MarketSourceSetting(token, false)).ToArray(),
            out _);
    }

    /// <summary>规范化并校验一条来源；非法返回 null。</summary>
    public static string? Normalize(MarketSourceKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaximumValueLength || trimmed.Any(char.IsControl))
        {
            return null;
        }

        if (kind == MarketSourceKind.Plugin && IsAdapterToken(trimmed))
        {
            return trimmed;
        }

        if (TryParseHttpUri(trimmed, out _))
        {
            return trimmed;
        }

        if (kind == MarketSourceKind.Skill && GitHubRepositoryPattern().IsMatch(trimmed))
        {
            return trimmed;
        }

        // 本地文件：必须形如 *.json（目录式导入走实例页的"导入 Skill"，不在这里）。
        if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return null;
    }

    public static MarketSourceEntry Describe(MarketSourceKind kind, string value)
    {
        var isUrl = TryParseHttpUri(value, out _);
        var isRepo = !isUrl && kind == MarketSourceKind.Skill && GitHubRepositoryPattern().IsMatch(value);
        var type = isUrl
            ? "网址目录"
            : IsAdapterToken(value)
                ? "内置中文源（源自 GitHub 目录，可关可删）"
                : isRepo
                    ? "GitHub 仓库（扫描其中的 SKILL.md）"
                    : "本地目录文件";
        return new MarketSourceEntry(value, type, isUrl, isRepo);
    }

    /// <summary>探测一条来源是否可达：URL 发一次 GET、仓库查 GitHub API、本地文件看是否存在。</summary>
    public async Task<MarketSourceProbeResult> ProbeAsync(
        MarketSourceKind kind,
        string value,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(kind, value);
        if (normalized is null)
        {
            return new MarketSourceProbeResult(false, "格式不合法");
        }

        if (IsAdapterToken(normalized))
        {
            // 令牌探测＝直接探它背后的真实接口。
            var probeUrl = normalized.Equals(AdapterZh1024, StringComparison.OrdinalIgnoreCase)
                ? "https://deepseek1024.com/api/v2/plugins?page=1&limit=200"
                : "https://dshfind.com/api/plugins-data";
            try
            {
                using var adapterTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                adapterTimeout.CancelAfter(ProbeTimeout);
                using var adapterResponse = await SharedClient.GetAsync(new Uri(probeUrl), adapterTimeout.Token);
                var length = adapterResponse.Content.Headers.ContentLength;
                return new MarketSourceProbeResult(
                    adapterResponse.IsSuccessStatusCode,
                    adapterResponse.IsSuccessStatusCode
                        ? $"HTTP {(int)adapterResponse.StatusCode}{(length is null ? string.Empty : $"，{length} 字节")}"
                        : $"HTTP {(int)adapterResponse.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
            {
                return new MarketSourceProbeResult(false, ex.Message);
            }
        }

        var entry = Describe(kind, normalized);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            if (entry.IsGitHubRepository)
            {
                using var response = await SharedClient.GetAsync(
                    new Uri($"https://api.github.com/repos/{normalized}"), timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return new MarketSourceProbeResult(false, $"GitHub 返回 {(int)response.StatusCode}");
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var root = document.RootElement;
                var branch = root.TryGetProperty("default_branch", out var branchValue) ? branchValue.GetString() : null;
                var stars = root.TryGetProperty("stargazers_count", out var starValue) ? starValue.GetInt32() : 0;
                return new MarketSourceProbeResult(true, $"仓库可达（默认分支 {branch ?? "未知"}，★{stars}）");
            }

            if (entry.IsUrl)
            {
                using var response = await SharedClient.GetAsync(new Uri(normalized), timeout.Token);
                var length = response.Content.Headers.ContentLength;
                return new MarketSourceProbeResult(
                    response.IsSuccessStatusCode,
                    response.IsSuccessStatusCode
                        ? $"HTTP {(int)response.StatusCode}{(length is null ? string.Empty : $"，{length} 字节")}"
                        : $"HTTP {(int)response.StatusCode}");
            }

            var info = new FileInfo(normalized);
            return info.Exists
                ? new MarketSourceProbeResult(true, $"文件存在（{info.Length} 字节）")
                : new MarketSourceProbeResult(false, "文件不存在");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MarketSourceProbeResult(false, $"超时（>{ProbeTimeout.TotalSeconds:F0} 秒）");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
            or UnauthorizedAccessException or InvalidOperationException or UriFormatException or ArgumentException)
        {
            return new MarketSourceProbeResult(false, ex.Message);
        }
    }

    private static bool TryParseHttpUri(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = ProbeTimeout };
        // 有些目录站会按 UA 拒掉默认请求（实测 dshfind 会对 Python-urllib 返回 403）。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Launcher/1.0 (+market-source-probe)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+$")]
    private static partial Regex GitHubRepositoryPattern();
}
