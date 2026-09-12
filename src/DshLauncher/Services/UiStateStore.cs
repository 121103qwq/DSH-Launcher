using System.IO;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>插件市场 UI 状态（搜索词/分类/来源/排序/各分类滚动位置）。</summary>
public sealed record MarketplaceUiState
{
    public string? Search { get; init; }

    public string? CategoryKey { get; init; }

    public string? SourceKey { get; init; }

    public string? SortKey { get; init; }

    public Dictionary<string, double> ScrollOffsets { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 轻量 UI 状态持久化（借鉴 dsh-plugins/dsh-launcher 的 fix #38「持久化市场搜索/筛选/滚动位置」，思路借鉴）。
/// 文件：&lt;Launcher 数据根&gt;\ui-state.json，按实例 ID 记录各窗口的界面状态。
/// 读写失败只降级（Warn），绝不打断 UI。
/// </summary>
public sealed class UiStateStore
{
    private readonly string _path;
    private readonly object _sync = new();

    public UiStateStore(LauncherPaths? paths = null)
    {
        _path = Path.Combine((paths ?? new LauncherPaths()).RootDirectory, "ui-state.json");
    }

    public string FilePath => _path;

    public MarketplaceUiState? GetMarketplace(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        lock (_sync)
        {
            return ReadRoot()?.Marketplaces.TryGetValue(instanceId, out var state) == true ? state : null;
        }
    }

    public void SaveMarketplace(string instanceId, MarketplaceUiState state)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || state is null)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var root = ReadRoot() ?? new UiStateRoot();
                root.Marketplaces[instanceId] = state;
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(root), new UTF8Encoding(false));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                LauncherLog.Warn("保存界面状态失败（不影响使用）。", ErrorCodes.E9001,
                    new { path = _path, error = ex.Message });
            }
        }
    }

    /// <summary>变更集 146：读取某页记住的垂直滚动偏移（无记录返回 0）。</summary>
    public double GetScrollOffset(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return 0;
        }

        lock (_sync)
        {
            return ReadRoot()?.ScrollOffsets.TryGetValue(key, out var offset) == true && offset > 0 ? offset : 0;
        }
    }

    /// <summary>变更集 146：记住某页的垂直滚动偏移（写盘失败只降级，不打断 UI）。</summary>
    public void SaveScrollOffset(string key, double offset)
    {
        if (string.IsNullOrWhiteSpace(key) || double.IsNaN(offset) || offset < 0)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var root = ReadRoot() ?? new UiStateRoot();
                if (root.ScrollOffsets.TryGetValue(key, out var current) && Math.Abs(current - offset) < 1)
                {
                    return;   // 变化 < 1px 不写盘，避免滚动过程中频繁 IO
                }

                root.ScrollOffsets[key] = offset;
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(root), new UTF8Encoding(false));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                LauncherLog.Warn("保存滚动位置失败（不影响使用）。", ErrorCodes.E9001,
                    new { path = _path, key, error = ex.Message });
            }
        }
    }

    private UiStateRoot? ReadRoot()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<UiStateRoot>(File.ReadAllText(_path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            LauncherLog.Warn("界面状态文件损坏，按空状态处理。", ErrorCodes.E9001,
                new { path = _path, error = ex.Message });
            return null;
        }
    }

    private sealed class UiStateRoot
    {
        public Dictionary<string, MarketplaceUiState> Marketplaces { get; set; } = new(StringComparer.Ordinal);

        /// <summary>变更集 146：内嵌页的滚动位置（key = 页/分类标识，value = 垂直偏移 px）。</summary>
        public Dictionary<string, double> ScrollOffsets { get; set; } = new(StringComparer.Ordinal);
    }
}
