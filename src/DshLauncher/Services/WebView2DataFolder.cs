using System.IO;

namespace DshLauncher.Services;

/// <summary>
/// WebView2 用户数据目录的定位（Chat 窗口 / 缓存账本共用）。
///
/// 默认位置是 exe 同目录的 &lt;exe 名&gt;.WebView2（WebView2 的约定），但把 Launcher
/// 放进只读目录（如 Program Files）时无法创建。此时回退到
/// %LocalAppData%\DeepSeek\launcher\WebView2，保证 Desktop 窗口仍可用。
/// </summary>
public static class WebView2DataFolder
{
    public const string FolderSuffix = ".WebView2";

    /// <summary>回退位置（exe 同目录不可写时使用）。</summary>
    public static string FallbackDirectory(string? localApplicationData = null)
    {
        var localData = string.IsNullOrWhiteSpace(localApplicationData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationData;
        return Path.Combine(localData, "DeepSeek", "launcher", "WebView2");
    }

    /// <summary>exe 同目录的默认数据目录（无论是否存在/可写）。</summary>
    public static string DefaultDirectory(string executableDirectory, string executableName) =>
        Path.Combine(executableDirectory, executableName + FolderSuffix);

    /// <summary>解析实际可用的 WebView2 用户数据目录。</summary>
    public static string Resolve(
        string executableDirectory,
        string executableName,
        string? localApplicationData = null)
    {
        var preferred = DefaultDirectory(executableDirectory, executableName);
        if (IsWritable(preferred))
        {
            return preferred;
        }

        var fallback = FallbackDirectory(localApplicationData);
        LauncherLog.Warn(
            "exe 同目录不可写，WebView2 数据目录已回退到用户本地目录（Desktop 窗口仍可用）。",
            ErrorCodes.E1010,
            new { preferred, fallback });
        return fallback;
    }

    /// <summary>当前进程使用的 WebView2 用户数据目录（含回退判断）。</summary>
    public static string ResolveForCurrentProcess()
    {
        var executableName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            executableName = "DSH Launcher.exe";
        }

        return Resolve(AppContext.BaseDirectory, executableName);
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".dsh-write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
