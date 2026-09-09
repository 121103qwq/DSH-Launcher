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
/// </summary>
public static class CrashCauseClassifier
{
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

    public static CrashCause Classify(CrashCauseInput input)
    {
        var lines = CollectLines(input);

        if (FindFirst(lines, NodeMissingSignatures) is { } nodeHit)
        {
            return new CrashCause(
                CrashCauseKind.NodeUnavailable,
                CrashConfidence.High,
                "Node 运行时不可用",
                Describe(nodeHit),
                "到「设置 / 诊断」检查 Node.js 是否可用（可安装便携版或指定路径），然后重启实例。",
                "node-settings");
        }

        if (input.NodeRuntimeAvailable == false)
        {
            return new CrashCause(
                CrashCauseKind.NodeUnavailable,
                CrashConfidence.Medium,
                "Node 运行时不可用",
                "启动器检测不到可用的 Node.js",
                "到「设置 / 诊断」检查 Node.js 是否可用（可安装便携版或指定路径），然后重启实例。",
                "node-settings");
        }

        if (FindFirst(lines, PortSignatures) is { } portHit)
        {
            return PortInUse(portHit);
        }

        if (input.PortOccupied == true)
        {
            return new CrashCause(
                CrashCauseKind.PortInUse,
                CrashConfidence.Medium,
                "端口被占用",
                input.Port is { } occupiedPort
                    ? $"实例端口 {occupiedPort} 在崩溃后仍被其它进程占用"
                    : "实例端口在崩溃后仍被其它进程占用",
                "先「清理残留进程」再重启；若仍冲突，可能是其它程序占用了该端口。",
                "cleanup-processes");
        }

        if (FindFirst(lines, ModuleResolutionSignatures) is { } moduleHit)
        {
            return new CrashCause(
                CrashCauseKind.ModuleResolution,
                CrashConfidence.High,
                "模块/插件解析失败",
                Describe(moduleHit),
                "在「实例设置 → 运行状况 → 插件排查」里用「定位肇事插件」找出坏插件，再一键禁用。",
                "bisect-plugins");
        }

        if (FindFirst(lines, PluginRuntimeSignatures) is { } pluginHit)
        {
            return new CrashCause(
                CrashCauseKind.PluginRuntime,
                CrashConfidence.High,
                "第三方插件运行异常",
                Describe(pluginHit),
                "用「安全模式」启动确认问题来自插件，再「定位肇事插件」锁定并禁用。",
                "bisect-plugins");
        }

        if (FindFirst(lines, PluginPathSignatures) is { } stackHit)
        {
            return new CrashCause(
                CrashCauseKind.PluginRuntime,
                CrashConfidence.Medium,
                "第三方插件运行异常",
                Describe(stackHit),
                "用「安全模式」启动确认问题来自插件，再「定位肇事插件」锁定并禁用。",
                "bisect-plugins");
        }

        if (FindFirst(lines, OutOfMemorySignatures) is { } memoryHit)
        {
            return new CrashCause(
                CrashCauseKind.OutOfMemory,
                CrashConfidence.High,
                "内存不足",
                Describe(memoryHit),
                "关闭部分插件或减少并行任务；确认 dsh 的 Node 堆上限后重试。",
                null);
        }

        if (FindFirst(lines, PermissionSignatures) is { } permissionHit)
        {
            return new CrashCause(
                CrashCauseKind.PermissionDenied,
                CrashConfidence.High,
                "权限不足或被拦截",
                Describe(permissionHit),
                "把实例 dsh_home 与 DSh 运行时目录加入杀毒/安全软件白名单，再重启。",
                null);
        }

        if (FindFirst(lines, DiskSignatures) is { } diskHit)
        {
            return new CrashCause(
                CrashCauseKind.DiskOrCorruption,
                CrashConfidence.High,
                "磁盘空间不足或文件损坏",
                Describe(diskHit),
                "检查磁盘剩余空间；若是存储文件损坏，可在「快照回滚」恢复到可用快照。",
                null);
        }

        if (input.ExitCode == 0)
        {
            return new CrashCause(
                CrashCauseKind.NormalExit,
                CrashConfidence.Medium,
                "正常退出（exitCode=0）",
                "进程以退出码 0 结束，且日志没有失败签名",
                "如果不是你主动停止的，可能是外部程序关闭了它；可在「运行状况 → 运行日志」确认。",
                null);
        }

        return CrashCause.Unknown with
        {
            Evidence = input.ExitCode is { } code
                ? $"exitCode={code}，日志未命中已知签名"
                : "日志未命中已知签名"
        };
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
