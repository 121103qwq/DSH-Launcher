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

public sealed record PluginCommandProgress(
    int Resolved,
    int Reused,
    int Downloaded,
    int Added);

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
    string DisplayName,
    string InstanceName,
    int GenerationVersion = 0,
    int GenerationCount = 1)
{
    /// <summary>
    /// 同一会话目录里更早的代际数量。dsh 的模型是“一个会话目录 = 一个会话”，
    /// 目录里的 session.vN.jsonl 是同一会话的不同代际，dsh 读其中最高的一代；
    /// 对话页因此只列最高代际，并在这里标出还有多少历史代际（work-log/56）。
    /// </summary>
    public int HistoricalGenerationCount => Math.Max(0, GenerationCount - 1);

    public bool HasHistoricalGenerations => HistoricalGenerationCount > 0;

    /// <summary>列表“代际”列文案。</summary>
    public string GenerationText => HasHistoricalGenerations
        ? $"v{GenerationVersion} · 含 {HistoricalGenerationCount} 个历史代际"
        : $"v{GenerationVersion}";
}

/// <summary>会话全文检索命中项（Snippet 已把换行/制表符替换为空格）。</summary>
public sealed record ConversationSearchHit(
    ConversationEntry Entry,
    int MatchCount,
    string Snippet,
    int SnippetMatchStart,
    int SnippetMatchLength);

public sealed record ConversationBackupEntry(
    string FileName,
    string FullPath,
    string? SessionId,
    string? WorkingDirectory,
    DateTimeOffset BackedUpAt,
    long SizeBytes,
    bool IsCompressed,
    bool HasValidHeader,
    string DisplayName,
    string InstanceName);

public sealed record ModelProviderInfo(
    string Provider,
    string DisplayName,
    string SettingsNamespace,
    string? ApiKeyEnvironment,
    string? BaseUrl,
    IReadOnlyList<string> Models,
    bool Configured);
