namespace DshLauncher.Models;

public enum VersionHealthState
{
    Healthy,
    Warning,
    Error
}

public sealed record VersionHealthItem(
    string Id,
    string Title,
    VersionHealthState State,
    string Detail,
    bool Repairable = false)
{
    public string StatusText => State switch
    {
        VersionHealthState.Healthy => "正常",
        VersionHealthState.Warning => "需注意",
        _ => "异常"
    };

    public string DisplayText => $"{Title} · {StatusText}\n{Detail}";
}

public sealed record VersionHealthReport(
    string InstanceId,
    DateTimeOffset CheckedAt,
    IReadOnlyList<VersionHealthItem> Items)
{
    public int ErrorCount => Items.Count(item => item.State == VersionHealthState.Error);

    public int WarningCount => Items.Count(item => item.State == VersionHealthState.Warning);

    public int RepairableCount => Items.Count(item => item.Repairable);

    public string Summary => ErrorCount > 0
        ? $"发现 {ErrorCount} 个异常、{WarningCount} 个提醒"
        : WarningCount > 0
            ? $"基础运行条件正常，另有 {WarningCount} 个提醒"
            : "版本检查通过";
}

public sealed record VersionRepairResult(
    ManagerInstance Instance,
    IReadOnlyList<string> Actions);

public sealed record VersionSnapshotInfo(
    string FilePath,
    DateTimeOffset CreatedAt,
    string Reason,
    long Size)
{
    public string DisplayName => $"{CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Reason}";
}
