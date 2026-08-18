namespace DshLauncher.Models;

/// <summary>
/// Skill 市场条目：从 GitHub 仓库内发现并校验通过的单个 SKILL.md。
/// </summary>
public sealed record SkillMarketItem(
    string Repository,
    string Name,
    string? Description,
    int Stars,
    string DefaultBranch,
    DateTimeOffset? UpdatedAt,
    bool Verified,
    int ValidationVersion = 0,
    string SkillPath = "",
    string Category = "其他");

public sealed record SkillMarketRefreshProgress(
    IReadOnlyList<SkillMarketItem> Items,
    int Completed,
    int Total,
    string Stage);

public sealed record SkillInstallProgress(
    long BytesReceived,
    long? TotalBytes,
    int? Percent,
    string Stage);
