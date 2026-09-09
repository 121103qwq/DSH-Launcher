using System.Diagnostics;

namespace DshLauncher.Services;

/// <summary>
/// GitHub 直装插件的进程级 git 重写（借鉴 MarcoG-h/DSH-Launcher 的 GIT_CONFIG_*
/// 方案）：只对本次 <c>dsh plugin</c> 子进程注入 <c>url.&lt;镜像&gt;.insteadOf</c>，
/// 不读写用户的全局/系统 git 配置。镜像可用性会漂移，失败时由调用方依次尝试。
/// </summary>
internal static class GitMirrorEnvironment
{
    internal sealed record GitMirror(string Name, string Base);

    /// <summary>失败时依次尝试的 GitHub 镜像（顺序即优先级）。</summary>
    internal static readonly GitMirror[] Mirrors =
    {
        new("gh-proxy.com", "https://gh-proxy.com/https://github.com/"),
        new("gitclone.com", "https://gitclone.com/github.com/")
    };

    /// <summary>禁用 git 的交互式凭据提示，避免插件命令在无人值守时挂起。</summary>
    internal static void ApplyNoPrompt(ProcessStartInfo startInfo)
    {
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    /// <summary>把 https://github.com/ 的拉取重写到指定镜像（进程级，不改用户配置）。</summary>
    internal static void ApplyMirror(ProcessStartInfo startInfo, GitMirror mirror)
    {
        startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = $"url.{mirror.Base}.insteadOf";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = "https://github.com/";
        ApplyNoPrompt(startInfo);
    }
}
