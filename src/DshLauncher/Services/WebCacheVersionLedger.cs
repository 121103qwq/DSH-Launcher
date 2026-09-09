using System.IO;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>
/// WebView2 缓存失效的版本账本（借鉴 Ruler4396/dsh-launcher 的 WebCacheVersionLedger，MIT）。
///
/// 背景：Chat 窗口的 WebView2 用户数据目录是跨实例共享的。dsh 升级后前端资源
/// 指纹变化，旧磁盘缓存可能被复用（白屏/旧界面/与新 token 机制冲突）。
/// 这里记录"上次清理决策时见到的 dsh 版本"：版本变化时清一次磁盘缓存。
///
/// 语义与安全铁律：
/// - 无账本（首次运行）→ 只记录基线，不清（避免首启无谓清缓存）；
/// - 版本为空/不可判 → 不清、不写（一次探测失败绝不能抹掉既有基线）；
/// - 读写失败只降级（Warn + 返回），绝不打断启动链路。
/// 文件：&lt;Launcher 数据根&gt;\webcache-version.json（{ version, at }）。
/// </summary>
public sealed class WebCacheVersionLedger
{
    private readonly string _path;

    public WebCacheVersionLedger(LauncherPaths? paths = null)
    {
        _path = System.IO.Path.Combine((paths ?? new LauncherPaths()).RootDirectory, "webcache-version.json");
    }

    public string FilePath => _path;

    public string? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path, Encoding.UTF8));
            var version = document.RootElement.TryGetProperty("version", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LauncherLog.Warn("webcache-version.json 损坏或不可读，按无基线处理（本次不清缓存）。",
                ErrorCodes.E1002, new { path = _path, error = ex.Message });
            return null;
        }
    }

    public void Write(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(new
            {
                version = version.Trim(),
                at = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LauncherLog.Warn("记录 WebView2 缓存版本基线失败（下次启动按旧基线决策）。",
                ErrorCodes.E1002, new { path = _path, error = ex.Message });
        }
    }

    /// <summary>
    /// 版本变化时清理 WebView2 磁盘缓存。返回是否真的执行了清理。
    /// 调用时机：打开 Chat 窗口前、且当前没有其它 Chat 窗口（避免删除正在使用的缓存）。
    /// </summary>
    public bool EnsureCacheMatches(string? currentVersion, Action<string>? trace = null)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return false;
        }

        var baseline = Read();
        if (baseline is null)
        {
            Write(currentVersion);
            return false;
        }

        if (string.Equals(baseline, currentVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var removed = ClearWebView2DiskCache();
        Write(currentVersion);
        LauncherLog.Info("dsh 版本变化，已清理 WebView2 磁盘缓存。", ErrorCodes.E1002,
            new { from = baseline, to = currentVersion, removedDirectories = removed });
        trace?.Invoke($"dsh 版本 {baseline} → {currentVersion}，已清理 WebView2 缓存（{removed} 个目录）。");
        return true;
    }

    /// <summary>
    /// 删除 WebView2 用户数据目录下的纯缓存子目录（保留 Cookie/登录态/配置）。
    /// 被占用的文件跳过；返回成功删除的目录数。
    /// </summary>
    public static int ClearWebView2DiskCache()
    {
        var removed = 0;
        foreach (var userDataFolder in EnumerateWebView2UserDataFolders())
        {
            var defaultFolder = Path.Combine(userDataFolder, "EBWebView", "Default");
            foreach (var relative in new[]
                     {
                         "Cache",
                         "Code Cache",
                         "GPUCache",
                         "DawnGraphiteCache",
                         "DawnWebGPUCache",
                         "ShaderCache",
                         "Service Worker"
                     })
            {
                var target = Path.Combine(defaultFolder, relative);
                if (!Directory.Exists(target))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(target, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 缓存目录可能被残留 WebView2 进程占用：跳过，下次版本变化再清。
                    LauncherLog.Warn("清理 WebView2 缓存目录失败（可能被占用）。", ErrorCodes.E1002,
                        new { path = target, error = ex.Message });
                }
            }
        }

        return removed;
    }

    private static IEnumerable<string> EnumerateWebView2UserDataFolders()
    {
        var results = new List<string>();
        var baseDirectory = AppContext.BaseDirectory;
        var exeName = System.IO.Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(exeName))
        {
            var candidate = System.IO.Path.Combine(baseDirectory, exeName + ".WebView2");
            if (Directory.Exists(candidate))
            {
                results.Add(candidate);
            }
        }

        // 兜底：exe 旁的任意 *.WebView2 目录（开发期 dll 启动、重命名发布物等）。
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(baseDirectory, "*.WebView2"))
            {
                if (!results.Contains(directory, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 枚举失败无需处理。
        }

        return results;
    }
}
