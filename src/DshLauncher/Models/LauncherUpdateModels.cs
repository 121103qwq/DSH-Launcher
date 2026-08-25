namespace DshLauncher.Models;

public sealed record LauncherReleaseInfo(
    string TagName,
    Version Version,
    string Name,
    string Notes,
    DateTimeOffset PublishedAt,
    string? AssetUrl,
    long AssetSize,
    string? Sha256)
{
    public bool CanInstall => AssetSize is >= 1L * 1024 * 1024 and <= 512L * 1024 * 1024
        && Uri.TryCreate(AssetUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && Sha256 is { Length: 64 }
        && Sha256.All(Uri.IsHexDigit);

    public string DisplayText => $"{TagName} · {PublishedAt:yyyy-MM-dd}"
        + (CanInstall ? string.Empty : " · 缺少可验证附件");
}

public sealed record LauncherUpdateApplyRequest(
    string TargetPath,
    int WaitProcessId,
    string ExpectedSha256);
