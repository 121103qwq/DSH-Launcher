namespace DshLauncher.Models;

/// <summary>
/// Provider/model selection shared by the Launcher global default and its
/// DSh-workspace/session automation rules.
/// </summary>
public sealed record CodingModelSelection(
    string Provider,
    string Model,
    string? ReasoningEffort = null)
{
    public string Key => $"{Provider}\n{Model}\n{ReasoningEffort ?? string.Empty}";

    public string DisplayText => string.IsNullOrWhiteSpace(ReasoningEffort)
        ? $"{Provider} / {Model}"
        : $"{Provider} / {Model} · {ReasoningEffort}";
}

public sealed record CodingModelOption(
    string Provider,
    string ProviderName,
    string Model,
    string ModelName,
    string? ReasoningEffort = null,
    string? ReasoningEffortName = null)
{
    public CodingModelSelection Selection => new(Provider, Model, ReasoningEffort);

    public string Key => Selection.Key;

    public string DisplayText
    {
        get
        {
            var modelText = string.Equals(Model, ModelName, StringComparison.Ordinal)
                ? Model
                : $"{ModelName}（{Model}）";
            var baseText = $"{ProviderName} / {modelText}";
            return string.IsNullOrWhiteSpace(ReasoningEffort)
                ? baseText
                : $"{baseText} · {ReasoningEffortName ?? ReasoningEffort}";
        }
    }
}

public sealed record CodingWorkspaceModelPolicy(
    string WorkingDirectory,
    CodingModelSelection Selection);

public sealed record CodingSessionModelPolicy(
    string InstanceId,
    string SessionId,
    CodingModelSelection Selection);

public sealed class CodingModelPolicyData
{
    public CodingModelSelection? GlobalDefault { get; set; }

    public List<CodingWorkspaceModelPolicy> DshWorkspaces { get; set; } = new();

    public List<CodingSessionModelPolicy> Sessions { get; set; } = new();
}

public sealed record GlobalProviderInfo(
    string Provider,
    string DisplayName,
    string? BaseUrl,
    IReadOnlyList<string> Models,
    bool IsOfficial,
    bool HasConfigurationConflict)
{
    public string ModelCountText => $"{Models.Count} 个模型";

    public string KindText => IsOfficial ? "官方目录" : "自定义";

    public string StatusText => HasConfigurationConflict
        ? "版本间配置不一致"
        : Models.Count > 0 ? "已读取" : "尚无模型目录";
}

public sealed record DshProviderRuntimeState(
    string Provider,
    string DisplayName,
    bool Active,
    bool Declared);

public sealed class ConversationModelEntry
{
    public required ConversationEntry Conversation { get; init; }

    public required string PolicyText { get; init; }

    public string DisplayName => Conversation.DisplayName;

    public string InstanceName => Conversation.InstanceName;

    public string DshWorkspace => string.IsNullOrWhiteSpace(Conversation.WorkingDirectory)
        ? "无工作区"
        : Conversation.WorkingDirectory;
}
