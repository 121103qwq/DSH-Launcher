using System.IO;
using System.Text.Json;

namespace DshLauncher.Watchdog;

/// <summary>
/// 台账文件读写。台账是 Watchdog 的"记忆"：Launcher 启动实例时登记，
/// Watchdog 每次识别/转正/停止后落盘——Launcher 崩溃后重开时据此无缝续管。
/// 文件原子写（临时文件 + move）。
/// </summary>
public sealed class WatchdogStateStore
{
    public string StatePath { get; }

    public WatchdogStateStore(string stateDirectory)
    {
        StatePath = Path.Combine(stateDirectory, "watchdog-state.json");
    }

    public WatchdogState Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath, System.Text.Encoding.UTF8);
                return JsonSerializer.Deserialize<WatchdogState>(json, WatchdogProtocol.Json)
                    ?? new WatchdogState();
            }
        }
        catch
        {
            // 台账损坏不阻断：按空台账启动，靠 Launcher 登记重建。
        }

        return new WatchdogState();
    }

    public void Save(WatchdogState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(StatePath)
                ?? throw new InvalidOperationException("台账文件没有父目录。");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(state, WatchdogProtocol.Json);
            File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(false));
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        catch
        {
            // 台账写失败不致命：Watchdog 仍以内存台账工作，收尾仍执行。
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch
        {
            // 清理失败交给下次启动覆盖。
        }
    }
}

/// <summary>诊断日志：%LocalAppData%\DeepSeek\launcher\watchdog.log（防无限增长，1MB 轮转）。</summary>
public sealed class WatchdogLog
{
    private readonly object _gate = new();
    private readonly string _path;

    public WatchdogLog(string stateDirectory)
    {
        _path = Path.Combine(stateDirectory, "watchdog.log");
    }

    /// <summary>warning = 需要用户知道的（幽灵/转正/清理）；info = 常规；debug 不落盘。</summary>
    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024)
                {
                    try
                    {
                        File.Move(_path, $"{_path}.1", overwrite: true);
                    }
                    catch
                    {
                        // 轮转失败继续追加。
                    }
                }

                File.AppendAllText(
                    _path,
                    $"[{DateTimeOffset.Now:O}] [{level}] {message}{Environment.NewLine}",
                    System.Text.Encoding.UTF8);
            }
            catch
            {
                // 日志失败绝不影响 Watchdog 正常工作。
            }
        }
    }
}
