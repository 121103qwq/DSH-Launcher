namespace DshLauncher.Models;

public sealed record NodeRuntimeInfo(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? Error)
{
    public static NodeRuntimeInfo Missing(string? error = null) =>
        new(false, null, null, error);

    public string VersionText => IsAvailable && !string.IsNullOrWhiteSpace(Version)
        ? $"v{Version}"
        : "未安装";

    public bool IsCompatibleWithDshSource => IsAvailable
        && Version is not null
        && System.Version.TryParse(Version, out var parsed)
        && ((parsed.Major == 22 && parsed >= new Version(22, 19, 0)) || parsed.Major >= 24);
}
