using System;
using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public enum LauncherTaskStatus
{
    Waiting,
    Running,
    Succeeded,
    Failed,
    Canceled
}

/// <summary>
/// A thread-safe, UI-independent description of one Launcher task.
/// Progress is a percentage from 0 to 100; null means indeterminate.
/// </summary>
public sealed record LauncherTaskSnapshot(
    Guid Id,
    string Title,
    string Category,
    LauncherTaskStatus Status,
    double? Progress,
    string StatusMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    bool CanCancel,
    bool CanRetry)
{
    [JsonIgnore]
    public DateTimeOffset Time => UpdatedAt;

    [JsonIgnore]
    public DateTimeOffset? FinishedAt => CompletedAt;

    [JsonIgnore]
    public bool IsCompleted => Status is LauncherTaskStatus.Succeeded
        or LauncherTaskStatus.Failed
        or LauncherTaskStatus.Canceled;

    [JsonIgnore]
    public bool IsRunning => Status is LauncherTaskStatus.Waiting or LauncherTaskStatus.Running;

    [JsonIgnore]
    public LauncherTaskStatus TaskStatus => Status;

    [JsonIgnore]
    public double? ProgressPercent => Progress;
}
