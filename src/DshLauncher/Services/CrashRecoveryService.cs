using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// 崩溃恢复：区分"用户/Launcher 主动停止"与"崩溃"，按每实例策略给出重启/冷却计划，
/// 并把崩溃现场（退出码、运行时长、dsh 输出尾部、启动证据、资源采样）落盘到
/// <c>&lt;DSH_HOME&gt;\.dsh-launcher\crash-records.jsonl</c>（最近 10 条）与
/// <c>crash-state.json</c>（重启计数/冷却状态，跨 Launcher 重启保留）。
/// 全部落盘尽力而为，任何 IO 异常都不影响运行。
/// </summary>
public sealed class CrashRecoveryService
{
    public const int MaxRecords = 10;

    /// <summary>主动停止后多久内的进程退出仍视为"有意停止"。</summary>
    public static TimeSpan IntentionalStopWindow { get; } = TimeSpan.FromMinutes(5);

    private const string RecordsFileName = "crash-records.jsonl";
    private const string StateFileName = "crash-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions StateOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private sealed class InstanceState
    {
        public bool Loaded;
        public int Attempts;
        public DateTimeOffset? StartedAt;
        public bool Cooldown;
        public DateTimeOffset? CooldownAt;
        public int? LastExitCode;
        public DateTimeOffset? LastCrashAt;
        public string? LastAction;
    }

    private sealed record PersistedState(
        int Attempts,
        DateTimeOffset? StartedAt,
        bool Cooldown,
        DateTimeOffset? CooldownAt,
        int? LastExitCode,
        DateTimeOffset? LastCrashAt,
        string? LastAction);

    private readonly object _gate = new();
    private readonly Func<string, string?>? _dshHomeResolver;
    private readonly Dictionary<string, InstanceState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CrashRecord>> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _intentionalStops = new(StringComparer.Ordinal);

    public CrashRecoveryService(Func<string, string?>? dshHomeResolver = null)
    {
        _dshHomeResolver = dshHomeResolver;
    }

