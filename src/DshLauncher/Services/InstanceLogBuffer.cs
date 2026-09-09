namespace DshLauncher.Services;

/// <summary>实例运行日志的一行（来源：dsh 标准输出/错误、Launcher 生命周期事件）。</summary>
public sealed record InstanceLogLine(DateTimeOffset At, string Source, string Text);

/// <summary>
/// 每实例的日志环形缓冲（进程内、不落盘）：dsh stdout/stderr + Launcher 生命周期事件。
/// 供"运行状况"页实时查看；容量固定，超出后丢最旧的。
/// </summary>
public sealed class InstanceLogBuffer
{
    public const int DefaultCapacity = 2000;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, Queue<InstanceLogLine>> _buffers = new(StringComparer.Ordinal);

    public InstanceLogBuffer(int capacity = DefaultCapacity)
    {
        _capacity = Math.Clamp(capacity, 50, 20000);
    }

    /// <summary>追加一段文本（内部按行拆分；空行忽略；自动限长）。</summary>
    public void Append(string? instanceId, string source, string? text)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        lock (_gate)
        {
            if (!_buffers.TryGetValue(instanceId, out var queue))
            {
                queue = new Queue<InstanceLogLine>();
                _buffers[instanceId] = queue;
            }

            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                queue.Enqueue(new InstanceLogLine(now, source, trimmed));
                while (queue.Count > _capacity)
                {
                    queue.Dequeue();
                }
            }
        }
    }

    public IReadOnlyList<InstanceLogLine> Snapshot(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return Array.Empty<InstanceLogLine>();
        }

        lock (_gate)
        {
            return _buffers.TryGetValue(instanceId, out var queue)
                ? queue.ToArray()
                : Array.Empty<InstanceLogLine>();
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
            _buffers.Remove(instanceId);
        }
    }
}
