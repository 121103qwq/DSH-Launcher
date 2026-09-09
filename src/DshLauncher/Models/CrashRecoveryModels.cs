namespace DshLauncher.Models;

/// <summary>崩溃后行为（每实例设置）。</summary>
public enum CrashRecoveryPolicy
{
    /// <summary>仅通知（默认）：记录现场并提示，不自动重启。</summary>
    NotifyOnly,

    /// <summary>自动重启（受限）：退避重试，达到上限后停下并通知。</summary>
    AutoRestart,

    /// <summary>直接冷却关闭：不重启，转"冷却中"并保留现场。</summary>
    CoolDown,

    /// <summary>先自动重启，达到上限后转冷却关闭（推荐组合）。</summary>
    RestartThenCoolDown
}

/// <summary>一次崩溃的处置决定。</summary>
public enum CrashRecoveryDecision
{
    /// <summary>只记录与提示。</summary>
    NotifyOnly,

    /// <summary>延时自动重启。</summary>
    Restart,

    /// <summary>转入冷却关闭（不再自动重启，保留现场）。</summary>
    CoolDown
}

/// <summary>崩溃处置计划（纯函数产物，便于单测）。</summary>
public sealed record CrashRecoveryPlan(
    CrashRecoveryDecision Decision,
    TimeSpan Delay,
    int Attempt,
    int Limit,
    string Summary);

/// <summary>崩溃现场记录（落盘 &lt;DSH_HOME&gt;\.dsh-launcher\crash-records.jsonl，最近 10 条）。</summary>
public sealed record CrashRecord
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public int? ExitCode { get; init; }

    /// <summary>本次运行时长（从启动成功到崩溃）。</summary>
    public TimeSpan Uptime { get; init; }

    /// <summary>处置动作：仅通知 / 自动重启 / 冷却关闭。</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>计划摘要（含第几次、上限、退避等）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>dsh 最后若干行输出（最多 20 行）。</summary>
    public IReadOnlyList<string> TailLog { get; init; } = Array.Empty<string>();

    /// <summary>最近的启动/运行证据摘要。</summary>
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    /// <summary>崩溃前最后一次资源采样摘要（CPU/内存/进程数）。</summary>
    public string? Resource { get; init; }

    public string Describe()
    {
        var exit = ExitCode?.ToString() ?? "?";
        var uptime = Uptime > TimeSpan.Zero
            ? $"运行 {FormatDuration(Uptime)}"
            : "运行时长未知";
        return $"exitCode={exit}，{uptime}，{Action}";
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{value.TotalHours:0.#} 小时"
        : value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#} 分钟"
            : $"{value.TotalSeconds:0} 秒";
}

/// <summary>运行状况页展示的崩溃恢复状态。</summary>
public sealed record CrashRecoveryStatus(
    bool Cooldown,
    int Attempts,
    DateTimeOffset? LastCrashAt,
    int? LastExitCode,
    string? LastAction)
{
    public static readonly CrashRecoveryStatus Normal = new(false, 0, null, null, null);
}

/// <summary>崩溃恢复的决策规则（无副作用，可单测）。</summary>
public static class CrashRecoveryPlanner
{
    /// <summary>退避表：第 1–4 次重启用；第 5 次起固定 5 分钟。</summary>
    public static readonly TimeSpan[] RestartBackoff =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    };

    /// <summary>连续运行超过该时长后再崩溃，重启计数清零（避免"跑了一阵又崩"累计到上限）。</summary>
    public static TimeSpan StableResetWindow { get; } = TimeSpan.FromMinutes(10);

    public static TimeSpan DelayFor(int attempt) => attempt <= 0
        ? RestartBackoff[0]
        : RestartBackoff[Math.Min(attempt - 1, RestartBackoff.Length - 1)];

    public static string FormatDelay(TimeSpan delay) => delay.TotalMinutes >= 1
        ? $"{delay.TotalMinutes:0.#} 分钟"
        : $"{delay.TotalSeconds:0} 秒";

    /// <summary>
    /// 决定这次崩溃怎么处置。
    /// </summary>
    /// <param name="policy">用户设置。</param>
    /// <param name="attempts">本次崩溃前已自动重启的次数（0 = 首次崩溃）。</param>
    /// <param name="limit">重启上限（夹取 1–10）。</param>
    public static CrashRecoveryPlan Plan(CrashRecoveryPolicy policy, int attempts, int limit)
    {
        limit = Math.Clamp(limit, 1, 10);
        attempts = Math.Max(0, attempts);
        switch (policy)
        {
            case CrashRecoveryPolicy.NotifyOnly:
                return new CrashRecoveryPlan(CrashRecoveryDecision.NotifyOnly, TimeSpan.Zero, 0, limit, "仅通知（不自动重启）");

            case CrashRecoveryPolicy.CoolDown:
                return new CrashRecoveryPlan(CrashRecoveryDecision.CoolDown, TimeSpan.Zero, attempts, limit, "策略为冷却关闭（不自动重启）");

            default:
                if (attempts < limit)
                {
                    var attempt = attempts + 1;
                    var delay = DelayFor(attempt);
                    return new CrashRecoveryPlan(
                        CrashRecoveryDecision.Restart,
                        delay,
                        attempt,
                        limit,
                        $"将于 {FormatDelay(delay)} 后自动重启（第 {attempt}/{limit} 次）");
                }

                return new CrashRecoveryPlan(
                    CrashRecoveryDecision.CoolDown,
                    TimeSpan.Zero,
                    attempts,
                    limit,
                    policy == CrashRecoveryPolicy.RestartThenCoolDown
                        ? $"已达重启上限（{limit} 次），转入冷却关闭"
                        : $"已达重启上限（{limit} 次）");
        }
    }
}
