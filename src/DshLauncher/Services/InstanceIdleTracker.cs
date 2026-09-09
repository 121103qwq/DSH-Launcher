using System.IO;

namespace DshLauncher.Services;

public sealed record InstanceActivity(string Source, DateTimeOffset At);

/// <summary>
/// 实例空闲判定：记录每个实例最近一次"活动"（CPU / 外部连接 / 数据文件写入 /
/// dsh 日志输出 / Launcher 操作），供"空闲自动停止"使用。
///
/// 铁律：存在后台任务时绝不能算空闲——正在等 LLM 响应时 CPU 可能是 0、
/// 也没有文件写入，因此还必须看**到非回环地址的已建立连接**。
/// </summary>
public sealed class InstanceIdleTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InstanceActivity> _last = new(StringComparer.Ordinal);

    public void MarkActivity(string? instanceId, string source, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        var when = at ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_last.TryGetValue(instanceId, out var current) && current.At >= when)
            {
                return;
            }

            _last[instanceId] = new InstanceActivity(source, when);
        }
    }

    public InstanceActivity? GetLastActivity(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        lock (_gate)
        {
            return _last.TryGetValue(instanceId, out var activity) ? activity : null;
        }
    }

    public void Forget(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            _last.Remove(instanceId);
        }
    }

    public bool IsIdle(string? instanceId, TimeSpan threshold, DateTimeOffset now) =>
        GetLastActivity(instanceId) is not { } activity || now - activity.At >= threshold;

    /// <summary>
    /// 是否应自动停止（纯函数，便于测试）：
    /// 开关未开、非 Launcher 托管、Launcher 自身在忙（安装/更新/准备运行环境等）、
    /// 存在后台任务（外部连接）——任一命中都不停。
    /// </summary>
    public static bool ShouldAutoStop(
        bool enabled,
        bool managed,
        bool launcherBusy,
        bool hasBackgroundTask,
        InstanceActivity? lastActivity,
        TimeSpan threshold,
        DateTimeOffset now)
    {
        if (!enabled || !managed || launcherBusy || hasBackgroundTask)
        {
            return false;
        }

        return lastActivity is null || now - lastActivity.At >= threshold;
    }
}

/// <summary>数据文件活动探针：sessions/ 与 storages/ 下最近一次写入时间。</summary>
public static class InstanceActivityProbe
{
    public const int MaximumFilesScanned = 4000;

    /// <summary>最近一次数据写入时间（无文件/无法访问返回 null）。</summary>
    public static DateTimeOffset? GetLastDataWriteTime(string? dshHome)
    {
        if (string.IsNullOrWhiteSpace(dshHome) || !Directory.Exists(dshHome))
        {
            return null;
        }

        DateTimeOffset? newest = null;
        var scanned = 0;
        foreach (var relative in new[] { "sessions", "storages" })
        {
            var root = Path.Combine(dshHome, relative);
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (++scanned > MaximumFilesScanned)
                    {
                        break;
                    }

                    try
                    {
                        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                        if (newest is null || written > newest)
                        {
                            newest = written;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件不可读跳过。
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录不可枚举跳过。
            }

            if (scanned > MaximumFilesScanned)
            {
                break;
            }
        }

        return newest;
    }
}
