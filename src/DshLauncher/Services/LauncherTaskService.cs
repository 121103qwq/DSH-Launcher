using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Launcher 长任务的共享台账。与 WPF 解耦：worker 线程可以直接上报，视图自行把
/// <see cref="Changed"/> marshal 回自己的 Dispatcher（与上游 LauncherTaskService 同样的划分）。
/// <para>
/// 运行中的任务只在内存里；结束后写入 Launcher 数据根的 <c>launcher-tasks.json</c>，
/// 只保留最近 <see cref="MaximumRetainedTasks"/> 条。历史里的详情一律截断，避免长输出把
/// 台账撑大或把凭据带进文件；读取失败/损坏一律当作"没有历史"，绝不因为台账影响任何长任务。
/// </para>
/// </summary>
public sealed class LauncherTaskService
{
    public const int MaximumRetainedTasks = 50;

    /// <summary>单条详情的上限：长输出只留开头，够定位是哪一步即可。</summary>
    private const int MaximumDetailLength = 200;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly LauncherPaths _paths;
    private readonly object _gate = new();
    private readonly List<LauncherTaskItem> _history = new();
    private readonly Dictionary<Guid, LauncherTaskHandle> _running = new();

    public LauncherTaskService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
        lock (_gate)
        {
            _history.AddRange(ReadHistoryUnlocked());
        }
    }

    /// <summary>任务开始 / 上报进度 / 结束以及历史变化时触发（可能在任意线程）。</summary>
    public event EventHandler? Changed;

    public string HistoryPath => Path.Combine(_paths.RootDirectory, "launcher-tasks.json");

    /// <summary>正在运行的任务数（顶栏角标用它）。</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>
    /// 开始一个任务。返回值负责上报进度与收尾；直接 <c>Dispose</c> 而未收尾时，
    /// 按"已取消"（令牌被取消）或"失败（操作未完成）"落账，不会留下永远"进行中"的行。
    /// </summary>
    public LauncherTaskHandle Begin(
        LauncherTaskKind kind,
        string title,
        string? instanceName = null,
        string? detail = null)
    {
        var handle = new LauncherTaskHandle(this, kind, title, instanceName, detail);
        lock (_gate)
        {
            _running[handle.Id] = handle;
        }

        RaiseChanged();
        return handle;
    }

    /// <summary>运行中（按开始时间倒序）在前、历史（按结束时间倒序）在后的快照。</summary>
    public IReadOnlyList<LauncherTaskItem> Snapshot()
    {
        lock (_gate)
        {
            return _running.Values
                .Select(handle => handle.Item)
                .OrderByDescending(item => item.StartedAt)
                .Concat(_history.OrderByDescending(item => item.FinishedAt ?? item.StartedAt))
                .ToArray();
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            WriteHistoryUnlocked();
        }

        RaiseChanged();
    }

    /// <summary>
    /// 按任务 id 请求取消（任务中心里的“取消”按钮用它）。只发取消信号，
    /// 真正收尾仍由长任务自己的流程决定；任务不在运行中时返回 false。
    /// </summary>
    public bool TryCancel(Guid id)
    {
        LauncherTaskHandle? handle;
        lock (_gate)
        {
            _running.TryGetValue(id, out handle);
        }

        if (handle is null || handle.IsCancellationRequested)
        {
            return false;
        }

        handle.Report("已请求取消，等待任务收尾…");
        handle.Cancel();
        return true;
    }

    internal void ReportDetail(Guid id, string detail)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var handle))
            {
                return;
            }

            handle.ApplyDetail(Truncate(detail));
        }

        RaiseChanged();
    }

    internal void Finish(Guid id, LauncherTaskState state, string? result)
    {
        lock (_gate)
        {
            if (!_running.Remove(id, out var handle))
            {
                return;
            }

            var finished = handle.Item with
            {
                State = state,
                FinishedAt = DateTimeOffset.UtcNow,
                Result = Truncate(result),
                Detail = handle.Item.Detail
            };
            _history.Add(finished);
            var trimmed = _history
                .OrderByDescending(item => item.FinishedAt ?? item.StartedAt)
                .Take(MaximumRetainedTasks)
                .ToArray();
            _history.Clear();
            _history.AddRange(trimmed);
            WriteHistoryUnlocked();
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        // 在锁外触发：视图的处理函数可能同步回读 Snapshot()，锁内触发会自锁。
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Truncate(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        var single = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= MaximumDetailLength
            ? single
            : single[..MaximumDetailLength] + "…";
    }

    private IReadOnlyList<LauncherTaskItem> ReadHistoryUnlocked()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return Array.Empty<LauncherTaskItem>();
            }

            var document = JsonSerializer.Deserialize<LauncherTaskHistoryDocument>(
                File.ReadAllText(HistoryPath, Encoding.UTF8),
                ReadOptions);
            if (document is null || document.Version != LauncherTaskHistoryDocument.CurrentVersion)
            {
                return Array.Empty<LauncherTaskItem>();
            }

            return (document.Tasks ?? new List<LauncherTaskItem>())
                .Select(NormalizeInterrupted)
                .OrderByDescending(item => item.FinishedAt ?? item.StartedAt)
                .Take(MaximumRetainedTasks)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<LauncherTaskItem>();
        }
    }

    /// <summary>
    /// 历史里不该再出现"进行中"：进程被杀时最后一条会停在 Running，重新加载时
    /// 归成"中断"（失败 + 说明），否则任务页会永远显示一行转不完的任务。
    /// </summary>
    private static LauncherTaskItem NormalizeInterrupted(LauncherTaskItem item) =>
        item.State == LauncherTaskState.Running
            ? item with
            {
                State = LauncherTaskState.Failed,
                FinishedAt = item.FinishedAt ?? item.StartedAt,
                Result = "启动器已重启，任务中断"
            }
            : item;

    private void WriteHistoryUnlocked()
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            var document = new LauncherTaskHistoryDocument
            {
                Version = LauncherTaskHistoryDocument.CurrentVersion,
                Tasks = _history.ToList()
            };
            File.WriteAllText(
                HistoryPath,
                JsonSerializer.Serialize(document, WriteOptions),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 台账写不进去不影响任务本身。
        }
    }

    private sealed class LauncherTaskHistoryDocument
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public List<LauncherTaskItem>? Tasks { get; set; } = new();
    }
}

