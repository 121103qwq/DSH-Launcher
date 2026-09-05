using DshLauncher.Models;

namespace DshLauncher.Services;

public enum ThemeCapabilityStatus
{
    Unknown,
    Unsupported,
    Supported
}

public sealed record ThemeCapabilityProbeResult(
    ThemeCapabilityStatus Status,
    string Reason,
    string? Preference = null,
    int? Revision = null)
{
    public bool IsSupported => Status == ThemeCapabilityStatus.Supported;

    public static ThemeCapabilityProbeResult Unknown(string reason) =>
        new(ThemeCapabilityStatus.Unknown, reason);

    public static ThemeCapabilityProbeResult Unsupported(string reason) =>
        new(ThemeCapabilityStatus.Unsupported, reason);

    public static ThemeCapabilityProbeResult Supported(
        string preference,
        int? revision,
        string reason) =>
        new(ThemeCapabilityStatus.Supported, reason, preference, revision);
}

public sealed record ThemeSyncResult(bool IsSuccess, string Reason);

/// <summary>
/// Owns the Extension page's dsh-market theme state and the optional Chat
/// linkage. The page remains responsible for ordinary Plugin interactions.
/// </summary>
public sealed class ThemeIntegrationController : IDisposable
{
    private readonly DshMarketThemeService _marketService;
    private readonly bool _ownsMarketService;
    private bool _disposed;

    public ThemeIntegrationController(DshMarketThemeService? marketService = null)
    {
        _marketService = marketService ?? new DshMarketThemeService();
        _ownsMarketService = marketService is null;
    }

    public bool UseDshMarketHotReload { get; private set; } = true;

    public long ProfileGeneration { get; private set; }

    public DshMarketThemeState MarketState { get; private set; } =
        DshMarketThemeState.Unavailable("尚未检测当前实例的 dsh-market。 ");

    public ThemeCapabilityProbeResult ChatCapability { get; private set; } =
        ThemeCapabilityProbeResult.Unknown("尚未探测当前实例的 Chat 主题能力。 ");

    public void SetUseDshMarketHotReload(bool enabled) => UseDshMarketHotReload = enabled;

    public long BeginProfileSelection(string profileName)
    {
        ProfileGeneration++;
        ChatCapability = string.Equals(profileName, "web", StringComparison.OrdinalIgnoreCase)
            ? ThemeCapabilityProbeResult.Unknown("正在探测当前 Profile 的 Chat 主题能力。 ")
            : ThemeCapabilityProbeResult.Unsupported(
                "Chat 主题联动只作用于 web profile，当前 Profile 不支持。 ");
        return ProfileGeneration;
    }

    public bool IsCurrentProfile(long generation) => generation == ProfileGeneration;

    public bool SetChatCapability(
        ThemeCapabilityProbeResult capability,
        long? profileGeneration = null)
    {
        if (profileGeneration is not null && profileGeneration != ProfileGeneration)
        {
            return false;
        }

        ChatCapability = capability;
        return true;
    }

    public DshMarketThemeState MarkMarketUnavailable(string reason)
    {
        MarketState = DshMarketThemeState.Unavailable(reason);
        return MarketState;
    }

    public async Task<DshMarketThemeState> ReadMarketAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        MarketState = await _marketService.ReadAsync(instance, cancellationToken);
        return MarketState;
    }

    public async Task<ThemeCapabilityProbeResult> ProbeChatCapabilityAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default,
        long? profileGeneration = null)
    {
        var capability = await _marketService.ProbeThemeCapabilityAsync(instance, cancellationToken);
        SetChatCapability(capability, profileGeneration);
        return capability;
    }

    public async Task<DshMarketThemeApplyResult> ApplyThemeAsync(
        ManagerInstance instance,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        var result = await _marketService.ApplyAsync(instance, packageName, cancellationToken);
        if (result.IsSuccess)
        {
            MarketState = MarketState with { LiveNames = result.LiveNames };
        }

        return result;
    }

    public Task<DshMarketPluginMutationResult> InstallPluginAsync(
        ManagerInstance instance,
        string catalogUrl,
        CancellationToken cancellationToken = default) =>
        _marketService.InstallPluginAsync(instance, catalogUrl, cancellationToken);

    public Task<DshMarketPluginMutationResult> UpdatePluginAsync(
        ManagerInstance instance,
        string packageName,
        CancellationToken cancellationToken = default) =>
        _marketService.UpdatePluginAsync(instance, packageName, cancellationToken);

    public async Task<ThemeSyncResult> SyncChatThemeAsync(
        string? address,
        CancellationToken cancellationToken = default)
    {
        if (!ChatCapability.IsSupported)
        {
            return new ThemeSyncResult(false, ChatCapability.Reason);
        }

        if (!ChatWindow.TryGetOpenChat(address, out _))
        {
            return new ThemeSyncResult(false, "已检测到 Chat 主题能力，但当前没有打开的 Chat 窗口。 ");
        }

        var synced = await ChatWindow.TrySyncOpenChatAsync(address, cancellationToken);
        return synced
            ? new ThemeSyncResult(true, "已触发 Chat 主题同步。 ")
            : new ThemeSyncResult(false, "Chat 窗口尚未完成加载，未触发主题同步。 ");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsMarketService)
        {
            _marketService.Dispose();
        }
    }
}
