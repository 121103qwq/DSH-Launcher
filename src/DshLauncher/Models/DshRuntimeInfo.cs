namespace DshLauncher.Models;

public sealed record DshRuntimeInfo(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? PackageRoot,
    string? Error,
    string? NodeEngine = null,
    string? DeepSeekDesktopVersion = null,
    string? BundledNodeExecutablePath = null,
    DshRuntimeLaunchSpec? LaunchSpec = null)
{
    public static DshRuntimeInfo Missing(string? error = null) =>
        new(false, null, null, null, error, null, null, null, null);

    public DshRuntimeLaunchSpec? EffectiveLaunchSpec => LaunchSpec
        ?? (string.IsNullOrWhiteSpace(ExecutablePath)
            ? null
            : new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.DirectCommand,
                ExecutablePath,
                NodeExecutablePath: BundledNodeExecutablePath,
                ProductName: string.IsNullOrWhiteSpace(DeepSeekDesktopVersion) ? null : "DeepSeek Desktop",
                ProductVersion: DeepSeekDesktopVersion));

    public string VersionText => IsAvailable && !string.IsNullOrWhiteSpace(Version)
        ? Version.StartsWith('v') ? Version : $"v{Version}"
        : "未安装";

    public string NodeEngineText => string.IsNullOrWhiteSpace(NodeEngine)
        ? "package.json 未声明 engines.node"
        : NodeEngine;

    public string DisplayVersionText => !string.IsNullOrWhiteSpace(EffectiveLaunchSpec?.ProductName)
        ? $"{EffectiveLaunchSpec.ProductName}"
            + (string.IsNullOrWhiteSpace(EffectiveLaunchSpec.ProductVersion) ? string.Empty : $" v{EffectiveLaunchSpec.ProductVersion}")
            + $" · DSh {VersionText}"
        : $"DSh {VersionText}";

    public string SuggestedInstanceName
    {
        get
        {
            var normalizedVersion = string.IsNullOrWhiteSpace(Version)
                ? "默认版本"
                : Version.Trim().TrimStart('v');
            var dshName = $"DSh {normalizedVersion}";
            var productName = EffectiveLaunchSpec?.ProductName;
            var productVersion = EffectiveLaunchSpec?.ProductVersion;
            return string.IsNullOrWhiteSpace(productName)
                ? dshName
                : productName
                    + (string.IsNullOrWhiteSpace(productVersion) ? string.Empty : $" {productVersion}")
                    + $" · {dshName}";
        }
    }
}
