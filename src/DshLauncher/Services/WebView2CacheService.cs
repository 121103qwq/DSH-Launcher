using System.IO;

namespace DshLauncher.Services;

/// <summary>
/// 桌面窗口（WebView2）缓存治理（work-log/71 事故 A4）。
///
/// 事故：同一实例在浏览器里正常，只有 Desktop（WebView2）窗口报
/// "Failed to load plugins"——根因是 WebView2 自己的缓存/用户数据被污染，
/// 移开数据目录后立即恢复。为了让用户不必手动找目录，这里提供：
///   * 排队清除（下次启动启动器、WebView2 尚未创建时执行）；
///   * 立即清除（当前没有桌面窗口时可调用）。
/// 复用既有的 <see cref="WebCacheVersionLedger.ClearWebView2DiskCache"/>，不另造清理逻辑。
/// </summary>
public static class WebView2CacheService
{
    /// <summary>排队清除的标记文件（放在数据根，便于用户/支持人员定位）。</summary>
    public const string PendingFileName = "webview2-cache-clear.pending";

    public static string ResolvePendingFilePath(LauncherPaths? paths = null) =>
        Path.Combine((paths ?? new LauncherPaths()).RootDirectory, PendingFileName);

    /// <summary>排队：下次启动启动器时清除桌面窗口缓存（本次运行不动它，避免锁文件）。</summary>
    public static bool TryQueue(out string? error, LauncherPaths? paths = null)
    {
        error = null;
        var target = ResolvePendingFilePath(paths);
        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(target, DateTimeOffset.Now.ToString("O"));
            LauncherLog.Info("已排队：下次启动启动器时清除桌面窗口（WebView2）缓存。", "E3002", new { target });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>是否有待执行的清除请求。</summary>
    public static bool IsPending(LauncherPaths? paths = null) => File.Exists(ResolvePendingFilePath(paths));

    /// <summary>
    /// 立即清除桌面窗口缓存（仅在没有任何 WebView2 窗口运行时调用）。
    /// 返回删除的缓存目录数；失败不抛异常。
    /// </summary>
    public static int ClearNow()
    {
        try
        {
            var removed = WebCacheVersionLedger.ClearWebView2DiskCache();
            LauncherLog.Info($"已清除桌面窗口（WebView2）磁盘缓存：{removed} 个目录。", "E3002");
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Warn($"清除桌面窗口缓存失败：{ex.Message}", "E3002");
            return 0;
        }
    }

    /// <summary>
    /// 启动时执行排队的清除：成功或失败都会删掉标记（避免每次启动都重复尝试），
    /// 失败时记录告警并保留一次重试机会（标记保留）。
    /// </summary>
    public static void ApplyPendingAtStartup(LauncherPaths? paths = null)
    {
        var marker = ResolvePendingFilePath(paths);
        if (!File.Exists(marker))
        {
            return;
        }

        var removed = ClearNow();
        try
        {
            File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 标记删不掉不影响本次清除结果，下次启动会再试一次。
            LauncherLog.Warn($"清除标记删除失败：{ex.Message}", "E3002");
        }
    }
}
