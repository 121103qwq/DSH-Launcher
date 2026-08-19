namespace DshLauncher.Models;

public sealed record DshMarketThemeState(
    bool IsAvailable,
    IReadOnlySet<string> InstalledNames,
    IReadOnlySet<string> LiveNames,
    string? Error)
{
    public static DshMarketThemeState Unavailable(string error) =>
        new(
            false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            error);
}

public sealed record DshMarketThemeApplyResult(
    bool IsSuccess,
    IReadOnlySet<string> LiveNames,
    string? Error);

public sealed record DshMarketPluginMutationResult(
    bool IsSuccess,
    bool IsHotLoaded,
    string? Error,
    string Output);
