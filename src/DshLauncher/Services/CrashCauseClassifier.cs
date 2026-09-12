using System.Net;
using System.Net.Sockets;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 崩溃原因归类：在崩溃现场（退出码、日志尾部、启动证据、资源采样）上跑一张**有序规则表**，
/// 先精确签名、后辅助信号，给出"最可能原因 + 命中依据 + 建议"。低置信一律标未知，绝不硬编原因。
///
/// 规则只读 dsh/stderr 来源的日志行（与 <see cref="StartupLogClassifier"/> 同一克制口径），
/// 判定完全本地，不做遥测。
///
/// 变更集 126 两处增强：
/// ① **退出码专项映射**（见 <see cref="MatchExitCode"/>）——只用**本机实测过**的码，不做未验证的码表推断；
/// ② **多因复合**（见 <see cref="ClassifyAll"/>）——主因＝规则序最前，其余独立线索作为次因并入证据与日志。
/// </summary>
public static class CrashCauseClassifier
{
    /// <summary>规则输入：崩溃现场 + 已收集的 dsh/stderr 日志行。</summary>
    private sealed record RuleContext(CrashCauseInput Input, IReadOnlyList<string> Lines);

    private static readonly string[] NodeMissingSignatures =
    {
        "'node' is not recognized",
        "node: not found",
        "node.exe: not found",
        "the term 'node' is not recognized",
        "无法将“node”项识别为"
    };

    private static readonly string[] PortSignatures = { "eaddrinuse", "address already in use", "端口被占用" };

    private static readonly string[] ModuleResolutionSignatures =
    {
        "cannot resolve profile bundle",
        "err_module_not_found",
        "cannot find module",
        "cannot find package",
        "module not found",
        "failed to resolve"
    };

    private static readonly string[] PluginRuntimeSignatures =
    {
        "plugin tree failed to load",
        "failed to apply loader entry"
    };

    private static readonly string[] OutOfMemorySignatures =
    {
        "javascript heap out of memory",
        "out of memory",
        "enomem",
        "allocation failed"
    };

    private static readonly string[] PermissionSignatures =
    {
        "eacces",
        "eperm",
        "access is denied",
        "permission denied",
        "拒绝访问",
        "操作被拒绝"
    };

    private static readonly string[] DiskSignatures =
    {
        "enospc",
        "no space left",
        "disk full",
        "磁盘空间不足",
        "malformed-medium",
        "unexpected end of json",
        "unterminated string in json"
    };

    /// <summary>
    /// 有序规则表（变更集 42 的规则顺序保持不变）：命中在前的规则即为主因。
    /// 变更集 126 在签名规则之后、正常退出之前插入退出码规则：签名比退出码更精确，所以签名优先。
    /// </summary>
    private static readonly Func<RuleContext, CrashCause?>[] Rules =
    {
        MatchNodeSignature,
        MatchNodeUnavailable,
        MatchPortSignature,
        MatchPortOccupied,
        MatchModuleResolution,
        MatchPluginRuntimeSignature,
        MatchPluginRuntimePath,
        MatchOutOfMemory,
        MatchPermissionDenied,
        MatchDiskOrCorruption,
        MatchExitCode,
        MatchNormalExit
    };

    /// <summary>端口是否仍被占用（用于端口类归因的辅助信号；探测异常返回 null）。</summary>
    public static bool? ProbePortOccupied(int? port)
    {
        if (port is not { } value || value is <= 0 or > 65535)
        {
            return null;
        }

        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, value);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>单因归类（只取主因）；需要次因请用 <see cref="ClassifyAll"/>。</summary>
    public static CrashCause Classify(CrashCauseInput input) => ClassifyAll(input).Primary;

    /// <summary>
    /// 变更集 126：多因复合——按规则序收集**全部**命中，主因是第一条，其余作为次因返回。
    /// 同类（<see cref="CrashCauseKind"/>）只保留最先命中的那条；兜底的"未知"不进次因。
    /// </summary>
    public static CrashCauseReport ClassifyAll(CrashCauseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var context = new RuleContext(input, CollectLines(input));
        var hits = new List<CrashCause>();
        foreach (var rule in Rules)
        {
            if (rule(context) is not { } cause)
            {
                continue;
            }

            if (hits.Any(existing => existing.Kind == cause.Kind))
            {
                continue;
            }

            if (cause.Kind == CrashCauseKind.Unknown)
            {
                // 兜底规则：未知没有可复合的信息，命中即停（且只在没有其它命中时保留）。
                if (hits.Count == 0)
                {
                    hits.Add(cause);
                }

                break;
            }

            hits.Add(cause);
        }

        if (hits.Count == 0)
        {
            hits.Add(CrashCause.Unknown with { Evidence = "日志未命中已知签名" });
        }

        return new CrashCauseReport(hits[0], hits.Skip(1).ToArray());
    }

