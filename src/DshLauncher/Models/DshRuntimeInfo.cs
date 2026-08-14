namespace DshLauncher.Models;

public sealed record DshRuntimeInfo(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? PackageRoot,
    string? Error)
{
    public static DshRuntimeInfo Missing(string? error = null) =>
        new(false, null, null, null, error);

    public string VersionText => IsAvailable && !string.IsNullOrWhiteSpace(Version)
        ? Version.StartsWith('v') ? Version : $"v{Version}"
        : "未安装";
}
