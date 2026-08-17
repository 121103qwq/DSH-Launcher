namespace DshLauncher.Models;

public enum DshRuntimeLaunchMode
{
    DirectCommand,
    NodeScript,
    ElectronBootstrap
}

/// <summary>
/// Describes how to invoke a physical @deepseek-ai/dsh package. The package
/// root remains the runtime identity; this record only describes its host and
/// entry points so normal npm installs and packaged desktop applications can
/// share the same lifecycle code.
/// </summary>
public sealed record DshRuntimeLaunchSpec(
    DshRuntimeLaunchMode Mode,
    string HostPath,
    string? EntryPointPath = null,
    string? NodeExecutablePath = null,
    string? PnpmScriptPath = null,
    string? ProductName = null,
    string? ProductVersion = null)
{
    public bool UsesPackagedNode => Mode == DshRuntimeLaunchMode.ElectronBootstrap
        || !string.IsNullOrWhiteSpace(NodeExecutablePath);

    public bool SupportsDesktopShell => Mode == DshRuntimeLaunchMode.ElectronBootstrap
        && !string.IsNullOrWhiteSpace(ProductName);
}
