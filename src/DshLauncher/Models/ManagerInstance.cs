using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public enum InstanceKind
{
    Installed,
    Source
}

public enum InstanceRuntimeStatus
{
    Unknown,
    Ready,
    Missing,
    Error,
    Running,
    Stopped
}

public enum InstanceRuntimeOwnership
{
    None,
    Managed,
    Attached
}

public sealed record ManagerInstance(
    string Id,
    string Name,
    string RootPath,
    InstanceKind Kind,
    string DshHome,
    string? DshExecutablePath,
    string? DetectedVersion,
    InstanceRuntimeStatus RuntimeStatus,
    string? PackageManager,
    string? LastError,
    DateTimeOffset RegisteredAt,
    int? ProcessId = null,
    int? Port = null,
    string? WebUrl = null)
{
    [JsonIgnore]
    public string KindText => Kind == InstanceKind.Installed ? "installed" : "source";

    [JsonIgnore]
    public InstanceRuntimeOwnership RuntimeOwnership { get; init; }

    [JsonIgnore]
    public string RuntimeOwnershipText => RuntimeOwnership switch
    {
        InstanceRuntimeOwnership.Managed => "Launcher 管理",
        InstanceRuntimeOwnership.Attached => "外部服务",
        _ => "未连接"
    };

    [JsonIgnore]
    public string StatusText => RuntimeStatus switch
    {
        InstanceRuntimeStatus.Ready => "可用",
        InstanceRuntimeStatus.Missing => "缺少运行环境",
        InstanceRuntimeStatus.Error => "检查失败",
        InstanceRuntimeStatus.Running when RuntimeOwnership == InstanceRuntimeOwnership.Attached => "已连接（外部）",
        InstanceRuntimeStatus.Running when RuntimeOwnership == InstanceRuntimeOwnership.Managed => "运行中（Launcher）",
        InstanceRuntimeStatus.Running => "运行中",
        InstanceRuntimeStatus.Stopped => "已停止",
        _ => "待检查"
    };
}
