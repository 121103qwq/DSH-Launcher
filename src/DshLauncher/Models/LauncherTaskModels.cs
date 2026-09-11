using System.Text.Json.Serialization;

namespace DshLauncher.Models;

/// <summary>长任务的类别（任务中心里分组/标色用）。</summary>
public enum LauncherTaskKind
{
    /// <summary>运行环境：Node 便携版下载、DSh 安装与修复。</summary>
    RuntimePrepare,

    /// <summary>插件 / 技能 的安装、更新、卸载。</summary>
    Plugin,

    /// <summary>实例导入（目录扫描、DSH_HOME 导入）。</summary>
    InstanceImport,

    /// <summary>更换实例运行版本（含目标版本下载）。</summary>
    VersionSwitch,

    /// <summary>其它长任务。</summary>
    Other
}

/// <summary>任务状态。运行中的任务只在内存里，结束后进历史。</summary>
public enum LauncherTaskState
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// 任务台账里的一条。只有原始字段落盘；下面的显示文案都标了
/// <see cref="JsonIgnoreAttribute"/>，改文案不会污染已有历史文件。
/// </summary>
public sealed record LauncherTaskItem(
    Guid Id,
    LauncherTaskKind Kind,
    string Title,
    string? InstanceName,
    string Detail,
    LauncherTaskState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Result)
{
    [JsonIgnore]
    public bool IsRunning => State == LauncherTaskState.Running;

    [JsonIgnore]
    public string KindText => Kind switch
    {
        LauncherTaskKind.RuntimePrepare => "运行环境",
        LauncherTaskKind.Plugin => "插件 / 技能",
        LauncherTaskKind.InstanceImport => "实例导入",
        LauncherTaskKind.VersionSwitch => "更换运行版本",
        _ => "其它"
    };

    [JsonIgnore]
    public string StateText => State switch
    {
        LauncherTaskState.Running => "进行中",
        LauncherTaskState.Succeeded => "已完成",
        LauncherTaskState.Failed => "失败",
        _ => "已取消"
    };

    [JsonIgnore]
    public string TargetText => string.IsNullOrWhiteSpace(InstanceName) ? "全局" : InstanceName;

    [JsonIgnore]
    public string StartedText => StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");

    [JsonIgnore]
    public string DurationText
    {
        get
        {
            if (FinishedAt is not { } finished)
            {
                return "—";
            }

            var seconds = Math.Max(0, (finished - StartedAt).TotalSeconds);
            return seconds < 60 ? $"{seconds:F0} 秒" : $"{seconds / 60:F1} 分钟";
        }
    }

    /// <summary>最近一条进度或最终结果（列表里显示的第二行）。</summary>
    [JsonIgnore]
    public string SummaryText => string.IsNullOrWhiteSpace(Result) ? Detail : Result;
}
