using System.IO;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>
/// 探针时间轴上的一条事件。**只携带白名单字段**：`raw`（原文）与 `key`（凭据片段）永不进入本记录
/// （<c>key</c> 仅在显式开启脱敏预览时进入独立的 <see cref="AuditProbeTimelineEvent.KeyPreview"/>）。
/// </summary>
public sealed record AuditProbeTimelineEvent(
    long TimeUtcMillis,
    string SessionId,
    long Sequence,
    string Type,
    string Actor,
    string HashPrefix,
    bool HasCredentialFlag,
    int? Severity,
    string? KeyPreview,
    string SourceFile);

/// <summary>探针时间轴报告（纯内存；不含原文）。</summary>
public sealed record AuditProbeTimelineReport(
    bool ProbeDetected,
    string AuditDirectory,
    IReadOnlyList<AuditProbeTimelineEvent> Events,
    int FilesRead,
    int MalformedLines,
    bool Truncated,
    bool KeyPreviewsIncluded,
    IReadOnlyList<string> Notes,
    DateTime CompletedAtUtc);

/// <summary>
/// 读取社区探针 `@marcog-h/dsh-audit` 写下的行为审计文件（#20 增量 3）。
///
/// **事实来源（2026-09-11 拉 npm tarball 读源码核实，0.1.5）**：
///   * 路径 `<DSH_HOME>/audit/<DSH_AUDIT_PROFILE|session>.jsonl` → 我们读该目录下**所有** `*.jsonl`；
///   * 每行一条 JSON：`{t, sid, seq, type, actor, h}`，命中凭据模式时追加 `flags:["credential"]`、
///     `sev`、`key`（片段）与 **`raw`（自动导出的原文，仅把完整密钥打成 `*`）**；
///   * `h` = 内容 sha256 前 16 位；只记录核心事件（user/assistant/tool 消息类）。
///
/// **安全契约**：`raw` **永不读取、永不进入返回值、永不出现在日志/导出**；
/// `key` 仅在显式开启脱敏预览时读取。读取全程只读、不联网、不写盘。
/// </summary>
public static class AuditProbeTimelineService
{
    public const string AuditDirectoryName = "audit";

