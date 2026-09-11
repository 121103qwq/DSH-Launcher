using System.Net.Http;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>一次官方版本查询的结果（含比较结论）。</summary>
public sealed record DshUpdateNotice(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    DateTimeOffset CheckedAt);

/// <summary>
/// dsh 官方版本提示（实例设置里的「显示 DSh 版本更新提示」开关 + 实例卡片徽标共用）。
/// <list type="bullet">
/// <item>带 <b>6 小时内存缓存</b>：同一实例反复切页/切选中不会反复联网；</item>
/// <item>网络失败<b>不缓存</b>，下次仍会尝试；调用方决定怎么展示失败；</item>
/// <item>比较走 <see cref="PluginCompatibility.Compare"/>（含预发布顺序），与"更换运行版本"同一口径。</item>
/// </list>
/// </summary>
public sealed class DshUpdateNoticeService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<HttpClient>? _clientFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, DshUpdateNotice> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DshUpdateNoticeService(Func<HttpClient>? clientFactory = null, Func<DateTimeOffset>? clock = null)
    {
        _clientFactory = clientFactory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>查询官方最新版本并与当前版本比较；无版本号或查询失败时返回 null（不缓存失败）。</summary>
    public async Task<DshUpdateNotice?> CheckAsync(
        string? currentVersion,
        CancellationToken cancellationToken,
        bool force = false)
    {
        var current = Normalize(currentVersion);
        if (current.Length == 0)
        {
            return null;
        }

        if (!force && TryReadCache(current, out var cached))
        {
            return cached;
        }

        var latest = await ReadLatestAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(latest))
        {
            return null;
        }

        var notice = new DshUpdateNotice(
            current,
            latest,
            PluginCompatibility.Compare(latest, current) > 0,
            _clock());
        lock (_gate)
        {
            _cache[current] = notice;
        }

        return notice;
    }

    /// <summary>读缓存结论（不联网）：卡片徽标按选中变化同步求值，需要的就是这个。</summary>
    public bool TryPeek(string? currentVersion, out DshUpdateNotice? notice)
    {
        var current = Normalize(currentVersion);
        if (current.Length == 0)
        {
            notice = null;
            return false;
        }

        return TryReadCache(current, out notice);
    }

    /// <summary>丢弃缓存（实例刚换过版本时用，避免继续显示旧结论）。</summary>
    public void Invalidate(string? currentVersion)
    {
        var current = Normalize(currentVersion);
        lock (_gate)
        {
            if (current.Length == 0)
            {
                _cache.Clear();
                return;
            }

            _cache.Remove(current);
        }
    }

    private bool TryReadCache(string current, out DshUpdateNotice? notice)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(current, out var cached)
                && _clock() - cached.CheckedAt < CacheLifetime)
            {
                notice = cached;
                return true;
            }
        }

        notice = null;
        return false;
    }

    private async Task<string?> ReadLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);
        // 未注入工厂时让目录服务自己建/自己释放 HttpClient；注入时由目录服务接管释放。
        using var catalog = _clientFactory is null
            ? new DshVersionCatalogService()
            : new DshVersionCatalogService(_clientFactory());
        var versions = await catalog.ReadOfficialVersionsAsync(timeout.Token);
        return versions.FirstOrDefault();
    }

    private static string Normalize(string? version) =>
        version?.Trim().TrimStart('v', 'V') ?? string.Empty;
}