    /// <summary>用户/Launcher 主动停止前登记（空闲自动停止、安全模式切换、更新重启、关闭 Launcher 等）。</summary>
    public void NoteIntentionalStop(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            _intentionalStops[instanceId] = DateTimeOffset.Now;
        }
    }

    /// <summary>消费"主动停止"标记：窗口期内返回 true（表示这次退出不是崩溃）。</summary>
    public bool ConsumeIntentionalStop(string? instanceId, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        var moment = now ?? DateTimeOffset.Now;
        lock (_gate)
        {
            if (!_intentionalStops.TryGetValue(instanceId, out var at))
            {
                return false;
            }

            _intentionalStops.Remove(instanceId);
            return moment - at <= IntentionalStopWindow;
        }
    }

    /// <summary>启动成功：清零重启计数、解除冷却、记录启动时刻。</summary>
    public void NoteStarted(string? instanceId, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        var moment = at ?? DateTimeOffset.Now;
        lock (_gate)
        {
            var state = EnsureLoadedLocked(instanceId);
            state.Attempts = 0;
            state.Cooldown = false;
            state.CooldownAt = null;
            state.StartedAt = moment;
            SaveStateLocked(instanceId, state);
        }
    }

    /// <summary>记录一次崩溃并给出处置计划。</summary>
    public CrashRecoveryPlan NoteCrash(
        string instanceId,
        CrashRecoveryPolicy policy,
        int limit,
        CrashRecord context,
        DateTimeOffset? at = null)
    {
        var moment = at ?? DateTimeOffset.Now;
        CrashRecoveryPlan plan;
        lock (_gate)
        {
            var state = EnsureLoadedLocked(instanceId);
            var uptime = state.StartedAt is { } started && moment > started
                ? moment - started
                : TimeSpan.Zero;

            // 稳定运行超过窗口再崩溃：视为新一轮，计数清零。
            if (state.StartedAt is { } stable && moment - stable >= CrashRecoveryPlanner.StableResetWindow)
            {
                state.Attempts = 0;
            }

            plan = CrashRecoveryPlanner.Plan(policy, state.Attempts, limit);
            state.LastExitCode = context.ExitCode;
            state.LastCrashAt = moment;
            state.LastAction = plan.Decision switch
            {
                CrashRecoveryDecision.Restart => "自动重启",
                CrashRecoveryDecision.CoolDown => "冷却关闭",
                _ => "仅通知"
            };
            state.StartedAt = null;

            if (plan.Decision == CrashRecoveryDecision.Restart)
            {
                state.Attempts = plan.Attempt;
                state.Cooldown = false;
                state.CooldownAt = null;
            }
            else if (plan.Decision == CrashRecoveryDecision.CoolDown)
            {
                state.Cooldown = true;
                state.CooldownAt = moment;
            }

            var record = context with
            {
                At = moment,
                Uptime = uptime,
                Action = state.LastAction!,
                Summary = plan.Summary
            };
            AppendRecordLocked(instanceId, record);
            SaveStateLocked(instanceId, state);
        }

        return plan;
    }

    public CrashRecoveryStatus GetStatus(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return CrashRecoveryStatus.Normal;
        }

        lock (_gate)
        {
            var state = EnsureLoadedLocked(instanceId);
            return new CrashRecoveryStatus(
                state.Cooldown,
                state.Attempts,
                state.LastCrashAt,
                state.LastExitCode,
                state.LastAction);
        }
    }

    public bool IsCoolingDown(string? instanceId) => GetStatus(instanceId).Cooldown;

    /// <summary>解除冷却（用户点"清冷却并重启"，或手动启动成功）。</summary>
    public void ClearCooldown(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            var state = EnsureLoadedLocked(instanceId);
            state.Cooldown = false;
            state.CooldownAt = null;
            state.Attempts = 0;
            SaveStateLocked(instanceId, state);
        }
    }

    /// <summary>崩溃记录（最新在前）。</summary>
    public IReadOnlyList<CrashRecord> GetRecords(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return Array.Empty<CrashRecord>();
        }

        lock (_gate)
        {
            EnsureLoadedLocked(instanceId);
            return _records.TryGetValue(instanceId, out var list)
                ? list.AsEnumerable().Reverse().ToArray()
                : Array.Empty<CrashRecord>();
        }
    }

    /// <summary>实例被删除时清掉内存状态（磁盘随目录一起删）。</summary>
    public void Forget(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            _states.Remove(instanceId);
            _records.Remove(instanceId);
            _intentionalStops.Remove(instanceId);
        }
    }

    private string? ResolveDirectory(string instanceId)
    {
        var home = _dshHomeResolver?.Invoke(instanceId);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".dsh-launcher");
    }

    private InstanceState EnsureLoadedLocked(string instanceId)
    {
        if (_states.TryGetValue(instanceId, out var existing) && existing.Loaded)
        {
            return existing;
        }

        var state = new InstanceState { Loaded = true };
        var directory = ResolveDirectory(instanceId);
        if (directory is not null)
        {
            try
            {
                var statePath = Path.Combine(directory, StateFileName);
                if (File.Exists(statePath))
                {
                    var persisted = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath, Encoding.UTF8), JsonOptions);
                    if (persisted is not null)
                    {
                        state.Attempts = Math.Max(0, persisted.Attempts);
                        state.StartedAt = persisted.StartedAt;
                        state.Cooldown = persisted.Cooldown;
                        state.CooldownAt = persisted.CooldownAt;
                        state.LastExitCode = persisted.LastExitCode;
                        state.LastCrashAt = persisted.LastCrashAt;
                        state.LastAction = persisted.LastAction;
                    }
                }

                var recordsPath = Path.Combine(directory, RecordsFileName);
                if (File.Exists(recordsPath))
                {
                    var list = new List<CrashRecord>();
                    foreach (var line in File.ReadLines(recordsPath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        try
                        {
                            if (JsonSerializer.Deserialize<CrashRecord>(line, JsonOptions) is { } record)
                            {
                                list.Add(record);
                            }
                        }
                        catch (JsonException)
                        {
                            // 损坏行跳过。
                        }
                    }

                    if (list.Count > MaxRecords)
                    {
                        list.RemoveRange(0, list.Count - MaxRecords);
                    }

                    if (list.Count > 0)
                    {
                        _records[instanceId] = list;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // 读取失败按空状态处理。
            }
        }

        _states[instanceId] = state;
        return state;
    }

    private void AppendRecordLocked(string instanceId, CrashRecord record)
    {
        if (!_records.TryGetValue(instanceId, out var list))
        {
            list = new List<CrashRecord>();
            _records[instanceId] = list;
        }

        list.Add(record);
        if (list.Count > MaxRecords)
        {
            list.RemoveRange(0, list.Count - MaxRecords);
        }

        var directory = ResolveDirectory(instanceId);
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, RecordsFileName);
            var builder = new StringBuilder();
            foreach (var item in list)
            {
                builder.AppendLine(JsonSerializer.Serialize(item, JsonOptions));
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, builder.ToString(), Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 现场记录写不进去只影响回看。
        }
    }

    private void SaveStateLocked(string instanceId, InstanceState state)
    {
        var directory = ResolveDirectory(instanceId);
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var persisted = new PersistedState(
                state.Attempts,
                state.StartedAt,
                state.Cooldown,
                state.CooldownAt,
                state.LastExitCode,
                state.LastCrashAt,
                state.LastAction);
            var path = Path.Combine(directory, StateFileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(persisted, StateOptions), Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 状态写不进去只影响跨重启记忆。
        }
    }
}
