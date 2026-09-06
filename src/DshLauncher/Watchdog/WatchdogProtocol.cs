using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher.Watchdog;

/// <summary>
/// 进程内监控（WatchdogRuntime）的数据模型。原独立 watchdog 进程方案的
/// 管道协议（请求/应答）已随进程内化删除——监控与 Launcher 同进程，
/// 事件通过 C# event 直接回调。
/// </summary>
public sealed class WatchdogInstanceDto
{
    /// <summary>实例 ID（instances.json 的 Id）。</summary>
    public required string InstanceId { get; set; }

    public string? Name { get; set; }

    /// <summary>实例 DSH_HOME（隔离数据目录），清理/识别都按它匹配，绝不按 ID 猜路径。</summary>
    public required string DshHome { get; set; }

    /// <summary>实例根目录（工作目录），识别市场重启进程的辅助信号。</summary>
    public string? RootPath { get; set; }

    public int ProcessId { get; set; }

    public int Port { get; set; }

    public string? WebUrl { get; set; }

    /// <summary>0.1.2-rc.1 起带 launch token 的地址；市场重启后从
    /// %TEMP%/dsh-market-restart-*.out.log 兜底提取。</summary>
    public string? AuthenticatedWebUrl { get; set; }

    /// <summary>是否受 Launcher 管理（未管理孤儿不纳入收尾）。</summary>
    public bool Managed { get; set; } = true;

    /// <summary>最近一次状态变化（识别/转正/停止），仅供日志。不参与传输。</summary>
    [JsonIgnore]
    public string? LastTransition { get; set; }
}

/// <summary>单实例台账文件内容（崩溃后恢复用：下次启动据此提示残留实例）。</summary>
public sealed class WatchdogState
{
    public List<WatchdogInstanceDto> Instances { get; set; } = new();

    public DateTimeOffset? LauncherStartedAt { get; set; }
}

public static class WatchdogProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
