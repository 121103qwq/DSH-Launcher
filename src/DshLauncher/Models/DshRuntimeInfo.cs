namespace DshLauncher.Models;

public sealed record DshRuntimeInfo(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? PackageRoot,
    string? Error,
    string? NodeEngine = null,
    string? DeepSeekDesktopVersion = null,
    string? BundledNodeExecutablePath = null)
{
    public static DshRuntimeInfo Missing(string? error = null) =>
        new(false, null, null, null, error, null, null, null);

    public string VersionText => IsAvailable && !string.IsNullOrWhiteSpace(Version)
        ? Version.StartsWith('v') ? Version : $"v{Version}"
        : "未安装";

    public string NodeEngineText => string.IsNullOrWhiteSpace(NodeEngine)
        ? "package.json 未声明 engines.node"
        : NodeEngine;

    public string DisplayVersionText => !string.IsNullOrWhiteSpace(DeepSeekDesktopVersion)
        ? $"DeepSeek Desktop v{DeepSeekDesktopVersion} · DSh {VersionText}"
        : $"DSh {VersionText}";
}
