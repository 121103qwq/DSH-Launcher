using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>一次"更换运行版本"的留痕（用于「版本与快照」页的切换历史与一键回退）。</summary>
public sealed record VersionSwitchRecord(
    string InstanceId,
    string InstanceName,
    string FromVersion,
    string ToVersion,
    string Direction,
    string? PackageRoot,
    bool SnapshotCreated,
    string? SessionBackupDirectory,
    DateTimeOffset SwitchedAt)
{
    /// <summary>列表里显示的一行文案（只用于 UI，不落盘）。</summary>
    [JsonIgnore]
    public string DisplayText =>
        $"{SwitchedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {FromVersion} → {ToVersion}（{Direction}）"
        + (SnapshotCreated ? " · 已建快照" : string.Empty)
        + (string.IsNullOrWhiteSpace(SessionBackupDirectory) ? string.Empty : " · 已导出会话");
}

/// <summary>
/// 切换历史：写在 Launcher 数据根 <c>version-switch-history.json</c>（含实例 id，实例移动目录后仍可追溯）。
/// 只保留最近 <see cref="MaxRecords"/> 条；读取失败/损坏一律当作"没有历史"，绝不因历史文件影响换版本。
/// </summary>
public sealed class VersionSwitchHistoryService
{
    public const int MaxRecords = 50;

    private readonly LauncherPaths _paths;
    private readonly object _gate = new();

    public VersionSwitchHistoryService(LauncherPaths? paths = null) => _paths = paths ?? new LauncherPaths();

    public string HistoryPath => Path.Combine(_paths.RootDirectory, "version-switch-history.json");

    public IReadOnlyList<VersionSwitchRecord> Load()
    {
        lock (_gate)
        {
            return ReadUnlocked();
        }
    }

    public IReadOnlyList<VersionSwitchRecord> LoadForInstance(string instanceId) =>
        Load()
            .Where(record => string.Equals(record.InstanceId, instanceId, StringComparison.Ordinal))
            .OrderByDescending(record => record.SwitchedAt)
            .ToArray();

    /// <summary>最近一次切换（"一键回退"的起点）；没有历史时返回 null。</summary>
    public VersionSwitchRecord? LatestForInstance(string instanceId) =>
        LoadForInstance(instanceId).FirstOrDefault();

    public void Append(VersionSwitchRecord record)
    {
        lock (_gate)
        {
            var all = ReadUnlocked().ToList();
            all.Add(record);
            var trimmed = all
                .OrderByDescending(item => item.SwitchedAt)
                .Take(MaxRecords)
                .ToArray();
            WriteUnlocked(trimmed);
        }
    }

    private IReadOnlyList<VersionSwitchRecord> ReadUnlocked()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return Array.Empty<VersionSwitchRecord>();
            }

            var records = JsonSerializer.Deserialize<List<VersionSwitchRecord>>(
                File.ReadAllText(HistoryPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return records ?? (IReadOnlyList<VersionSwitchRecord>)Array.Empty<VersionSwitchRecord>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<VersionSwitchRecord>();
        }
    }

    private void WriteUnlocked(IReadOnlyList<VersionSwitchRecord> records)
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            File.WriteAllText(
                HistoryPath,
                JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 历史写入失败不影响切换本身。
        }
    }
}