    public static AuditProbeTimelineReport Run(
        string dshHome,
        bool includeKeyPreview = false,
        int maxEvents = 5000,
        long maxFileBytes = 8L * 1024 * 1024)
    {
        var notes = new List<string>();
        var events = new List<AuditProbeTimelineEvent>();
        var malformed = 0;
        var filesRead = 0;
        var truncated = false;

        if (string.IsNullOrWhiteSpace(dshHome) || !Directory.Exists(dshHome))
        {
            notes.Add("DSH_HOME 不存在，无法读取探针数据。");
            return new AuditProbeTimelineReport(
                false, string.Empty, events, 0, 0, false, includeKeyPreview, notes, DateTime.UtcNow);
        }

        var auditDirectory = Path.Combine(Path.GetFullPath(dshHome), AuditDirectoryName);
        if (!Directory.Exists(auditDirectory))
        {
            notes.Add("未检测到社区探针（缺少 audit/ 目录）。静态体检与危险配置检查不受影响。");
            return new AuditProbeTimelineReport(
                false, auditDirectory, events, 0, 0, false, includeKeyPreview, notes, DateTime.UtcNow);
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(auditDirectory, "*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add("枚举 audit/ 目录失败：" + ex.Message);
            return new AuditProbeTimelineReport(
                false, auditDirectory, events, 0, 0, false, includeKeyPreview, notes, DateTime.UtcNow);
        }

        if (files.Count == 0)
        {
            notes.Add("audit/ 目录存在但没有 .jsonl 文件（探针可能尚未写入事件）。");
            return new AuditProbeTimelineReport(
                false, auditDirectory, events, 0, 0, false, includeKeyPreview, notes, DateTime.UtcNow);
        }

        foreach (var file in files)
        {
            var length = new FileInfo(file).Length;
            if (length > maxFileBytes)
            {
                truncated = true;
                notes.Add($"{Path.GetFileName(file)} 超过 {maxFileBytes / 1024 / 1024} MB，只读取前 {maxFileBytes / 1024 / 1024} MB。");
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                filesRead++;
                var fileName = Path.GetFileName(file);
                long consumed = 0;
                while (reader.ReadLine() is { } line)
                {
                    consumed += line.Length + 1;
                    if (consumed > maxFileBytes)
                    {
                        truncated = true;
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (TryParseEvent(line, fileName, includeKeyPreview, out var parsed))
                    {
                        events.Add(parsed!);
                    }
                    else
                    {
                        malformed++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"{Path.GetFileName(file)} 读取失败：{ex.Message}");
            }
        }

        events.Sort((left, right) => left.TimeUtcMillis.CompareTo(right.TimeUtcMillis));
        if (events.Count > maxEvents)
        {
            events.RemoveRange(0, events.Count - maxEvents);
            truncated = true;
            notes.Add($"事件数超过上限，只保留最近 {maxEvents} 条。");
        }

        if (malformed > 0)
        {
            notes.Add($"有 {malformed} 行不是可解析的事件（可能是探针正在写入的残行），已跳过。");
        }

        if (events.Count == 0)
        {
            notes.Add("audit/ 目录里没有可解析的事件。");
        }

        return new AuditProbeTimelineReport(
            events.Count > 0,
            auditDirectory,
            events,
            filesRead,
            malformed,
            truncated,
            includeKeyPreview,
            notes,
            DateTime.UtcNow);
    }

    /// <summary>
    /// 解析单行事件：**逐字段白名单取值**。`raw` 从不读取；`key` 仅在开启预览时读取。
    /// </summary>
    private static bool TryParseEvent(
        string line,
        string sourceFile,
        bool includeKeyPreview,
        out AuditProbeTimelineEvent? parsed)
    {
        parsed = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            var time = ReadLong(root, "t") ?? 0;
            var sessionId = ReadString(root, "sid") ?? string.Empty;
            var sequence = ReadLong(root, "seq") ?? 0;
            var type = ReadString(root, "type") ?? "unknown";
            var actor = ReadString(root, "actor") ?? string.Empty;
            var hash = ReadString(root, "h") ?? string.Empty;
            var severity = ReadInt(root, "sev");
            var hasCredentialFlag = false;
            if (root.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array)
            {
                foreach (var flag in flags.EnumerateArray())
                {
                    if (flag.ValueKind == JsonValueKind.String
                        && string.Equals(flag.GetString(), "credential", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCredentialFlag = true;
                        break;
                    }
                }
            }

            // 白名单：只有开启预览时才取 key；raw 永不进入任何路径。
            var keyPreview = includeKeyPreview ? ReadString(root, "key") : null;

            parsed = new AuditProbeTimelineEvent(
                time,
                sessionId,
                sequence,
                type,
                actor,
                hash.Length > 12 ? hash[..12] : hash,
                hasCredentialFlag,
                severity,
                keyPreview,
                sourceFile);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? ReadInt(JsonElement element, string name)
    {
        var value = ReadLong(element, name);
        return value is null ? null : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
    }

    /// <summary>展示文本（时间 / 类型 / 触发者 / 标记 / 哈希前缀），**不含原文**。</summary>
    public static string DescribeEvent(AuditProbeTimelineEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var time = DateTimeOffset.FromUnixTimeMilliseconds(item.TimeUtcMillis).ToLocalTime();
        var flag = item.HasCredentialFlag ? " ⚠凭据标记" : string.Empty;
        var severity = item.Severity is { } sev ? $" sev={sev}" : string.Empty;
        var actor = string.IsNullOrWhiteSpace(item.Actor) ? string.Empty : $" · {item.Actor}";
        var key = item.KeyPreview is { Length: > 0 } preview ? $" · 片段 {preview}" : string.Empty;
        return $"{time:HH:mm:ss.fff} · {item.Type}{actor}{flag}{severity} · #{item.HashPrefix}{key}";
    }

    /// <summary>一行摘要。</summary>
    public static string Summarize(AuditProbeTimelineReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.ProbeDetected)
        {
            return "未检测到探针行为数据";
        }

        var credential = report.Events.Count(item => item.HasCredentialFlag);
        var sessions = report.Events.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count();
        return $"事件 {report.Events.Count} 条 · 会话 {sessions} 个 · 凭据标记 {credential} 条"
            + (report.Truncated ? " · 已截断" : string.Empty)
            + (report.KeyPreviewsIncluded ? " · 含凭据片段" : " · 不含原文与片段");
    }
}
