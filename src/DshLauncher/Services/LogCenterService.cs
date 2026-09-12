using System.IO;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>日志中心的一条记录（`launcher.log` 的结构化行）。</summary>
public sealed record LogCenterEntry(
    DateTimeOffset Utc,
    string Level,
    string? Code,
    string Message,
    string? Instance,
    string? InstanceId)
{
    /// <summary>本地日期（按天分组的键）。</summary>
    public DateTime LocalDay => Utc.ToLocalTime().Date;

    /// <summary>本地时间 HH:mm:ss（列表左侧）。</summary>
    public string LocalTimeText => Utc.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>按天分组的一组记录。</summary>
public sealed record LogDayGroup(DateTime Day, IReadOnlyList<LogCenterEntry> Entries);

/// <summary>日志中心读取结果（含坏行统计与尾部截断标记）。</summary>
public sealed record LogCenterSnapshot(
    IReadOnlyList<LogCenterEntry> Entries,
    int SkippedLines,
    int TotalLines,
    bool Truncated);

/// <summary>
/// 日志中心数据层（work-log/86，变更集 103）：**只读** `launcher.log`（含轮转旧文件
/// `launcher.log.old`），解析结构化行、按天分组、按级别/关键字/实例过滤。
///
/// 约束：
///   * 只读——不写入、不删除、不轮转（删除仍走「存储与清理」）；
///   * 文件可能正被 Launcher 写入 → 以 FileShare.ReadWrite 打开，坏行跳过并计数；
///   * 尾部上限（默认 5000 条），超出时置 Truncated（界面注明"仅最近 N 条"）；
///   * 无 WPF 依赖，便于自测。
/// </summary>
public static class LogCenterService
{
    public const int DefaultMaxEntries = 5000;

    public static string LogDirectory => LauncherLog.LogDirectory;

    /// <summary>读取 launcher.log（+ 轮转旧文件），按时间新→旧返回。</summary>
    public static LogCenterSnapshot Load(int maxEntries = DefaultMaxEntries)
    {
        var entries = new List<LogCenterEntry>();
        var skipped = 0;
        var total = 0;

        foreach (var file in EnumerateLogFiles())
        {
            foreach (var line in ReadLinesSafe(file))
            {
                total++;
                if (TryParse(line, out var entry))
                {
                    entries.Add(entry);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    skipped++;
                }
            }
        }

        entries.Sort((left, right) => right.Utc.CompareTo(left.Utc));
        var truncated = entries.Count > maxEntries;
        if (truncated)
        {
            entries = entries.Take(maxEntries).ToList();
        }

        return new LogCenterSnapshot(entries, skipped, total, truncated);
    }

    /// <summary>旧轮转文件在前、当前文件在后（解析后统一按时间排序，这里只保证都读到）。</summary>
    private static IEnumerable<string> EnumerateLogFiles()
    {
        var current = LauncherLog.LogPath;
        var rotated = current + ".old";
        if (File.Exists(rotated))
        {
            yield return rotated;
        }

        if (File.Exists(current))
        {
            yield return current;
        }
    }

    /// <summary>逐行读取；文件被占用/IO 失败时不抛（日志中心绝不能反过来弄崩界面）。</summary>
    private static IEnumerable<string> ReadLinesSafe(string path)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 读不到就当空；界面会显示"暂时没有日志"。
        }

        return lines;
    }

    public static bool TryParse(string line, out LogCenterEntry entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("utc", out var utcProperty)
                || utcProperty.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(utcProperty.GetString(), out var utc))
            {
                return false;
            }

            var level = root.TryGetProperty("level", out var levelProperty) && levelProperty.ValueKind == JsonValueKind.String
                ? levelProperty.GetString() ?? "INFO"
                : "INFO";
            var code = root.TryGetProperty("code", out var codeProperty) && codeProperty.ValueKind == JsonValueKind.String
                ? codeProperty.GetString()
                : null;
            var message = root.TryGetProperty("msg", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String
                ? messageProperty.GetString() ?? string.Empty
                : string.Empty;

            string? instance = null;
            string? instanceId = null;
            if (root.TryGetProperty("ctx", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                if (context.TryGetProperty("instance", out var instanceProperty) && instanceProperty.ValueKind == JsonValueKind.String)
                {
                    instance = instanceProperty.GetString();
                }

                if (context.TryGetProperty("instanceId", out var instanceIdProperty) && instanceIdProperty.ValueKind == JsonValueKind.String)
                {
                    instanceId = instanceIdProperty.GetString();
                }
            }

            entry = new LogCenterEntry(utc, level, code, message, instance, instanceId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 过滤（null / 空 = 不筛）：
    ///   * <paramref name="level"/> 精确匹配级别（INFO/WARN/ERROR）；
    ///   * <paramref name="keyword"/> 在消息 / 错误码 / 实例名里模糊匹配（忽略大小写）；
    ///   * <paramref name="instance"/> 匹配实例名或实例 ID（精确）。
    /// </summary>
    public static IReadOnlyList<LogCenterEntry> Filter(
        IEnumerable<LogCenterEntry> entries,
        string? level = null,
        string? keyword = null,
        string? instance = null)
    {
        var query = entries;
        if (!string.IsNullOrWhiteSpace(level))
        {
            query = query.Where(entry => string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(entry =>
                entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (entry.Code?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                || (entry.Instance?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(instance))
        {
            query = query.Where(entry =>
                string.Equals(entry.Instance, instance, StringComparison.Ordinal)
                || string.Equals(entry.InstanceId, instance, StringComparison.Ordinal));
        }

        return query.ToList();
    }

    /// <summary>可选实例列表（下拉用）：去重、按名称排序；不含 null/空。</summary>
    public static IReadOnlyList<string> CollectInstances(IEnumerable<LogCenterEntry> entries) =>
        entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Instance))
            .Select(entry => entry.Instance!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>按本地日期分组（新→旧；组内也新→旧）。</summary>
    public static IReadOnlyList<LogDayGroup> GroupByDay(IEnumerable<LogCenterEntry> entries) =>
        entries
            .GroupBy(entry => entry.LocalDay)
            .OrderByDescending(group => group.Key)
            .Select(group => new LogDayGroup(
                group.Key,
                group.OrderByDescending(entry => entry.Utc).ToList()))
            .ToList();
}
