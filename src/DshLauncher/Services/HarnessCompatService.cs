using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>Update feed declared by the official desktop in resources\app-update.yml.</summary>
public sealed record OfficialUpdateFeed(string? Url, string? Channel, string? Provider);

public sealed record HarnessConfigCopyResult(IReadOnlyList<string> CopiedFiles, IReadOnlyList<string> BackupFiles)
{
    public bool CopiedAnything => CopiedFiles.Count > 0;
}

/// <summary>
/// Harness compatibility v1 (docs/HARNESS_COMPAT.md): read the official
/// desktop's update channel, and copy provider configuration between the
/// official DSH_HOME and a Launcher-managed DSH_HOME in either direction.
/// Copies are explicit, only between stopped homes, and back up what they replace.
/// </summary>
public sealed class HarnessCompatService
{
    /// <summary>Files that hold model-provider settings and credentials in a DSH_HOME.</summary>
    internal static readonly string[] ProviderFiles = { "settings.yaml", ".credentials.yaml" };

    private const long MaximumFileBytes = 1024 * 1024;
    private readonly Func<DateTimeOffset> _clock;

    public HarnessCompatService(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.Now);

    public static OfficialUpdateFeed? ReadUpdateFeed(string installRoot)
    {
        var path = Path.Combine(installRoot, "resources", "app-update.yml");
        if (!File.Exists(path)) return null;
        try
        {
            return ParseUpdateFeed(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Minimal reader for the flat electron-updater app-update.yml keys we need.</summary>
    internal static OfficialUpdateFeed ParseUpdateFeed(string yaml)
    {
        string? url = null, channel = null, provider = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = Unquote(line[(colon + 1)..].Trim());
            if (value.Length == 0) continue;
            switch (key)
            {
                case "url": url = value; break;
                case "channel": channel = value; break;
                case "provider": provider = value; break;
            }
        }
        return new OfficialUpdateFeed(url, channel, provider);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0]
            ? value[1..^1]
            : value;

    public static string DescribeFeed(OfficialUpdateFeed? feed) =>
        feed is null ? "未找到 app-update.yml（便携版或安装已损坏）"
        : $"更新通道：{feed.Channel ?? "latest"}{(feed.Url is null ? string.Empty : $" · {feed.Url}")}";

    /// <summary>
    /// Copy provider files from <paramref name="sourceHome"/> to <paramref name="targetHome"/>.
    /// Callers must make sure neither home is in use (official desktop or Launcher process).
    /// </summary>
    public HarnessConfigCopyResult CopyProviderConfiguration(
        string sourceHome,
        string targetHome,
        bool sourceInUse,
        bool targetInUse)
    {
        if (string.IsNullOrWhiteSpace(sourceHome) || string.IsNullOrWhiteSpace(targetHome))
            throw new ArgumentException("DSH_HOME 不能为空。");
        var source = Path.GetFullPath(sourceHome);
        var target = Path.GetFullPath(targetHome);
        if (ExternalDshHomeGuard.SameHome(source, target))
            throw new InvalidOperationException("源和目标是同一个 DSH_HOME，无需复制。");
        if (sourceInUse)
            throw new InvalidOperationException("源 DSH_HOME 正在被使用，请先退出官方桌面端或停止该版本。");
        if (targetInUse)
            throw new InvalidOperationException("目标 DSH_HOME 正在被使用，请先退出官方桌面端或停止该版本。");
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"源 DSH_HOME 不存在：{source}");

        var pending = new List<(string From, string To)>();
        foreach (var name in ProviderFiles)
        {
            var from = Path.Combine(source, name);
            if (!File.Exists(from)) continue;
            if (new FileInfo(from).Length > MaximumFileBytes)
                throw new InvalidDataException($"{name} 超过 1 MB，已拒绝复制。");
            pending.Add((from, Path.Combine(target, name)));
        }
        if (pending.Count == 0)
            return new HarnessConfigCopyResult(Array.Empty<string>(), Array.Empty<string>());

        Directory.CreateDirectory(target);
        var stamp = _clock().ToString("yyyyMMdd-HHmmss");
        var copied = new List<string>();
        var backups = new List<string>();
        foreach (var (from, to) in pending)
        {
            if (File.Exists(to))
            {
                var backup = $"{to}.bak-{stamp}";
                File.Copy(to, backup, overwrite: true);
                backups.Add(backup);
            }
            // Write to a temp file first so a failure never leaves a half-written config.
            var temp = to + ".tmp-launcher";
            File.Copy(from, temp, overwrite: true);
            File.Move(temp, to, overwrite: true);
            copied.Add(to);
        }
        return new HarnessConfigCopyResult(copied, backups);
    }
}
