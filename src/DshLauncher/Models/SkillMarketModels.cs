namespace DshLauncher.Models;

/// <summary>
/// Skill 市场候选：GitHub 上名称含 skill、根目录带 SKILL.md 的公开仓库。
/// </summary>
public sealed record SkillMarketItem(
    string Repository,
    string Name,
    string? Description,
    int Stars,
    string DefaultBranch,
    DateTimeOffset? UpdatedAt,
    bool Verified);
