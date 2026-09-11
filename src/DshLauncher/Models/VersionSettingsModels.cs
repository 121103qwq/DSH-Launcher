using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public enum ConversationSyncMode
{
    Independent,
    Workspace,
    All
}

public enum PluginInstallMode
{
    Fast,
    Compatibility
}

[JsonConverter(typeof(VersionOpenModeConverter))]
public enum VersionOpenMode
{
    /// <summary>Web 启动：dsh 原生运行方式（服务由 Launcher 管理，打开方式交给 dsh 默认浏览器）。</summary>
    Web,
    /// <summary>Desktop 启动：启动器所带的方式（启动服务 + 自动打开内部 Chat 窗口，抑制 dsh 浏览器双开）。</summary>
    Desktop,
    Custom
}

/// <summary>
/// 兼容旧设置文件：v1.0.7 的枚举值是 Launcher/Desktop/Custom，其中
/// "Launcher"（启动器方式）重命名为 Desktop；旧 "Desktop"（DSH Desktop 封装窗口）
/// 不再是打开方式选项（该入口保留为启动页的独立按钮），保守映射到 Desktop。
/// </summary>
public sealed class VersionOpenModeConverter : JsonConverter<VersionOpenMode>
{
    public override VersionOpenMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "Web" => VersionOpenMode.Web,
            "Desktop" or "Launcher" => VersionOpenMode.Desktop,
            "Custom" => VersionOpenMode.Custom,
            _ => VersionOpenMode.Desktop
        };
    }

    public override void Write(Utf8JsonWriter writer, VersionOpenMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class VersionSettingsData
{
    public bool SyncAllConfiguration { get; set; }

    /// <summary>
    /// 该实例当前使用的 dsh profile（<c>$DSH_HOME/profiles/&lt;name&gt;</c>）。
    /// 空值＝沿用 dsh 的 <c>web</c> 别名（历史行为）。见 work-log/60。
    /// </summary>
    public string? ActiveProfile { get; set; }

    public ConversationSyncMode ConversationSyncMode { get; set; } = ConversationSyncMode.Independent;

    public string? ConversationWorkspace { get; set; }

    public bool SyncModelProviders { get; set; } = true;

    public bool UseDshMarketHotReload { get; set; } = true;

    public string? WindowTitle { get; set; }

    public string? NodeExecutablePath { get; set; }

    /// <summary>
    /// Null keeps the legacy behavior: a detected DSH Desktop runtime opens as
    /// a desktop window, while normal DSh runtimes use Launcher web startup.
    /// </summary>
    public VersionOpenMode? OpenMode { get; set; }

    /// <summary>
    /// Local executable, script or Windows shortcut used when OpenMode is Custom.
    /// This machine-specific path is not included in shareable version packages.
    /// </summary>
    public string? CustomOpenTargetPath { get; set; }

    /// <summary>
    /// 实例级环境变量：启动 dsh 时注入进程环境。DSH_HOME / DSH_AGENTS_HOME / PATH
    /// 为保留项；敏感值（KEY/TOKEN/SECRET/PASSWORD…）落盘时用 DPAPI 加密。
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>空闲时自动停止该实例（仅对 Launcher 托管的运行中实例生效）。</summary>
    public bool AutoStopWhenIdle { get; set; }

    /// <summary>是否在该实例设置页显示 DSh 版本更新提示（联网查询官方版本，默认关）。</summary>
    public bool CheckDshUpdates { get; set; }

    /// <summary>空闲阈值（分钟，5–240，默认 30）。</summary>
    public int? AutoStopIdleMinutes { get; set; }

    /// <summary>崩溃后行为（默认仅通知）。</summary>
    public CrashRecoveryPolicy CrashPolicy { get; set; } = CrashRecoveryPolicy.NotifyOnly;

    /// <summary>自动重启上限（1–10，默认 5）。</summary>
    public int? CrashRestartLimit { get; set; }
}

/// <summary>
/// Launcher 级设置：作用域是全部版本，而不是某一个版本的 DSH_HOME。
/// </summary>
public enum CloseBehavior
{
    /// <summary>最小化到托盘：关闭主窗口仅隐藏，实例继续运行，托盘可恢复。</summary>
    MinimizeToTray,
    /// <summary>关闭启动器与实例：退出应用并停止 Launcher 管理的实例。</summary>
    ExitAndStopInstances
}

/// <summary>
/// 兼容旧设置文件：早期版本存在 CloseBehavior.Exit（“仅退出不停实例”）语义，
/// 与“web/desktop 窗口不具备关停实例能力”的设计冲突，已移除；旧值一律映射到
/// ExitAndStopInstances（退出并停实例）。
/// </summary>
public sealed class CloseBehaviorConverter : JsonConverter<CloseBehavior>
{
    public override CloseBehavior Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "MinimizeToTray" => CloseBehavior.MinimizeToTray,
            _ => CloseBehavior.ExitAndStopInstances
        };
    }

    public override void Write(Utf8JsonWriter writer, CloseBehavior value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class LauncherSettingsData
{
    public bool SyncAllConfiguration { get; set; }

    public List<string> Workspaces { get; set; } = new();

    public PluginInstallMode PluginInstallMode { get; set; } = PluginInstallMode.Fast;



    /// <summary>点击主窗口 × 时的行为。</summary>
    [JsonConverter(typeof(CloseBehaviorConverter))]
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.MinimizeToTray;

    /// <summary>
    /// Optional npm global prefix used only for the Launcher-managed DSh runtime.
    /// Instance data remains isolated under each instance's DSH_HOME.
    /// </summary>
    public string? DshInstallDirectory { get; set; }

    /// <summary>实例守护监控轮询间隔（秒，2–120；默认 5）。</summary>
    public int WatchdogProbeSeconds { get; set; } = 5;

    /// <summary>Launcher 级代理开关（同时作用于 Launcher HTTP 与 dsh 实例环境变量）。</summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>代理地址，形如 http://127.0.0.1:7890（缺 scheme 时按 http 处理）。</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>NO_PROXY 列表（逗号/分号/空格分隔）。</summary>
    public string? NoProxy { get; set; }

    /// <summary>是否把代理注入启动的 dsh 实例（默认开）。</summary>
    public bool ProxyApplyDsh { get; set; } = true;

    /// <summary>是否在主界面显示 DeepSeek 余额（默认关；开启后仅在本机内存读取凭据）。</summary>
    public bool BalanceEnabled { get; set; }
}

public sealed record VersionExportOptions(
    bool IncludeProviderConfiguration,
    bool IncludePluginConfiguration);
