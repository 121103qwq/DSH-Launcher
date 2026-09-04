using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Reads resources for an instance's recorded process and identifiable
/// descendants. Each call is one sample; callers own polling and no timer or
/// long-lived monitoring thread is created. The service has no mutating API,
/// so Attached instances remain read-only as well.
/// </summary>
public sealed class InstanceResourceMonitor
{
    private const uint SnapshotProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    /// <summary>Samples the current process tree without a previous CPU baseline.</summary>
    public Task<InstanceResourceSnapshot> SampleAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default) =>
        SampleAsync(instance, previousSnapshot: null, cancellationToken: cancellationToken);

    /// <summary>
    /// Samples the current process tree on a thread-pool thread. Supplying the
    /// prior result enables CPU percentage calculation without retaining
    /// monitor state between calls.
    /// </summary>
    public Task<InstanceResourceSnapshot> SampleAsync(
        ManagerInstance instance,
        InstanceResourceSnapshot? previousSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return Task.Run(
            () => SampleCore(instance, previousSnapshot, cancellationToken),
            cancellationToken);
    }

    private static InstanceResourceSnapshot SampleCore(
        ManagerInstance instance,
        InstanceResourceSnapshot? previousSnapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampledAt = DateTimeOffset.UtcNow;
        var processId = instance.ProcessId;
        if (processId is not > 0)
        {
            return InstanceResourceSnapshot.Unavailable(
                processId,
                instance.RuntimeOwnership,
                "实例没有记录有效的进程 ID，资源状态不可用。",
                sampledAt);
        }

        Process rootProcess;
        try
        {
            rootProcess = Process.GetProcessById(processId.Value);
        }
        catch (Exception ex) when (IsProcessReadException(ex))
        {
            return InstanceResourceSnapshot.Unavailable(
                processId,
                instance.RuntimeOwnership,
                $"进程 {processId.Value} 不存在或不可读取，资源状态不可用：{ex.Message}",
                sampledAt);
        }

        using (rootProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rootMeasurement = TryReadMeasurement(rootProcess);
            if (rootMeasurement is null)
            {
                return InstanceResourceSnapshot.Unavailable(
                    processId,
                    instance.RuntimeOwnership,
                    $"进程 {processId.Value} 已退出或不可读取，资源状态不可用。",
                    sampledAt);
            }

            var processIds = FindProcessTree(processId.Value, cancellationToken);
            var measurements = new List<ProcessMeasurement> { rootMeasurement.Value };
            foreach (var childId in processIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (childId == processId.Value)
                {
                    continue;
                }

                try
                {
                    using var childProcess = Process.GetProcessById(childId);
                    var childMeasurement = TryReadMeasurement(childProcess);
                    if (childMeasurement is not null)
                    {
                        measurements.Add(childMeasurement.Value);
                    }
                }
                catch (Exception ex) when (IsProcessReadException(ex))
                {
                    // A child can exit between enumeration and sampling. The
                    // root remains authoritative; unreadable descendants are
                    // simply omitted from this point-in-time aggregate.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            sampledAt = DateTimeOffset.UtcNow;
            var totalProcessorTime = SumProcessorTime(measurements);
            var workingSetBytes = SumWorkingSet(measurements);
            TimeSpan? runtimeDuration = rootMeasurement.Value.StartedAt is { } startedAt
                ? MaxZero(sampledAt - startedAt)
                : null;
            var cpuUsagePercent = CalculateCpuUsage(
                previousSnapshot,
                processId.Value,
                processIds,
                totalProcessorTime,
                sampledAt);

            return new InstanceResourceSnapshot(
                processId,
                instance.RuntimeOwnership,
                InstanceResourceStatus.Available,
                totalProcessorTime,
                cpuUsagePercent,
                workingSetBytes,
                runtimeDuration,
                sampledAt,
                processIds,
                unavailableReason: null);
        }
    }

    private static ProcessMeasurement? TryReadMeasurement(Process process)
    {
        try
        {
            var processorTime = process.TotalProcessorTime;
            var workingSetBytes = process.WorkingSet64;
            DateTimeOffset? startedAt = null;
            try
            {
                startedAt = new DateTimeOffset(process.StartTime).ToUniversalTime();
            }
            catch (Exception ex) when (IsProcessReadException(ex))
            {
                // CPU and working set can still be useful when start time is
                // unavailable due to permissions or a process race.
            }

            return new ProcessMeasurement(processorTime, workingSetBytes, startedAt);
        }
        catch (Exception ex) when (IsProcessReadException(ex))
        {
            return null;
        }
    }

    private static IReadOnlyList<int> FindProcessTree(
        int rootProcessId,
        CancellationToken cancellationToken)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return [rootProcessId];
        }

        try
        {
            var entry = new PROCESSENTRY32
            {
                Size = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };
            if (!Process32First(snapshot, ref entry))
            {
                return [rootProcessId];
            }

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processId = entry.ProcessId;
                var parentProcessId = entry.ParentProcessId;
                if (processId <= int.MaxValue
                    && parentProcessId <= int.MaxValue
                    && processId > 0
                    && processId != parentProcessId)
                {
                    if (!childrenByParent.TryGetValue((int)parentProcessId, out var children))
                    {
                        children = [];
                        childrenByParent[(int)parentProcessId] = children;
                    }

                    children.Add((int)processId);
                }
            }
            while (Process32Next(snapshot, ref entry));

            var result = new List<int> { rootProcessId };
            var seen = new HashSet<int> { rootProcessId };
            var pending = new Queue<int>();
            pending.Enqueue(rootProcessId);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentProcessId = pending.Dequeue();
                if (!childrenByParent.TryGetValue(parentProcessId, out var children))
                {
                    continue;
                }

                foreach (var childProcessId in children)
                {
                    if (!seen.Add(childProcessId))
                    {
                        continue;
                    }

                    result.Add(childProcessId);
                    pending.Enqueue(childProcessId);
                }
            }

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static TimeSpan SumProcessorTime(IEnumerable<ProcessMeasurement> measurements)
    {
        var ticks = 0L;
        foreach (var measurement in measurements)
        {
            ticks = checked(ticks + measurement.TotalProcessorTime.Ticks);
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static long SumWorkingSet(IEnumerable<ProcessMeasurement> measurements)
    {
        var bytes = 0L;
        foreach (var measurement in measurements)
        {
            bytes = checked(bytes + measurement.WorkingSetBytes);
        }

        return bytes;
    }

    private static double? CalculateCpuUsage(
        InstanceResourceSnapshot? previousSnapshot,
        int processId,
        IReadOnlyList<int> processIds,
        TimeSpan totalProcessorTime,
        DateTimeOffset sampledAt)
    {
        if (previousSnapshot is null
            || !previousSnapshot.IsAvailable
            || previousSnapshot.ProcessId != processId
            || !previousSnapshot.ProcessIds.SequenceEqual(processIds)
            || previousSnapshot.TotalProcessorTime is not { } previousProcessorTime)
        {
            return null;
        }

        var elapsedSeconds = (sampledAt - previousSnapshot.SampledAt).TotalSeconds;
        var processorSeconds = (totalProcessorTime - previousProcessorTime).TotalSeconds;
        if (elapsedSeconds <= 0 || processorSeconds < 0)
        {
            return null;
        }

        var logicalProcessorCount = Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp(
            processorSeconds / elapsedSeconds / logicalProcessorCount * 100,
            0,
            100);
    }

    private static TimeSpan MaxZero(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static bool IsProcessReadException(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or ObjectDisposedException
            or UnauthorizedAccessException
            or NotSupportedException
            or Win32Exception;

    private readonly record struct ProcessMeasurement(
        TimeSpan TotalProcessorTime,
        long WorkingSetBytes,
        DateTimeOffset? StartedAt);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static bool Process32First(nint snapshot, ref PROCESSENTRY32 entry) =>
        Process32FirstW(snapshot, ref entry);

    private static bool Process32Next(nint snapshot, ref PROCESSENTRY32 entry) =>
        Process32NextW(snapshot, ref entry);
}
