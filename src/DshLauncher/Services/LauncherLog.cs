using System.IO;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

/// <summary>
/// 轻量结构化日志（借鉴 Ruler4396/dsh-launcher 的统一日志 + 错误码设计，MIT）。
/// 每行一条 JSON：{ utc, level, code, msg, ctx }，写入
/// %LocalAppData%\DeepSeek\launcher\launcher.log（1MB 轮转 → launcher.log.old）。
/// 只记录 Launcher 自身事件；dsh 子进程输出仍由实例日志/运行输出承载。
/// 任何写入失败都静默降级——日志绝不能反过来弄崩 Launcher。
/// 目录可用环境变量 <c>DSH_LAUNCHER_LOG_ROOT</c> 覆盖（harness 用它把测试条目写进临时目录，
/// 不再污染用户真实 launcher.log，见 work-log/54）。
/// </summary>
public static class LauncherLog
{
    /// <summary>日志目录覆盖变量；空值/无值时用 LocalAppData 默认目录。</summary>
    public const string LogRootVariable = "DSH_LAUNCHER_LOG_ROOT";

    private const long MaxBytes = 1_000_000;
    private static readonly object Sync = new();

    public static string LogDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(LogRootVariable);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeek", "launcher")
                : Path.GetFullPath(overridden);
        }
    }

    public static string LogPath => Path.Combine(LogDirectory, "launcher.log");

    public static void Info(string message, string? code = null, object? context = null) =>
        Write("INFO", message, code, context);

    public static void Warn(string message, string? code = null, object? context = null) =>
        Write("WARN", message, code, context);

    public static void Error(string message, string? code = null, object? context = null) =>
        Write("ERROR", message, code, context);

    private static void Write(string level, string message, string? code, object? context)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                var line = JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow.ToString("O"),
                    level,
                    code,
                    msg = message,
                    ctx = context
                });
                File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志失败不影响主流程。
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            var oldPath = LogPath + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            File.Move(LogPath, oldPath);
        }
        catch
        {
            // 轮转失败时继续追加（文件可能暂时被占用）。
        }
    }
}
