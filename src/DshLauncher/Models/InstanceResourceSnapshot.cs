using System;
using System.Collections.Generic;
using System.Linq;

namespace DshLauncher.Models;

public enum InstanceResourceStatus
{
    Unavailable,
    Available
}

/// <summary>
/// A point-in-time, read-only view of the process tree belonging to an
/// instance. CPU percentage is only populated when a previous snapshot with
/// the same process tree is supplied to the monitor.
/// </summary>
public sealed record InstanceResourceSnapshot
{
    public InstanceResourceSnapshot(
        int? processId,
        InstanceRuntimeOwnership runtimeOwnership,
        InstanceResourceStatus status,
        TimeSpan? totalProcessorTime,
        double? cpuUsagePercent,
        long? workingSetBytes,
        TimeSpan? runtimeDuration,
        DateTimeOffset sampledAt,
        IEnumerable<int>? processIds,
        string? unavailableReason)
    {
        ProcessId = processId;
        RuntimeOwnership = runtimeOwnership;
        Status = status;
        TotalProcessorTime = totalProcessorTime;
        CpuUsagePercent = cpuUsagePercent;
        WorkingSetBytes = workingSetBytes;
        RuntimeDuration = runtimeDuration;
        SampledAt = sampledAt;
        ProcessIds = processIds is null
            ? Array.Empty<int>()
            : Array.AsReadOnly(processIds.Distinct().OrderBy(static id => id).ToArray());
        UnavailableReason = unavailableReason;
    }

    public int? ProcessId { get; }

    public InstanceRuntimeOwnership RuntimeOwnership { get; }

    public InstanceResourceStatus Status { get; }

    public bool IsAvailable => Status == InstanceResourceStatus.Available;

    /// <summary>Total CPU time accumulated by the root and readable descendants.</summary>
    public TimeSpan? TotalProcessorTime { get; }

    /// <summary>
    /// CPU usage normalized to the machine's logical processor count. It is
    /// null for the first sample or while the process tree changes.
    /// </summary>
    public double? CpuUsagePercent { get; }

    public long? WorkingSetBytes { get; }

    /// <summary>Elapsed time since the root process started.</summary>
    public TimeSpan? RuntimeDuration { get; }

    public DateTimeOffset SampledAt { get; }

    /// <summary>The root PID and every identifiable descendant PID.</summary>
    public IReadOnlyList<int> ProcessIds { get; }

    public int ProcessCount => ProcessIds.Count;

    /// <summary>
    /// Non-null only when the process could not be sampled. The monitor never
    /// turns an access or process-lifetime error into a successful snapshot.
    /// </summary>
    public string? UnavailableReason { get; }

    public static InstanceResourceSnapshot Unavailable(
        int? processId,
        InstanceRuntimeOwnership runtimeOwnership,
        string reason,
        DateTimeOffset sampledAt) =>
        new(
            processId,
            runtimeOwnership,
            InstanceResourceStatus.Unavailable,
            totalProcessorTime: null,
            cpuUsagePercent: null,
            workingSetBytes: null,
            runtimeDuration: null,
            sampledAt,
            processIds: null,
            unavailableReason: reason);
}
