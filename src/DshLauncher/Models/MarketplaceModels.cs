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

public enum MarketplaceUpdateStatus
{
    Unknown,
    UpToDate,
    Available,
    Unavailable
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
    DateTimeOffset? PublishedAt = null,
    string? InstalledVersion = null,
    MarketplaceUpdateStatus UpdateStatus = MarketplaceUpdateStatus.Unknown,
    string? MergedSourceText = null,
    bool IsTheme = false,
    bool ThemeMarketAvailable = false,
    bool ThemeCanApply = false,
    string? ThemePackageName = null,
    string? ThemeStatusText = null,
    IReadOnlyList<MarketplaceSourceKind>? MergedSourceKinds = null,
    bool CanInstallOrUpdate = false,
    string? DeveloperAvatarUrl = null,
    string? DshMarketUrl = null,
    bool IsHotLoadAction = false)
{
    [JsonIgnore]
    public string SourceText => MergedSourceText ?? (SourceKind switch
    {
        MarketplaceSourceKind.Official => "DSh 官方",
        MarketplaceSourceKind.CommunityCatalog => "社区目录",
        MarketplaceSourceKind.GitHubTopic => "GitHub dsh-plugin 标签",
        _ => SourceName
    });

    [JsonIgnore]
    public string VerificationText => VerificationStatus switch
    {
        MarketplaceVerificationStatus.Verified => "已检查，可以安装",
        MarketplaceVerificationStatus.Rejected => "不能安装",
        _ => "安装前还要检查"
    };

    [JsonIgnore]
    public string ActionText
    {
        get
        {
            if (!IsInstalled)
            {
                return IsHotLoadAction ? "热加载" : "安装";
            }

            if (!IsManaged)
            {
                return "已安装";
            }

            return UpdateStatus switch
            {
                MarketplaceUpdateStatus.Available when !string.IsNullOrWhiteSpace(InstalledVersion)
                    && !string.IsNullOrWhiteSpace(Version) => $"更新 {InstalledVersion} → {Version}",
                MarketplaceUpdateStatus.UpToDate => "已安装",
                MarketplaceUpdateStatus.Unavailable => "更新不可用",
                _ => "更新状态未知"
            };
        }
    }

    [JsonIgnore]
    public bool CanAction => CanInstallOrUpdate
        && VerificationStatus != MarketplaceVerificationStatus.Rejected
        && (!IsInstalled || (IsManaged && UpdateStatus != MarketplaceUpdateStatus.UpToDate));

    [JsonIgnore]
    public bool CanRemove => CanMutate && IsInstalled && IsManaged;

    [JsonIgnore]
    public string TargetText => PackageName ?? InstallSpec;

    [JsonIgnore]
    public string VersionStatusText => !IsInstalled
        ? (string.IsNullOrWhiteSpace(Version) ? "版本未知" : $"最新 {Version}")
        : string.IsNullOrWhiteSpace(InstalledVersion)
            ? "已安装 · 版本未知"
            : UpdateStatus == MarketplaceUpdateStatus.Available && !string.IsNullOrWhiteSpace(Version)
                ? $"已安装 {InstalledVersion} · 可更新到 {Version}"
                : $"已安装 {InstalledVersion}";

    [JsonIgnore]
    public string StarsText => Stars is { } count ? $"★ {count:N0}" : "Star 未知";

    [JsonIgnore]
    public string PublishedText => PublishedAt is { } published
        ? published.ToLocalTime().ToString("yyyy-MM-dd")
        : "发布时间未知";

    [JsonIgnore]
    public string ThemeActionText => "应用主题";
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

public sealed record ThemeReadmePreview(
    byte[]? ImageBytes,
    string? ImageUrl,
    string Message)
{
    [JsonIgnore]
    public bool HasImage => ImageBytes is { Length: > 0 };
}
