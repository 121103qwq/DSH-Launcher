namespace DshLauncher.Services;

/// <summary>启动健康证据的来源层（借鉴 Ruler4396 的四层证据设计，MIT；本实现按本仓结构重写）。</summary>
public enum BootLayer
{
    /// <summary>进程层：启动后进程提前退出/消失。</summary>
    Process,

    /// <summary>日志层：dsh 输出命中启动失败签名。</summary>
    Log,

    /// <summary>HTTP 层：健康检查探测失败。</summary>
    Http,

    /// <summary>页面层：WebView2 页面加载/探针失败（只采集与提示，不单独判死）。</summary>
    Page
}

public sealed record StartupEvidence(BootLayer Layer, string Summary, string? Detail = null)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

public sealed record StartupVerdict(
    bool IsHealthy,
    string? ErrorCode,
    string Summary,
    IReadOnlyList<StartupEvidence> Evidence)
{
    public static StartupVerdict Healthy(IReadOnlyList<StartupEvidence> evidence) =>
        new(true, null, "启动健康检查通过。", evidence);

    public static StartupVerdict Failed(string summary, IReadOnlyList<StartupEvidence> evidence) =>
        new(false, ErrorCodes.E1015, summary, evidence);

    /// <summary>把证据压成一行摘要（用于日志与提示）。</summary>
    public string DescribeEvidence() =>
        string.Join(
            "；",
            Evidence.Select(item => item.Detail is { Length: > 0 } detail
                ? $"[{item.Layer}] {item.Summary}（{detail}）"
                : $"[{item.Layer}] {item.Summary}"));
}

/// <summary>
/// 启动日志的失败签名识别。只扫 dsh/stderr 来源；命中的第一行即作为证据。
/// 签名表保持克制，避免把正常运行日志误判为失败。
/// </summary>
public static class StartupLogClassifier
{
    private static readonly (string Signature, string Description)[] Signatures =
    {
        ("plugin tree failed to load", "插件树加载失败"),
        ("failed to apply loader entry", "Loader 条目应用失败"),
        ("malformed-medium", "存储文件损坏（JSON 解析失败）"),
        ("cannot find module", "模块缺失"),
        ("eaddrinuse", "端口被占用"),
        ("uncaught exception", "未捕获异常"),
        ("unhandled rejection", "未处理的 Promise 拒绝"),
        ("syntaxerror", "语法错误"),
        ("eacces", "权限不足"),
        ("eperm", "操作不被允许"),
        ("fatal error", "致命错误")
    };

    /// <summary>在日志行里找第一条失败签名；没有则返回 null。</summary>
    public static (string Signature, string Description, InstanceLogLine Line)? FindFailure(
        IEnumerable<InstanceLogLine> lines)
    {
        foreach (var line in lines)
        {
            if (!string.Equals(line.Source, "dsh", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(line.Source, "stderr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = line.Text;
            if (text.Length == 0)
            {
                continue;
            }

            foreach (var (signature, description) in Signatures)
            {
                if (text.Contains(signature, StringComparison.OrdinalIgnoreCase))
                {
                    return (signature, description, line);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 只保留 <paramref name="since"/> 之后新增的日志行。实例日志是跨启动保留的环形缓冲，
    /// 启动健康检查必须带上本次进程的起始时间作基线，否则上一轮的失败签名会被反复命中。
    /// </summary>
    public static IReadOnlyList<InstanceLogLine> Since(
        IEnumerable<InstanceLogLine> lines,
        DateTimeOffset since)
    {
        var result = new List<InstanceLogLine>();
        foreach (var line in lines)
        {
            if (line.At >= since)
            {
                result.Add(line);
            }
        }

        return result;
    }
}
