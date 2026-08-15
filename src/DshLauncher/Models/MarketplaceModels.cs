using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public enum MarketplaceSourceKind
{
    Official,
    CommunityCatalog,
    GitHubTopic,
    Custom
}

public enum MarketplaceVerificationStatus
{
    Verified,
    Unverified,
    Rejected
}

public enum MarketplaceSortOrder
{
    Relevance,
    PublishedAt,
    Stars
}

public sealed record MarketplaceItem(
    string Id,
    string Name,
    string? PackageName,
    string? Version,
    string Description,
    string InstallSpec,
    string? RepositoryUrl,
    string Category,
    MarketplaceSourceKind SourceKind,
    string SourceName,
    MarketplaceVerificationStatus VerificationStatus,
    string VerificationMessage,
    bool IsInstalled = false,
    bool IsManaged = false,
    bool CanMutate = false,
    long? Stars = null,
    DateTimeOffset? PublishedAt = null)
{
    [JsonIgnore]
    public string SourceText => SourceKind switch
    {
        MarketplaceSourceKind.Official => "DSh 官方运行环境",
        MarketplaceSourceKind.CommunityCatalog => "社区目录",
        MarketplaceSourceKind.GitHubTopic => "GitHub dsh-plugin 标签",
        _ => SourceName
    };

    [JsonIgnore]
    public string VerificationText => VerificationStatus switch
    {
        MarketplaceVerificationStatus.Verified => "已检查，可以安装",
        MarketplaceVerificationStatus.Rejected => "不能安装",
        _ => "安装前还要检查"
    };

    [JsonIgnore]
    public string ActionText => IsInstalled ? "更新" : "安装";

    [JsonIgnore]
    public bool CanAction => CanMutate
        && VerificationStatus != MarketplaceVerificationStatus.Rejected
        && (!IsInstalled || IsManaged);

    [JsonIgnore]
    public bool CanRemove => CanMutate && IsInstalled && IsManaged;

    [JsonIgnore]
    public string TargetText => PackageName ?? InstallSpec;

    [JsonIgnore]
    public string StarsText => Stars is { } count ? $"★ {count:N0}" : "Star 未知";

    [JsonIgnore]
    public string PublishedText => PublishedAt is { } published
        ? published.ToLocalTime().ToString("yyyy-MM-dd")
        : "发布时间未知";
}

public sealed record MarketplaceSearchResult(
    IReadOnlyList<MarketplaceItem> Items,
    IReadOnlyList<string> Warnings,
    int SourcesChecked,
    DateTimeOffset RetrievedAt);

public sealed record MarketplaceVerificationResult(
    MarketplaceVerificationStatus Status,
    string Message,
    string? PackageName,
    string? Version,
    string? InstallSpec);