/// <summary>
/// 一次长任务的句柄：上报进度、请求取消、收尾。
/// 取消令牌与长任务自己的 <c>CancellationTokenSource</c> 用
/// <c>CreateLinkedTokenSource</c> 串起来，所以任务中心里的"取消"和原有进度窗的取消按钮等效。
/// </summary>
public sealed class LauncherTaskHandle : IDisposable
{
    private readonly LauncherTaskService _service;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    internal LauncherTaskHandle(
        LauncherTaskService service,
        LauncherTaskKind kind,
        string title,
        string? instanceName,
        string? detail)
    {
        _service = service;
        Id = Guid.NewGuid();
        Item = new LauncherTaskItem(
            Id,
            kind,
            title,
            instanceName,
            detail ?? string.Empty,
            LauncherTaskState.Running,
            DateTimeOffset.UtcNow,
            null,
            null);
    }

    public Guid Id { get; }

    public LauncherTaskItem Item { get; private set; }

    public CancellationToken Token => _cancellation.Token;

    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    /// <summary>上报一行进度（覆盖上一行，历史里只留最后一条）。</summary>
    public void Report(string detail)
    {
        if (_finished)
        {
            return;
        }

        Item = Item with { Detail = detail };
        _service.ReportDetail(Id, detail);
    }

    /// <summary>请求取消：让长任务自己的取消令牌触发。</summary>
    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经收尾并释放，忽略。
        }
    }

    public void Complete(string? result = null) => Finish(LauncherTaskState.Succeeded, result);

    public void Fail(string? message) => Finish(LauncherTaskState.Failed, message);

    public void MarkCancelled(string? reason = "已取消") => Finish(LauncherTaskState.Cancelled, reason);

    /// <summary>
    /// 把这个任务与长任务自己的取消源双向串联：任务中心点“取消”与原有进度窗点“取消”完全等效，
    /// 并且无论从哪边取消，台账状态都会如实落成“已取消”。返回值随任务一起释放（退订）。
    /// </summary>
    public IDisposable LinkTo(CancellationTokenSource cancellation)
    {
        var forward = Token.Register(() =>
        {
            // IsCancellationRequested 在回调执行前就已置位，因此不会递归取消。
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 长任务已经自己收尾并释放了取消源。
            }
        });
        var backward = cancellation.Token.Register(Cancel);
        return new CancellationLink(forward, backward);
    }

    /// <summary>把两个取消登记当成一个 <see cref="IDisposable"/> 一起释放。</summary>
    private sealed class CancellationLink(
        CancellationTokenRegistration first,
        CancellationTokenRegistration second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    internal void ApplyDetail(string detail) => Item = Item with { Detail = detail };

    private void Finish(LauncherTaskState state, string? result)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _service.Finish(Id, state, result);
    }

    public void Dispose()
    {
        if (!_finished)
        {
            Finish(
                _cancellation.IsCancellationRequested ? LauncherTaskState.Cancelled : LauncherTaskState.Failed,
                _cancellation.IsCancellationRequested ? "已取消" : "操作未完成");
        }

        _cancellation.Dispose();
    }
}
