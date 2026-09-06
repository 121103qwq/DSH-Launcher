using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher.Watchdog;

/// <summary>
/// Launcher 与 Watchdog 之间的共享协议（命名管道 JSON 行）。
/// 主项目（DshLauncher）通过 <c>&lt;Compile Include&gt;</c> 链接本文件保持单一契约。
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

    /// <summary>0.1.2-rc.1 起带 launch token 的地址；市场重启后由 Watchdog 从
    /// %TEMP%/dsh-market-restart-*.out.log 兜底提取。</summary>
    public string? AuthenticatedWebUrl { get; set; }

    /// <summary>Launcher 退出时是否应停止该实例（Managed=true 才执行收尾停止）。</summary>
    public bool Managed { get; set; } = true;

    /// <summary>最近一次状态变化（识别/转正/停止），仅供日志。不参与传输。</summary>
    [JsonIgnore]
    public string? LastTransition { get; set; }
}

/// <summary>单实例台账文件内容。</summary>
public sealed class WatchdogState
{
    public List<WatchdogInstanceDto> Instances { get; set; } = new();

    public DateTimeOffset? LauncherStartedAt { get; set; }

    public int? LauncherPid { get; set; }
}

public static class WatchdogProtocol
{
    /// <summary>客户端 → 服务端消息。</summary>
    public sealed class Request
    {
        /// <summary>register / unregister / snapshot / cleanup / shutdown / ping</summary>
        public required string T { get; set; }

        public WatchdogInstanceDto? Instance { get; set; }

        public string? InstanceId { get; set; }
    }

    /// <summary>服务端 → 客户端应答（含事件推送）。</summary>
    public sealed class Response
    {
        public required string T { get; set; }

        public string? Event { get; set; }

        public List<WatchdogInstanceDto>? Instances { get; set; }

        public WatchdogInstanceDto? Instance { get; set; }

        public string? InstanceId { get; set; }

        public string? Message { get; set; }

        public int Cleaned { get; set; }
    }

    public const string TaskRegister = "register";
    public const string TaskUnregister = "unregister";
    public const string TaskSnapshot = "snapshot";
    public const string TaskCleanup = "cleanup";
    public const string TaskShutdown = "shutdown";
    public const string TaskPing = "ping";

    public const string EventGhostAdopted = "ghost-adopted";
    public const string EventGhostOrphan = "ghost-orphan";
    public const string EventInstanceStopped = "instance-stopped";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetLauncherPipeName(int sessionId) =>
        $"DSH-Launcher-Watchdog-{sessionId}";
}
