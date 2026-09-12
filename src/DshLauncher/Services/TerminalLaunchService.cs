using System;
using System.IO;

namespace DshLauncher.Services;

/// <summary>
/// 「终端启动」的目录解析（可测，work-log/92，变更集 109）。
/// dsh-TUI 用**进程 cwd** 当工作区（插件没有 --cwd / --workspace 参数），所以启动目录即 TUI 工作区。
/// </summary>
public static class TerminalLaunchService
{
    /// <summary>解析启动目录：显式设置（且真实存在）→ 回退目录（用户主目录）；返回绝对路径。</summary>
    public static string ResolveWorkingDirectory(string? configured, string? fallbackDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured!);
        }

        if (!string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            return Path.GetFullPath(fallbackDirectory!);
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
