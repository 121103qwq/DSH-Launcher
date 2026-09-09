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
    }
}
