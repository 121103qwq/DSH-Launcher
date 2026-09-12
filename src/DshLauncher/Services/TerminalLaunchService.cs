using System;

namespace DshLauncher.Services;

/// <summary>
/// 终端启动命令（可测，work-log/95，变更集 112）：dsh-TUI 这类插件由用户自己在终端里跑，
/// 启动器不再把它做成"启动方式"，只负责给出**能直接粘贴执行**的完整命令——含 DSH_HOME，
/// 否则 dsh 会退到默认 home（<c>~/.dsh</c>）找不到该 profile（work-log/91 的真缺陷）。
/// </summary>
public static class TerminalLaunchService
{
    /// <summary>
    /// 生成 PowerShell 命令：设置 DSH_HOME 后调用该版本的 dsh 跑指定 profile。
    /// 缺入口或 home 时返回 null（调用方提示，不猜）。
    /// <paramref name="noOpen"/> = true 时追加 <c>--no-open</c>：目标 profile 的栈里含 web-app 时会起
    /// Web UI，dsh 默认会在默认浏览器里打开它（变更集 115 实测：手敲命令弹浏览器的根因）。
    /// 该选项由 dsh-web-app 定义，纯终端面 profile 不要加。
    /// </summary>
    public static string? BuildPowerShellCommand(
        string? dshHome,
        string? dshExecutablePath,
        string? profileName,
        bool noOpen = false)
    {
        if (string.IsNullOrWhiteSpace(dshHome) || string.IsNullOrWhiteSpace(dshExecutablePath))
        {
            return null;
        }

        var profile = string.IsNullOrWhiteSpace(profileName) ? "web" : profileName!.Trim();
        var home = dshHome!.Trim().Replace("'", "''");
        var executable = dshExecutablePath!.Trim().Replace("'", "''");
        var suffix = noOpen ? " --no-open" : string.Empty;
        return $"$env:DSH_HOME='{home}'; & '{executable}' --profile {profile}{suffix}";
    }
}
