using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 变更集 123：技能市场（Agent 页）的筛选与排序——从窗口里抽出的纯函数，便于 SelfTest 覆盖。
/// 分类/搜索/来源三个条件与扩展页语义对齐；排序三档：综合（保持服务返回顺序）/ 热门（Star）/ 最近更新。
/// </summary>
public static class SkillMarketQuery
{
    /// <summary>按分类、搜索词、来源过滤，再按排序键排序；空值一律表示“不过滤/默认排序”。</summary>
    /// <remarks>
    /// 来源匹配为双向包含：设置页里可能写 `owner/repo`，也可能是完整 GitHub URL，
    /// 而条目的 <see cref="SkillMarketItem.Repository"/> 不一定同形，因此任一方包含另一方即算命中。
    /// </remarks>
    public static IReadOnlyList<SkillMarketItem> Apply(
        IEnumerable<SkillMarketItem> items,
        string? category,
        string? query,
        string? sourceFilter,
        string? sortKey)
    {
        ArgumentNullException.ThrowIfNull(items);

        var filtered = items
            .Where(item => string.IsNullOrWhiteSpace(category)
                || string.Equals(item.Category, category, StringComparison.Ordinal))
            .Where(item => string.IsNullOrWhiteSpace(query)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(item => string.IsNullOrWhiteSpace(sourceFilter)
                || item.Repository.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase)
                || sourceFilter.EndsWith(item.Repository, StringComparison.OrdinalIgnoreCase));

        IEnumerable<SkillMarketItem> sorted = sortKey switch
        {
            "Stars" => filtered
                .OrderByDescending(item => item.Stars)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "UpdatedAt" => filtered
                .OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered
        };

        return sorted.ToArray();
    }
}
