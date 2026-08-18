namespace DshLauncher.Models;

public enum ConversationSyncMode
{
    Independent,
    Workspace,
    All
}

public enum PluginInstallMode
{
    Fast,
    Compatibility
}

public enum VersionOpenMode
{
    Launcher,
    Desktop
}

public sealed class VersionSettingsData
{
    public bool SyncAllConfiguration { get; set; }

    public ConversationSyncMode ConversationSyncMode { get; set; } = ConversationSyncMode.Independent;

    public string? ConversationWorkspace { get; set; }

    public bool SyncModelProviders { get; set; } = true;

    public string? WindowTitle { get; set; }

    public string? NodeExecutablePath { get; set; }

    /// <summary>
    /// Null keeps the legacy behavior: a detected DSH Desktop runtime opens as
    /// a desktop window, while normal DSh runtimes use Launcher web startup.
    /// </summary>
    public VersionOpenMode? OpenMode { get; set; }
}

/// <summary>
/// Launcher 级设置：作用域是全部版本，而不是某一个版本的 DSH_HOME。
/// </summary>
public sealed class LauncherSettingsData
{
    public bool SyncAllConfiguration { get; set; }

    public List<string> Workspaces { get; set; } = new();

    public PluginInstallMode PluginInstallMode { get; set; } = PluginInstallMode.Fast;

    /// <summary>
    /// Optional npm global prefix used only for the Launcher-managed DSh runtime.
    /// Instance data remains isolated under each instance's DSH_HOME.
    /// </summary>
    public string? DshInstallDirectory { get; set; }
}

public sealed record VersionExportOptions(
    bool IncludeProviderConfiguration,
    bool IncludePluginConfiguration);
