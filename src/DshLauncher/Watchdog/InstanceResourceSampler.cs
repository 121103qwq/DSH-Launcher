using System.Diagnostics;

namespace DshLauncher.Watchdog;

/// <summary>
/// 实例进程树的资源快照（CPU / 内存 / 运行时长）。
/// CPU 是相对上次采样的增量百分比（已按逻辑核心数归一），首次采样为 0。
/// </summary>
public sealed record InstanceResourceSnapshot(
    double CpuPercent,
    long WorkingSetBytes,
    TimeSpan Uptime,
    int ProcessCount,
    DateTimeOffset SampledAt);

/// <summary>进程树里单个进程的资源行（运行状况页的进程表用）。</summary>
public sealed record ProcessResourceLine(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    TimeSpan CpuTime);

/// <summary>
/// 进程树资源采样（由 Watchdog 的既有 5s 循环驱动，不额外起定时器）。
///
/// 数据来源：
///  - 进程树 = 根 PID + <see cref="ProcessQuery.GetDescendants"/>（同一份 Toolhelp32 快照）；
///  - CPU = 各进程 <c>TotalProcessorTime</c> 之和的增量 ÷ 采样间隔 ÷ 逻辑核心数；
///  - 内存 = 各进程 <c>WorkingSet64</c> 之和；
///  - 运行时长 = 根进程 <c>StartTime</c> 至今。
///
/// 全程只读，任何单个进程访问失败（已退出/权限）都跳过，不影响整棵树。
/// </summary>
public sealed class InstanceResourceSampler
{
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
    private readonly Dictionary<int, (DateTimeOffset At, TimeSpan Cpu)> _previous = new();

    /// <summary>采样一次；根进程已退出或无法访问时返回 null。</summary>
    public InstanceResourceSnapshot? Sample(int rootProcessId)
    {
        if (rootProcessId <= 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var totalCpu = TimeSpan.Zero;
        long workingSetBytes = 0;
        var processCount = 0;
        DateTimeOffset? rootStart = null;

        var pids = new List<int> { rootProcessId };
        try
        {
            pids.AddRange(ProcessQuery.GetDescendants(rootProcessId).Select(process => process.ProcessId));
        }
        catch
        {
            // 快照失败时至少采样根进程。
        }

        foreach (var pid in pids.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                totalCpu += process.TotalProcessorTime;
                workingSetBytes += process.WorkingSet64;
                processCount++;
                if (pid == rootProcessId)
                {
                    rootStart = process.StartTime;
                }
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
                // 进程刚退出或无权访问：跳过该进程。
            }
        }

        if (processCount == 0)
        {
            _previous.Remove(rootProcessId);
            return null;
        }

        var cpuPercent = 0.0;
        if (_previous.TryGetValue(rootProcessId, out var previous))
        {
            var elapsedSeconds = (now - previous.At).TotalSeconds;
            if (elapsedSeconds > 0.2)
            {
                cpuPercent = Math.Max(
                    0.0,
                    (totalCpu - previous.Cpu).TotalSeconds / elapsedSeconds / _processorCount * 100.0);
            }
        }

        _previous[rootProcessId] = (now, totalCpu);
        var uptime = rootStart is { } start && now > start ? now - start : TimeSpan.Zero;
        return new InstanceResourceSnapshot(
            Math.Round(cpuPercent, 1),
            workingSetBytes,
            uptime,
            processCount,
            now);
    }

    /// <summary>实例停止/注销时清理增量基线。</summary>
    public void Forget(int rootProcessId) => _previous.Remove(rootProcessId);

    /// <summary>进程树明细（按内存降序）；仅访问权限内的进程。</summary>
    public static IReadOnlyList<ProcessResourceLine> SampleProcesses(int rootProcessId)
    {
        if (rootProcessId <= 0)
        {
            return Array.Empty<ProcessResourceLine>();
        }

        var pids = new List<int> { rootProcessId };
        try
        {
            pids.AddRange(ProcessQuery.GetDescendants(rootProcessId).Select(process => process.ProcessId));
        }
        catch
        {
            // 快照失败时至少保留根进程。
        }

        var lines = new List<ProcessResourceLine>();
        foreach (var pid in pids.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                lines.Add(new ProcessResourceLine(
                    pid,
                    process.ProcessName,
                    process.WorkingSet64,
                    process.TotalProcessorTime));
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
                // 进程刚退出或无权访问。
            }
        }

        return lines
            .OrderByDescending(line => line.WorkingSetBytes)
            .ToArray();
    }
}
