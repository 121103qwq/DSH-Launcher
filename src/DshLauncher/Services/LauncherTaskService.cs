using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Shared task state for Launcher operations. This service intentionally has
/// no WPF dependency: callers may report from worker threads safely, while a
/// view can choose how to marshal the Changed event to its own Dispatcher.
/// </summary>
public sealed class LauncherTaskService
{
    public const int MaximumRetainedTasks = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly LauncherPaths _paths;
    private readonly Dictionary<Guid, TaskEntry> _entries = new();
    private readonly List<Guid> _order = new();

    public LauncherTaskService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
        Load();
    }

    public static LauncherTaskService Shared { get; } = new();

    // An intentionally familiar alias for callers that prefer singleton wording.
    public static LauncherTaskService Instance => Shared;

    public string HistoryPath => _paths.TaskHistoryPath;

    public event EventHandler? Changed;

    public LauncherTaskHandle Begin(
        string title,
        string category = "General",
        bool canCancel = true,
        Func<CancellationToken, Task>? retry = null)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new TaskEntry(
            Guid.NewGuid(),
            NormalizeText(title, "未命名任务", 160),
            NormalizeText(category, "General", 80),
            now,
            canCancel,
            retry);

        lock (_gate)
        {
            _entries[entry.Id] = entry;
            _order.Insert(0, entry.Id);
            TrimLocked();
            PersistLocked();
        }

        RaiseChanged();
        return new LauncherTaskHandle(this, entry.Id);
    }

    public LauncherTaskHandle Begin(
        string title,
        string category,
        Func<CancellationToken, Task> retry)
        => Begin(title, category, canCancel: true, retry: retry);

    public IReadOnlyList<LauncherTaskSnapshot> GetAll()
    {
        lock (_gate)
        {
            return _order
                .Where(_entries.ContainsKey)
                .Select(id => _entries[id].ToSnapshot())
                .ToArray();
        }
    }

    public IReadOnlyList<LauncherTaskSnapshot> Tasks => GetAll();

    public IReadOnlyList<LauncherTaskSnapshot> GetRunning()
        => GetAll().Where(static task => task.IsRunning).ToArray();

    public IReadOnlyList<LauncherTaskSnapshot> RunningTasks => GetRunning();

    public IReadOnlyList<LauncherTaskSnapshot> GetHistory()
        => GetAll().Where(static task => task.IsCompleted).ToArray();

    public IReadOnlyList<LauncherTaskSnapshot> History => GetHistory();

    public LauncherTaskSnapshot? GetSnapshot(Guid id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry)
                ? entry.ToSnapshot()
                : null;
        }
    }

    public void Report(Guid id, double? progress = null, string? statusMessage = null)
    {
        var changed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.IsRunning)
            {
                entry.Progress = NormalizeProgress(progress);
                if (statusMessage is not null)
                {
                    entry.StatusMessage = NormalizeText(statusMessage, string.Empty, 500);
                }

                entry.Status = LauncherTaskStatus.Running;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void Report(Guid id, string statusMessage)
        => Report(id, progress: null, statusMessage: statusMessage);

    public void Complete(Guid id, string? statusMessage = null)
    {
        var changed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.IsRunning)
            {
                entry.Status = LauncherTaskStatus.Succeeded;
                entry.Progress = 100;
                entry.StatusMessage = NormalizeText(statusMessage, "已完成", 500);
                entry.Error = null;
                entry.CanCancel = false;
                entry.CompletedAt = entry.UpdatedAt = DateTimeOffset.UtcNow;
                PersistLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void Fail(Guid id, Exception exception)
        => Fail(id, exception.Message);

    public void Fail(Guid id, string? error, string? statusMessage = null)
    {
        var changed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.IsRunning)
            {
                entry.Status = LauncherTaskStatus.Failed;
                entry.StatusMessage = NormalizeText(statusMessage, "执行失败", 500);
                entry.Error = NormalizeText(error, "未知错误", 2_000);
                entry.CanCancel = false;
                entry.CompletedAt = entry.UpdatedAt = DateTimeOffset.UtcNow;
                PersistLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void Cancel(Guid id, string? statusMessage = null)
    {
        CancellationTokenSource? cancellation = null;
        var changed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.IsRunning && entry.CanCancel)
            {
                entry.Status = LauncherTaskStatus.Canceled;
                entry.StatusMessage = NormalizeText(statusMessage, "已取消", 500);
                entry.Error = null;
                entry.CanCancel = false;
                entry.CompletedAt = entry.UpdatedAt = DateTimeOffset.UtcNow;
                cancellation = entry.Cancellation;
                PersistLocked();
                changed = true;
            }
        }

        // Invoke callbacks after leaving the state lock. A cancellation
        // callback is allowed to report or complete without lock inversion.
        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The task already ended and disposed its token source.
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void SetCancelable(Guid id, bool canCancel)
    {
        var changed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.IsRunning)
            {
                var next = canCancel && entry.AllowsCancel;
                if (entry.CanCancel != next)
                {
                    entry.CanCancel = next;
                    entry.UpdatedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public bool ClearCompleted()
    {
        int removed;
        lock (_gate)
        {
            var completedIds = _order
                .Where(id => _entries.TryGetValue(id, out var entry) && entry.IsCompleted)
                .ToArray();
            removed = completedIds.Length;
            if (removed == 0)
            {
                return false;
            }

            foreach (var id in completedIds)
            {
                _entries.Remove(id);
            }

            _order.RemoveAll(id => !_entries.ContainsKey(id));
            PersistLocked();
        }

        RaiseChanged();
        return removed > 0;
    }

    public Task<bool> RetryAsync(Guid id)
    {
        TaskEntry? entry;
        CancellationTokenSource? previousCancellation;
        CancellationTokenSource? retryCancellation;
        Func<CancellationToken, Task>? retry;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry)
                || !entry.IsCompleted
                || entry.Retry is null)
            {
                return Task.FromResult(false);
            }

            previousCancellation = entry.Cancellation;
            retryCancellation = new CancellationTokenSource();
            entry.Cancellation = retryCancellation;
            retry = entry.Retry;
            entry.Status = LauncherTaskStatus.Waiting;
            entry.Progress = null;
            entry.StatusMessage = "等待重试…";
            entry.Error = null;
            entry.CanCancel = entry.AllowsCancel;
            entry.CompletedAt = null;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            PersistLocked();
        }

        try
        {
            previousCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Nothing is using the previous attempt anymore.
        }

        RaiseChanged();
        return Task.Run(() => ExecuteRetryAsync(entry!, retry!, retryCancellation!));
    }

    public CancellationToken GetToken(Guid id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry)
                ? entry.Cancellation.Token
                : CancellationToken.None;
        }
    }

    internal void ReportFromHandle(Guid id, double? progress, string? statusMessage)
        => Report(id, progress, statusMessage);

    internal void CompleteFromHandle(Guid id, string? statusMessage)
        => Complete(id, statusMessage);

    internal void FailFromHandle(Guid id, Exception exception)
        => Fail(id, exception);

    internal void FailFromHandle(Guid id, string? error, string? statusMessage)
        => Fail(id, error, statusMessage);

    internal void CancelFromHandle(Guid id, string? statusMessage)
        => Cancel(id, statusMessage);

    internal void SetCancelableFromHandle(Guid id, bool canCancel)
        => SetCancelable(id, canCancel);

    private async Task<bool> ExecuteRetryAsync(
        TaskEntry entry,
        Func<CancellationToken, Task> retry,
        CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.Id, out var current)
                || !ReferenceEquals(current, entry)
                || !ReferenceEquals(current.Cancellation, cancellation)
                || current.Status != LauncherTaskStatus.Waiting)
            {
                return false;
            }

            current.Status = LauncherTaskStatus.Running;
            current.StatusMessage = "正在重试…";
            current.UpdatedAt = DateTimeOffset.UtcNow;
            PersistLocked();
        }

        RaiseChanged();
        try
        {
            await retry(cancellation.Token).ConfigureAwait(false);
            CompleteIfCurrent(entry, cancellation, "重试完成");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CancelIfCurrent(entry, cancellation, "已取消");
            return false;
        }
        catch (Exception exception)
        {
            FailIfCurrent(entry, cancellation, exception);
            return false;
        }
        // Keep the source available through the handle after completion. It
        // is replaced on the next retry and bounded by the retained task list.
    }

    private void CompleteIfCurrent(TaskEntry entry, CancellationTokenSource cancellation, string message)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.Id, out var current)
                || !ReferenceEquals(current, entry)
                || !ReferenceEquals(current.Cancellation, cancellation)
                || !current.IsRunning)
            {
                return;
            }

            current.Status = LauncherTaskStatus.Succeeded;
            current.Progress = 100;
            current.StatusMessage = message;
            current.CanCancel = false;
            current.CompletedAt = current.UpdatedAt = DateTimeOffset.UtcNow;
            PersistLocked();
        }

        RaiseChanged();
    }

    private void FailIfCurrent(TaskEntry entry, CancellationTokenSource cancellation, Exception exception)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.Id, out var current)
                || !ReferenceEquals(current, entry)
                || !ReferenceEquals(current.Cancellation, cancellation)
                || !current.IsRunning)
            {
                return;
            }

            current.Status = LauncherTaskStatus.Failed;
            current.StatusMessage = "执行失败";
            current.Error = NormalizeText(exception.Message, "未知错误", 2_000);
            current.CanCancel = false;
            current.CompletedAt = current.UpdatedAt = DateTimeOffset.UtcNow;
            PersistLocked();
        }

        RaiseChanged();
    }

    private void CancelIfCurrent(TaskEntry entry, CancellationTokenSource cancellation, string message)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(entry.Id, out var current)
                || !ReferenceEquals(current, entry)
                || !ReferenceEquals(current.Cancellation, cancellation)
                || !current.IsRunning)
            {
                return;
            }

            current.Status = LauncherTaskStatus.Canceled;
            current.StatusMessage = message;
            current.CanCancel = false;
            current.CompletedAt = current.UpdatedAt = DateTimeOffset.UtcNow;
            PersistLocked();
        }

        RaiseChanged();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_paths.TaskHistoryPath))
            {
                return;
            }

            var snapshots = JsonSerializer.Deserialize<List<LauncherTaskSnapshot>>(
                File.ReadAllText(_paths.TaskHistoryPath),
                JsonOptions);
            if (snapshots is null)
            {
                return;
            }

            var normalizedInterruptedTask = false;
            foreach (var snapshot in snapshots
                         .OrderByDescending(static item => item.UpdatedAt)
                         .Take(MaximumRetainedTasks))
            {
                var restored = snapshot;
                if (snapshot.IsRunning)
                {
                    var now = DateTimeOffset.UtcNow;
                    restored = snapshot with
                    {
                        Status = LauncherTaskStatus.Failed,
                        StatusMessage = "Launcher 上次退出前任务尚未完成",
                        UpdatedAt = now,
                        CompletedAt = now,
                        Error = "任务随上一次 Launcher 进程结束而中断。",
                        CanCancel = false,
                        CanRetry = false
                    };
                    normalizedInterruptedTask = true;
                }

                var entry = TaskEntry.FromSnapshot(restored);
                _entries[entry.Id] = entry;
                _order.Add(entry.Id);
            }

            if (normalizedInterruptedTask)
            {
                PersistLocked();
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or NotSupportedException)
        {
            // A corrupt history must not prevent Launcher startup.
        }
    }

    private void TrimLocked()
    {
        while (_order.Count > MaximumRetainedTasks)
        {
            var oldestId = _order[^1];
            _order.RemoveAt(_order.Count - 1);
            _entries.Remove(oldestId);
        }
    }

    private void PersistLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_paths.TaskHistoryPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = $"{_paths.TaskHistoryPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                    _order.Where(_entries.ContainsKey).Select(id => _entries[id].ToSnapshot()).ToArray(),
                    JsonOptions));
                File.Move(temporaryPath, _paths.TaskHistoryPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or NotSupportedException)
        {
            // Task reporting remains usable when the history location is
            // temporarily unavailable; the next mutation will try again.
        }
    }

    private void RaiseChanged()
    {
        var handlers = Changed?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.OfType<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // A UI subscriber must not break a worker-thread task update.
            }
        }
    }

    private static string NormalizeText(string? value, string fallback, int maximumLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : new string(value.Trim().Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static double? NormalizeProgress(double? progress)
    {
        if (progress is not { } value || double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        return Math.Clamp(value, 0, 100);
    }

    private sealed class TaskEntry
    {
        public TaskEntry(
            Guid id,
            string title,
            string category,
            DateTimeOffset createdAt,
            bool canCancel,
            Func<CancellationToken, Task>? retry)
        {
            Id = id;
            Title = title;
            Category = category;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
            CanCancel = canCancel;
            AllowsCancel = canCancel;
            Retry = retry;
            Cancellation = new CancellationTokenSource();
            StatusMessage = "正在执行…";
        }

        public Guid Id { get; }
        public string Title { get; }
        public string Category { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public LauncherTaskStatus Status { get; set; } = LauncherTaskStatus.Running;
        public double? Progress { get; set; }
        public string StatusMessage { get; set; }
        public string? Error { get; set; }
        public bool CanCancel { get; set; }
        public bool AllowsCancel { get; }
        public Func<CancellationToken, Task>? Retry { get; }
        public CancellationTokenSource Cancellation { get; set; }

        public bool IsRunning => Status is LauncherTaskStatus.Waiting or LauncherTaskStatus.Running;

        public bool IsCompleted => Status is LauncherTaskStatus.Succeeded
            or LauncherTaskStatus.Failed
            or LauncherTaskStatus.Canceled;

        public LauncherTaskSnapshot ToSnapshot()
            => new(
                Id,
                Title,
                Category,
                Status,
                Progress,
                StatusMessage,
                CreatedAt,
                UpdatedAt,
                CompletedAt,
                Error,
                CanCancel && IsRunning,
                Retry is not null && IsCompleted);

        public static TaskEntry FromSnapshot(LauncherTaskSnapshot snapshot)
        {
            var entry = new TaskEntry(
                snapshot.Id,
                NormalizeText(snapshot.Title, "未命名任务", 160),
                NormalizeText(snapshot.Category, "General", 80),
                snapshot.CreatedAt,
                canCancel: false,
                retry: null)
            {
                UpdatedAt = snapshot.UpdatedAt,
                CompletedAt = snapshot.CompletedAt,
                Status = snapshot.Status,
                Progress = NormalizeProgress(snapshot.Progress),
                StatusMessage = NormalizeText(snapshot.StatusMessage, string.Empty, 500),
                Error = snapshot.Error,
                CanCancel = false
            };
            return entry;
        }
    }
}

public sealed class LauncherTaskHandle
{
    private readonly LauncherTaskService _service;

    internal LauncherTaskHandle(LauncherTaskService service, Guid id)
    {
        _service = service;
        Id = id;
    }

    public Guid Id { get; }

    public CancellationToken Token => _service.GetToken(Id);

    public LauncherTaskSnapshot? Snapshot => _service.GetSnapshot(Id);

    public void Report(double? progress = null, string? statusMessage = null)
        => _service.ReportFromHandle(Id, progress, statusMessage);

    public void Report(string statusMessage)
        => _service.ReportFromHandle(Id, progress: null, statusMessage: statusMessage);

    public void Complete(string? statusMessage = null)
        => _service.CompleteFromHandle(Id, statusMessage);

    public void Fail(Exception exception)
        => _service.FailFromHandle(Id, exception);

    public void Fail(string error, string? statusMessage = null)
        => _service.FailFromHandle(Id, error, statusMessage);

    public void Cancel(string? statusMessage = null)
        => _service.CancelFromHandle(Id, statusMessage);

    public void SetCancelable(bool canCancel)
        => _service.SetCancelableFromHandle(Id, canCancel);

    public Task<bool> RetryAsync()
        => _service.RetryAsync(Id);
}
