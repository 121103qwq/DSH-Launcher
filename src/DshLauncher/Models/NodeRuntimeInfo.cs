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
}
