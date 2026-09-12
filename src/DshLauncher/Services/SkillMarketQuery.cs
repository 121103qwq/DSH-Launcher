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

    /// <summary>
    /// 变更集 124/125：来源下拉的选项——「全部来源」+ 来源说明行（不可选）+ 配置的 Skill 源 + 当前列表里实际出现的仓库。
    /// 技能列表来自动态 GitHub 搜索（`q=skill in:name`），并不存在一份固定的“内置来源清单”，
    /// 所以只列配置源在未配置时就是空的（变更集 123 的缺陷）；补上当前列表实际仓库后保证非空。
    /// 变更集 125：每项带该仓库的技能数（让用户知道“选了什么能得到什么”），并插入一行不可选的来源说明。
    /// </summary>
    /// <returns>按显示顺序排列的选项；首项固定为「全部来源」（Tag 为空串）。</returns>
    public static IReadOnlyList<SkillSourceChoice> BuildSourceChoices(
        IEnumerable<MarketSourceSetting> configuredSources,
        IEnumerable<SkillMarketItem> items)
    {
        ArgumentNullException.ThrowIfNull(configuredSources);
        ArgumentNullException.ThrowIfNull(items);

        var itemList = items as IReadOnlyCollection<SkillMarketItem> ?? items.ToArray();
        var repositoryOrder = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in itemList)
        {
            var repository = item.Repository;
            if (string.IsNullOrWhiteSpace(repository))
            {
                continue;
            }

            if (counts.TryGetValue(repository, out var count))
            {
                counts[repository] = count + 1;
            }
            else
            {
                counts[repository] = 1;
                repositoryOrder.Add(repository);
            }
        }

        var choices = new List<SkillSourceChoice>
        {
            new(string.Empty, $"全部来源（{itemList.Count} 个技能）", true, itemList.Count),
            new(string.Empty, "（仓库来自内置 GitHub 搜索）", false, 0)
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in configuredSources)
        {
            if (string.IsNullOrWhiteSpace(source.Value) || !seen.Add(source.Value))
            {
                continue;
            }

            var count = counts.TryGetValue(source.Value, out var value) ? value : 0;
            var label = source.Enabled
                ? $"{source.Value} · {count} 个技能"
                : $"{source.Value} · {count} 个技能（已停用）";
            choices.Add(new SkillSourceChoice(source.Value, label, true, count));
        }

        foreach (var repository in repositoryOrder)
        {
            if (seen.Add(repository))
            {
                choices.Add(new SkillSourceChoice(repository, $"{repository} · {counts[repository]} 个技能", true, counts[repository]));
            }
        }

        return choices;
    }
}

/// <summary>来源下拉的一项：标签用于显示；“说明行”通过 Selectable=false 表达；技能数让用户预知选中后的结果。</summary>
public sealed record SkillSourceChoice(string Tag, string Label, bool Selectable, int SkillCount);
