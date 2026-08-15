namespace DshLauncher.Models;

public enum ExtensionKind
{
    Plugin,
    Skill,
    Mcp,
    Workflow,
    Preset
}

public sealed record ExtensionEntry(
    string Id,
    ExtensionKind Kind,
    string Name,
    string? Version,
    string? Description,
    string Location,
    bool Enabled,
    bool Managed);

public sealed record McpServerDefinition(
    string ServerName,
    string Transport,
    string Command,
    IReadOnlyList<string> Arguments,
    string? Url,
    IReadOnlyDictionary<string, string> Headers,
    string? WorkingDirectory,
    bool Enabled = true);

public sealed record ConversationEntry(
    string RelativePath,
    string FullPath,
    string? SessionId,
    string? WorkingDirectory,
    DateTimeOffset UpdatedAt,
    long SizeBytes,
    bool IsCompressed,
    bool HasValidHeader,
    string DisplayName);

public sealed record ModelProviderInfo(
    string Provider,
    string DisplayName,
    string SettingsNamespace,
    string? ApiKeyEnvironment,
    string? BaseUrl,
    IReadOnlyList<string> Models,
    bool Configured);
