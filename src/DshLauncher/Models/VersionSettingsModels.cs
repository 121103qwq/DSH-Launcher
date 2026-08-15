namespace DshLauncher.Models;

public enum ConversationSyncMode
{
    Independent,
    Workspace,
    All
}

public sealed class VersionSettingsData
{
    public bool SyncAllConfiguration { get; set; }

    public ConversationSyncMode ConversationSyncMode { get; set; } = ConversationSyncMode.Independent;

    public string? ConversationWorkspace { get; set; }

    public bool SyncModelProviders { get; set; } = true;

    public string? WindowTitle { get; set; }

    public string? NodeExecutablePath { get; set; }
}

public sealed record VersionExportOptions(
    bool IncludeProviderConfiguration,
    bool IncludePluginConfiguration);
