using DshLauncher.Services;
using DshLauncher.Watchdog;

namespace DshLauncher.Models;

/// <summary>崩溃原因类别（本地规则判定，不做遥测）。</summary>
public enum CrashCauseKind
{
    /// <summary>证据不足，如实标未知。</summary>
    Unknown,

    /// <summary>exitCode=0 且无失败签名：不是典型崩溃。</summary>
    NormalExit,

    /// <summary>端口被占用（EADDRINUSE 或端口现被其它进程持有）。</summary>
    PortInUse,

    /// <summary>模块/插件解析失败（bundle 找不到、ERR_MODULE_NOT_FOUND 等）。</summary>
    ModuleResolution,

    /// <summary>第三方插件运行期异常（堆栈落在用户 node_modules）。</summary>
    PluginRuntime,

    /// <summary>Node 运行时缺失或不可用。</summary>
    NodeUnavailable,

    /// <summary>内存不足（heap out of memory / ENOMEM）。</summary>
    OutOfMemory,

    /// <summary>权限不足或被安全软件拦截（EACCES/EPERM/Access denied）。</summary>
    PermissionDenied,

    /// <summary>磁盘空间不足或文件损坏（ENOSPC/JSON 解析失败）。</summary>
    DiskOrCorruption,

    /// <summary>进程被 abort（变更集 126；本机实测退出码 134）。</summary>
    ProcessAborted,

    /// <summary>启动参数非法（变更集 126；本机实测 node 参数错退出码 9）。</summary>
    InvalidLaunchArguments,

    /// <summary>被强制结束（变更集 126；本机实测 TerminateProcess 退出码 -1）。</summary>
    ForceKilled,

    /// <summary>本机级崩溃或被系统终止（变更集 126；NTSTATUS 族负值退出码，只报原始码）。</summary>
    NativeCrash
}

/// <summary>判定置信度：低置信不向用户下结论。</summary>
public enum CrashConfidence
{
    Low,
    Medium,
    High
}

/// <summary>一次崩溃的归因结果。</summary>
public sealed record CrashCause(
    CrashCauseKind Kind,
    CrashConfidence Confidence,
    string Label,
    string Evidence,
    string Advice,
    string? ActionKey = null)
{
    public static readonly CrashCause Unknown = new(
        CrashCauseKind.Unknown,
        CrashConfidence.Low,
        "未知原因",
        "未命中任何已知签名",
        "可在「运行状况」查看日志与启动证据，或导出诊断包。");

    /// <summary>低置信时只展示“未知”，不硬编原因。</summary>
    public bool IsConfident => Confidence != CrashConfidence.Low;
}

/// <summary>归因输入：全部来自已收集的崩溃现场 + 两个轻量探针。</summary>
public sealed record CrashCauseInput(
    int? ExitCode,
    IReadOnlyList<InstanceLogLine> TailLog,
    IReadOnlyList<StartupEvidence> Evidence,
    InstanceResourceSnapshot? Resource,
    int? Port,
    bool? PortOccupied,
    bool? NodeRuntimeAvailable);

/// <summary>
/// 变更集 126：一次归因的全部命中——<see cref="Primary"/> 是规则序最前（置信最高）的主因，
/// <see cref="Secondary"/> 是其余独立线索（同类只留最先命中、已剔除“未知”）。
/// 通知只显示主因；次因并入崩溃记录证据与日志，用于排查时交叉验证。
/// </summary>
public sealed record CrashCauseReport(CrashCause Primary, IReadOnlyList<CrashCause> Secondary)
{
    /// <summary>次因的标签摘要（无次因时为空串）。</summary>
    public string SecondarySummary
    {
        get
        {
            if (Secondary.Count == 0)
            {
                return string.Empty;
            }

            var labels = new string[Secondary.Count];
            for (var index = 0; index < Secondary.Count; index++)
            {
                labels[index] = Secondary[index].Label;
            }

            return string.Join("；", labels);
        }
    }
}
