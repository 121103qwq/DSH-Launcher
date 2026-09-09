using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher.Services;

/// <summary>
/// 每实例的启动证据记录：内存环形缓冲（默认 60 条，供「运行状况」展示）+
/// 追加落盘到 <c>&lt;DSH_HOME&gt;\.dsh-launcher\startup-evidence.jsonl</c>
/// （默认保留最近 200 条），Launcher 重启后仍能回看启动/崩溃现场。
/// 落盘全程尽力而为——任何 IO/解析异常都不会影响启动流程。
/// </summary>
public sealed class StartupEvidenceStore
{
    public const int DefaultCapacity = 60;
    public const int DefaultPersistedCapacity = 200;

    private const string FileName = "startup-evidence.jsonl";
    private const int TrimEveryAppends = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 本地文件/诊断包直接可读（中文不转 \uXXXX）。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly int _persistedCapacity;
    private readonly Func<string, string?>? _dshHomeResolver;
    private readonly Dictionary<string, List<StartupEvidence>> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _appendsSinceTrim = new(StringComparer.Ordinal);

    public StartupEvidenceStore(
        int capacity = DefaultCapacity,
        Func<string, string?>? dshHomeResolver = null,
        int persistedCapacity = DefaultPersistedCapacity)
    {
        _capacity = Math.Clamp(capacity, 3, 500);
        _persistedCapacity = Math.Clamp(persistedCapacity, _capacity, 2000);
        _dshHomeResolver = dshHomeResolver;
    }

    /// <summary>该实例的落盘路径（未配置 dsh 解析器时返回 null，仅内存模式）。</summary>
    public string? GetPersistedPath(string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) ? null : ResolvePath(instanceId);

    public void Record(string? instanceId, StartupEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoadedLocked(instanceId);
            var list = GetOrCreateLocked(instanceId);
            list.Add(evidence);
            if (list.Count > _capacity)
            {
                list.RemoveRange(0, list.Count - _capacity);
            }
        }

        AppendPersisted(instanceId, evidence);
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
            EnsureLoadedLocked(instanceId);
            return _entries.TryGetValue(instanceId, out var list)
                ? list.AsEnumerable().Reverse().ToArray()
                : Array.Empty<StartupEvidence>();
        }
    }

    /// <summary>清空内存与落盘记录（运行状况页「清空启动证据」）。</summary>
    public void Clear(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        string? path;
        lock (_gate)
        {
            _entries.Remove(instanceId);
            _loaded.Remove(instanceId);
            _appendsSinceTrim.Remove(instanceId);
            path = ResolvePath(instanceId);
        }

        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败不影响使用（下次追加会继续写）。
        }
    }

    private List<StartupEvidence> GetOrCreateLocked(string instanceId)
    {
        if (!_entries.TryGetValue(instanceId, out var list))
        {
            list = new List<StartupEvidence>();
            _entries[instanceId] = list;
        }

        return list;
    }

    private string? ResolvePath(string instanceId)
    {
        var home = _dshHomeResolver?.Invoke(instanceId);
        return string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, ".dsh-launcher", FileName);
    }

    /// <summary>首次访问该实例时把落盘记录读回内存（只读最近 persistedCapacity 条）。</summary>
    private void EnsureLoadedLocked(string instanceId)
    {
        if (!_loaded.Add(instanceId))
        {
            return;
        }

        var path = ResolvePath(instanceId);
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            var parsed = new List<StartupEvidence>();
            foreach (var line in File.ReadLines(path))
            {
                if (TryParseLine(line, out var evidence))
                {
                    parsed.Add(evidence);
                }
            }

            if (parsed.Count == 0)
            {
                return;
            }

            if (parsed.Count > _persistedCapacity)
            {
                parsed.RemoveRange(0, parsed.Count - _persistedCapacity);
                RewriteFile(path, parsed);
            }

            var list = GetOrCreateLocked(instanceId);
            list.InsertRange(0, parsed.TakeLast(_capacity));
            if (list.Count > _capacity)
            {
                list.RemoveRange(0, list.Count - _capacity);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 文件损坏/不可读：当作空记录，不抛出。
        }
    }

    private void AppendPersisted(string instanceId, StartupEvidence evidence)
    {
        var path = ResolvePath(instanceId);
        if (path is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(ToPersisted(evidence), JsonOptions);
            File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);

            lock (_gate)
            {
                _appendsSinceTrim.TryGetValue(instanceId, out var count);
                count++;
                if (count < TrimEveryAppends)
                {
                    _appendsSinceTrim[instanceId] = count;
                    return;
                }

                _appendsSinceTrim[instanceId] = 0;
            }

            TrimFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 落盘失败只影响历史回看，绝不影响启动。
        }
    }

    /// <summary>文件超过保留上限时重写为最近 persistedCapacity 条。</summary>
    private void TrimFile(string path)
    {
        try
        {
            var parsed = new List<StartupEvidence>();
            foreach (var line in File.ReadLines(path))
            {
                if (TryParseLine(line, out var evidence))
                {
                    parsed.Add(evidence);
                }
            }

            if (parsed.Count <= _persistedCapacity)
            {
                return;
            }

            parsed.RemoveRange(0, parsed.Count - _persistedCapacity);
            RewriteFile(path, parsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 同上：尽力而为。
        }
    }

    private static void RewriteFile(string path, IReadOnlyList<StartupEvidence> evidence)
    {
        var builder = new StringBuilder();
        foreach (var item in evidence)
        {
            builder.AppendLine(JsonSerializer.Serialize(ToPersisted(item), JsonOptions));
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, builder.ToString(), Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    private static PersistedEvidence ToPersisted(StartupEvidence evidence) => new(
        evidence.At.ToString("o"),
        evidence.Layer.ToString(),
        evidence.Summary,
        evidence.Detail);

    private static bool TryParseLine(string line, out StartupEvidence evidence)
    {
        evidence = new StartupEvidence(BootLayer.Process, string.Empty);
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedEvidence>(line, JsonOptions);
            if (persisted is null || string.IsNullOrWhiteSpace(persisted.Summary))
            {
                return false;
            }

            var layer = Enum.TryParse<BootLayer>(persisted.Layer, ignoreCase: true, out var parsedLayer)
                ? parsedLayer
                : BootLayer.Process;
            var at = DateTimeOffset.TryParse(persisted.At, out var parsedAt)
                ? parsedAt
                : DateTimeOffset.Now;
            evidence = new StartupEvidence(layer, persisted.Summary, persisted.Detail) { At = at };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record PersistedEvidence(string At, string Layer, string Summary, string? Detail);
}