    private static CrashCause? MatchNodeSignature(RuleContext context)
    {
        if (FindFirst(context.Lines, NodeMissingSignatures) is not { } nodeHit)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.NodeUnavailable,
            CrashConfidence.High,
            "Node 运行时不可用",
            Describe(nodeHit),
            "到「设置 / 诊断」检查 Node.js 是否可用（可安装便携版或指定路径），然后重启实例。",
            "node-settings");
    }

    private static CrashCause? MatchNodeUnavailable(RuleContext context)
    {
        if (context.Input.NodeRuntimeAvailable != false)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.NodeUnavailable,
            CrashConfidence.Medium,
            "Node 运行时不可用",
            "启动器检测不到可用的 Node.js",
            "到「设置 / 诊断」检查 Node.js 是否可用（可安装便携版或指定路径），然后重启实例。",
            "node-settings");
    }

    private static CrashCause? MatchPortSignature(RuleContext context) =>
        FindFirst(context.Lines, PortSignatures) is { } portHit ? PortInUse(portHit) : null;

    private static CrashCause? MatchPortOccupied(RuleContext context)
    {
        if (context.Input.PortOccupied != true)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.PortInUse,
            CrashConfidence.Medium,
            "端口被占用",
            context.Input.Port is { } occupiedPort
                ? $"实例端口 {occupiedPort} 在崩溃后仍被其它进程占用"
                : "实例端口在崩溃后仍被其它进程占用",
            "先「清理残留进程」再重启；若仍冲突，可能是其它程序占用了该端口。",
            "cleanup-processes");
    }

    private static CrashCause? MatchModuleResolution(RuleContext context)
    {
        if (FindFirst(context.Lines, ModuleResolutionSignatures) is not { } moduleHit)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.ModuleResolution,
            CrashConfidence.High,
            "模块/插件解析失败",
            Describe(moduleHit),
            "在「实例设置 → 运行状况 → 插件排查」里用「定位肇事插件」找出坏插件，再一键禁用。",
            "bisect-plugins");
    }

    private static CrashCause? MatchPluginRuntimeSignature(RuleContext context)
    {
        if (FindFirst(context.Lines, PluginRuntimeSignatures) is not { } pluginHit)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.PluginRuntime,
            CrashConfidence.High,
            "第三方插件运行异常",
            Describe(pluginHit),
            "用「安全模式」启动确认问题来自插件，再「定位肇事插件」锁定并禁用。",
            "bisect-plugins");
    }

    private static CrashCause? MatchPluginRuntimePath(RuleContext context)
    {
        if (FindFirst(context.Lines, PluginPathSignatures) is not { } stackHit)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.PluginRuntime,
            CrashConfidence.Medium,
            "第三方插件运行异常",
            Describe(stackHit),
            "用「安全模式」启动确认问题来自插件，再「定位肇事插件」锁定并禁用。",
            "bisect-plugins");
    }

    private static CrashCause? MatchOutOfMemory(RuleContext context)
    {
        if (FindFirst(context.Lines, OutOfMemorySignatures) is not { } memoryHit)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.OutOfMemory,
            CrashConfidence.High,
            "内存不足",
            Describe(memoryHit),
            "关闭部分插件或减少并行任务；确认 dsh 的 Node 堆上限后重试。",
            null);
    }

    private static CrashCause? MatchPermissionDenied(RuleContext context)
    {
        if (FindFirst(context.Lines, PermissionSignatures) is not { } permissionHit)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.PermissionDenied,
            CrashConfidence.High,
            "权限不足或被拦截",
            Describe(permissionHit),
            "把实例 dsh_home 与 DSh 运行时目录加入杀毒/安全软件白名单，再重启。",
            null);
    }

    private static CrashCause? MatchDiskOrCorruption(RuleContext context)
    {
        if (FindFirst(context.Lines, DiskSignatures) is not { } diskHit)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.DiskOrCorruption,
            CrashConfidence.High,
            "磁盘空间不足或文件损坏",
            Describe(diskHit),
            "检查磁盘剩余空间；若是存储文件损坏，可在「快照回滚」恢复到可用快照。",
            null);
    }

    /// <summary>
    /// 变更集 126：退出码专项映射——只用**本机实测过**的码，不做未验证的码表推断（无 Windows SDK 头可作权威来源）。
    /// 实测环境 node v24.18.1 / Windows：
    /// <list type="bullet">
    /// <item>0 = 正常退出（交 <see cref="MatchNormalExit"/>）</item>
    /// <item>1 = 通用失败码：未捕获异常、缺模块、栈溢出、自 SIGINT/SIGTERM 全都落这里 → **单看码无法定位**，仍标未知</item>
    /// <item>134 = <c>process.abort()</c>（V8 致命错误路径）</item>
    /// <item>9 = 命令行参数非法</item>
    /// <item>-1 = 被 <c>TerminateProcess</c> 强制结束（.NET <c>Kill()</c> 实测；`taskkill /F` 则表现为 1，无法与一般失败区分）</item>
    /// <item>其它负值 = NTSTATUS 族（本机级崩溃或被系统终止）——**只报原始码，不下具体结论**</item>
    /// </list>
    /// </summary>
    private static CrashCause? MatchExitCode(RuleContext context)
    {
        if (context.Input.ExitCode is not { } code || code == 0)
        {
            return null;
        }

        if (code == 134)
        {
            return new CrashCause(
                CrashCauseKind.ProcessAborted,
                CrashConfidence.High,
                "进程被中止（abort）",
                "exitCode=134（本机实测：node 进程 abort 的退出码）",
                "这类退出多来自运行时遇到无法恢复的致命错误：先切换或升级 Node 版本，再禁用最近新增的插件后重试。",
                null);
        }

        if (code == 9)
        {
            return new CrashCause(
                CrashCauseKind.InvalidLaunchArguments,
                CrashConfidence.High,
                "启动参数非法",
                "exitCode=9（本机实测：node 命令行参数非法的退出码）",
                "检查实例启动参数与 dsh 版本是否匹配（例如 dsh-tui 不认 --no-open 这类选项），必要时重置实例启动参数。",
                null);
        }

        if (code == -1)
        {
            return new CrashCause(
                CrashCauseKind.ForceKilled,
                CrashConfidence.Medium,
                "进程被强制结束",
                "exitCode=-1（本机实测：TerminateProcess 强制结束；taskkill /F 则表现为 1，无法区分）",
                "若不是你主动停止的，检查是否有清理工具、任务管理器或安全软件强制结束了进程；可先「清理残留进程」再重启。",
                "cleanup-processes");
        }

        if (code < 0)
        {
            return new CrashCause(
                CrashCauseKind.NativeCrash,
                CrashConfidence.Medium,
                "本机级崩溃或被系统终止",
                $"exitCode=0x{unchecked((uint)code):X8}（NTSTATUS 族；本机未复现，故只报原始码）",
                "这类退出通常来自运行时/驱动/安全软件层面：先更新或切换 Node/DSh 运行时，再把实例目录加入杀软白名单，最后导出诊断包。",
                null);
        }

        if (code == 1)
        {
            return new CrashCause(
                CrashCauseKind.Unknown,
                CrashConfidence.Low,
                "以退出码 1 结束",
                "exitCode=1（本机实测：node 的通用失败码——未捕获异常、缺模块、栈溢出、自 SIGTERM 都会落在这里，单看码无法定位）",
                "到「运行状况 → 运行日志」看最后 20 行，必要时用「定位肇事插件」排查，或导出诊断包。",
                null);
        }

        return new CrashCause(
            CrashCauseKind.Unknown,
            CrashConfidence.Low,
            $"以退出码 {code} 结束",
            $"exitCode={code}（不在实测码表内，按未知处理）",
            "到「运行状况 → 运行日志」看最后 20 行，或导出诊断包。",
            null);
    }

    private static CrashCause? MatchNormalExit(RuleContext context)
    {
        if (context.Input.ExitCode != 0)
        {
            return null;
        }

        return new CrashCause(
            CrashCauseKind.NormalExit,
            CrashConfidence.Medium,
            "正常退出（exitCode=0）",
            "进程以退出码 0 结束，且日志没有失败签名",
            "如果不是你主动停止的，可能是外部程序关闭了它；可在「运行状况 → 运行日志」确认。",
            null);
    }

    /// <summary>堆栈落在用户 web profile 的第三方依赖目录里 → 很可能是插件运行期异常。</summary>
    private static readonly string[] PluginPathSignatures =
    {
        "profiles\\web\\node_modules\\",
        "profiles/web/node_modules/"
    };

    private static CrashCause PortInUse(string evidenceLine) => new(
        CrashCauseKind.PortInUse,
        CrashConfidence.High,
        "端口被占用",
        Describe(evidenceLine),
        "先「清理残留进程」再重启；若仍冲突，可能是其它程序占用了该端口。",
        "cleanup-processes");

    /// <summary>只取 dsh/stderr 来源、且有内容的行（与启动健康检查同一口径）。</summary>
    private static List<string> CollectLines(CrashCauseInput input)
    {
        var lines = new List<string>();
        foreach (var line in input.TailLog)
        {
            if (!string.Equals(line.Source, "dsh", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(line.Source, "stderr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                lines.Add(line.Text);
            }
        }

        return lines;
    }

    private static string? FindFirst(IEnumerable<string> lines, IReadOnlyList<string> signatures)
    {
        foreach (var line in lines)
        {
            foreach (var signature in signatures)
            {
                if (line.Contains(signature, StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }
        }

        return null;
    }

    private static string Describe(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..160] + "…";
    }
}
