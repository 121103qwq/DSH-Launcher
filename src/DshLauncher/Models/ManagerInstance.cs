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
    DateTimeOffset RegisteredAt)
{
    [JsonIgnore]
    public string KindText => Kind == InstanceKind.Installed ? "installed" : "source";

    [JsonIgnore]
    public string StatusText => RuntimeStatus switch
    {
        InstanceRuntimeStatus.Ready => "可用",
        InstanceRuntimeStatus.Missing => "缺少运行环境",
        InstanceRuntimeStatus.Error => "检查失败",
        InstanceRuntimeStatus.Running => "运行中",
        InstanceRuntimeStatus.Stopped => "已停止",
        _ => "待检查"
    };
}
