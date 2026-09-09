namespace DshLauncher.Services;

/// <summary>
/// 每实例的启动证据记录（进程内、不落盘）：成功/失败判定与四层证据都留痕，
/// 供「实例设置 → 运行状况」的"启动证据"区块展示。
/// </summary>
public sealed class StartupEvidenceStore
{
    public const int DefaultCapacity = 60;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, List<StartupEvidence>> _entries = new(StringComparer.Ordinal);

    public StartupEvidenceStore(int capacity = DefaultCapacity)
    {
        _capacity = Math.Clamp(capacity, 3, 500);
    }

    public void Record(string? instanceId, StartupEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(instanceId, out var list))
            {
                list = new List<StartupEvidence>();
                _entries[instanceId] = list;
            }

            list.Add(evidence);
            if (list.Count > _capacity)
            {
                list.RemoveRange(0, list.Count - _capacity);
            }
        }
    }

    public void Record(string? instanceId, IEnumerable<StartupEvidence> evidence)
    {
        foreach (var item in evidence)
        {
            Record(instanceId, item);
        }
    }

    /// <summary>最近的证据（按时间倒序，最多 capacity 条）。</summary>
    public IReadOnlyList<StartupEvidence> Snapshot(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return Array.Empty<StartupEvidence>();
        }

        lock (_gate)
        {
            return _entries.TryGetValue(instanceId, out var list)
                ? list.AsEnumerable().Reverse().ToArray()
                : Array.Empty<StartupEvidence>();
        }
    }

    public void Clear(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            _entries.Remove(instanceId);
        }
    }
}
