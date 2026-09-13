namespace DshLauncher.Models;

/// <summary>
/// A single entry from the DSH-PackForge market index. The index is only a
/// discovery source; an entry is installable only when its download metadata
/// passes the market service validation rules.
/// </summary>
public sealed class ModPackMarketEntry
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public long Size { get; init; }
    public bool IsInstallable { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    public string? PackageType { get; init; }
}
