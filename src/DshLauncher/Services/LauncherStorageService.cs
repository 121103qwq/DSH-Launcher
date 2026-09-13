using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>一类可清点/可清理的 Launcher 自有存储。</summary>
public sealed record LauncherStorageCategory(
    string Id,
    string Title,
    string Description,
    string Path,
    long SizeBytes,
    int FileCount,
    DateTimeOffset? LastWrite,
    bool Cleanable,
    string? Note,
    IReadOnlyList<string>? IncludeDirectoryPrefixes = null,
    IReadOnlyList<string>? ExcludeChildDirectoryNames = null,
    IReadOnlyList<string>? Paths = null)
{
    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : SizeBytes < 1024 * 1024
            ? $"{SizeBytes / 1024.0:F1} KB"
            : SizeBytes < 1024L * 1024 * 1024
                ? $"{SizeBytes / (1024.0 * 1024):F1} MB"
                : $"{SizeBytes / (1024.0 * 1024 * 1024):F2} GB";

    public string LastWriteText => LastWrite is null
        ? "—"
        : LastWrite.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string CleanableText => Cleanable ? "可清理" : "不清理";

    /// <summary>列表里的一行说明：描述 + 备注。</summary>
    public string DetailText => string.IsNullOrWhiteSpace(Note) ? Description : $"{Description}（{Note}）";
}

/// <summary>一次清理的结果。</summary>
public sealed record LauncherStorageCleanResult(
    int RemovedCount,
    long FreedBytes,
    IReadOnlyList<string> Failures);

/// <summary>
/// 存储清点与清理。原则（work-log/61）：
/// <list type="number">
/// <item>只统计/清理 **Launcher 自己拥有的文件**；会话、凭据、实例 dsh-home、手动快照一律不碰。</item>
/// <item>用户主动创建的备份（会话备份、换版本时的会话导出）可以清理，但走 **回收站** 以便恢复。</item>
/// <item>自动生成且已有上限的（自动快照）只统计，不做第二次删除入口，避免两套回收逻辑打架。</item>
/// </list>
/// </summary>
public sealed class LauncherStorageService
{
    private readonly LauncherPaths _paths;
    private readonly IReadOnlyList<ManagerInstance> _instances;

    public LauncherStorageService(LauncherPaths? paths = null, IEnumerable<ManagerInstance>? instances = null)
    {
        _paths = paths ?? new LauncherPaths();
        _instances = (instances ?? Array.Empty<ManagerInstance>()).ToArray();
    }

    /// <summary>Launcher 数据根（用于「打开目录」）。</summary>
    public string RootDirectory => _paths.RootDirectory;

    /// <summary>安全体检导出物的保留份数上限（超出后回收最旧的，走回收站可恢复）。</summary>
    public const int MaximumAuditExports = 20;

    /// <summary>安全体检导出目录（Q7）：设置页导出的体检结果 JSON 默认落在这里，纳入本服务的清理与保留策略。</summary>
    public string AuditExportDirectory => Path.Combine(_paths.RootDirectory, "audit");

    public IReadOnlyList<LauncherStorageCategory> Scan()
    {
        var logDirectory = LauncherLog.LogDirectory;
        var categories = new List<LauncherStorageCategory>
        {
            BuildFileCategory(
                "crash",
                "崩溃日志",
                "未处理异常的完整堆栈；超 2 MB 轮转，同类异常 60 秒内折叠",
                new[] { Path.Combine(logDirectory, "crash.log"), Path.Combine(logDirectory, "crash.log.old") },
                cleanable: true,
                note: "清空不影响功能，只失去历史证据"),
            BuildFileCategory(
                "old-logs",
                "轮转旧日志",
                "launcher.log / watchdog.log 的轮转副本",
                new[]
                {
                    Path.Combine(logDirectory, "launcher.log.old"),
                    Path.Combine(logDirectory, "watchdog.log.1")
                },
                cleanable: true,
                note: null),
            BuildFileCategory(
                "market-cache",
                "市场目录缓存",
                "Plugin / Skill 市场目录快照（下次刷新目录时重建）",
                new[]
                {
                    Path.Combine(_paths.RootDirectory, "marketplace-cache.json"),
                    Path.Combine(_paths.RootDirectory, "skill-market-cache.json")
                },
                cleanable: true,
                note: "删除后市场页首次打开需要联网刷新"),
            BuildFileCategory(
                "runtime-cache",
                "运行环境缓存",
                "上次扫描到的 DSh 运行目录（下次启动重扫）",
                new[] { Path.Combine(_paths.RootDirectory, "runtime-cache.json") },
                cleanable: true,
                note: null)
        };

        categories.Add(BuildDirectoryCategory(
            "audit-exports",
            "安全体检导出物",
            $"在设置页导出的体检结果 JSON（保留最近 {MaximumAuditExports} 份）",
            new[] { AuditExportDirectory },
            cleanable: true,
            note: "含本机文件路径与行号（默认不含凭据片段）；走回收站可恢复"));

        categories.Add(BuildDirectoryCategory(
            "plugin-snapshots",
            "插件操作回滚点",
            "插件安装/更新前自动备份的 profile 配置（保留最近 10 份）",
            _instances.Select(instance => Path.Combine(_paths.GetInstanceBackupDirectory(instance.Id), "plugins")),
            cleanable: true,
            note: "删除后无法回滚到这些插件配置"));

        categories.Add(BuildDirectoryCategory(
            "auto-snapshots",
            "自动配置快照",
            "保存设置/换版本前的自动快照（已有上限：10 份 / 256 MB）",
            _instances.Select(instance => Path.Combine(_paths.GetInstanceBackupDirectory(instance.Id), "snapshots")),
            cleanable: false,
            note: "由版本快照服务自动回收；手动快照不在清理范围内"));

        categories.Add(BuildDirectoryCategory(
            "conversation-backups",
            "会话备份文件",
            "在对话页点「备份」生成的 .jsonl 副本",
            _instances.Select(instance => _paths.GetInstanceBackupDirectory(instance.Id)),
            cleanable: true,
            note: "只删备份副本，实例内的会话文件不受影响；走回收站可恢复",
            excludeDirectoryNames: new[] { "snapshots", "plugins" }));

        categories.Add(BuildDirectoryCategory(
            "session-exports",
            "换版本会话导出",
            "更换运行版本时勾选导出的会话副本（session-backup-*）",
            _instances.Select(instance => Path.Combine(_paths.InstancesDirectory, instance.Id)),
            cleanable: true,
            note: "只删导出副本，实例内会话不受影响",
            includeDirectoryNames: new[] { "session-backup-" }));

        categories.Add(BuildDirectoryCategory(
            "instances",
            "实例数据（不清理）",
            "各实例的 DSH_HOME：会话、配置、插件、凭据都在这里",
            new[] { _paths.InstancesDirectory },
            cleanable: false,
            note: "Launcher 不提供一键清理；请用版本控制页删除整个实例"));

        return categories;
    }

    /// <summary>清理指定类别（只处理 <see cref=“LauncherStorageCategory.Cleanable”/> 的项）。</summary>
    public LauncherStorageCleanResult Clean(IEnumerable<string> categoryIds)
    {
        var wanted = new HashSet<string>(categoryIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        return CleanCategories(Scan().Where(item => wanted.Contains(item.Id)));
    }

    /// <summary>
    /// 清理全部「可清理」类别。与 <c>Clean(可清理类别的 Id)</c> 结果一致，但只用一次清点
    /// —— 界面「存储与清理」按钮在确认后不需要再自己扫一遍（变更集 152：原来一次点击要扫两次）。
    /// </summary>
    public LauncherStorageCleanResult CleanAllCleanable() =>
        CleanCategories(Scan().Where(item => item.Cleanable));

    private LauncherStorageCleanResult CleanCategories(IEnumerable<LauncherStorageCategory> categories)
    {
        var removed = 0;
        long freed = 0;
        var failures = new List<string>();
        foreach (var category in categories)
        {
            if (!category.Cleanable)
            {
                failures.Add($"{category.Title}：按策略不参与清理");
                continue;
            }

            switch (category.Id)
            {
                case "crash":
                    TrimCrashLogs(ref removed, ref freed, failures);
                    break;
                case "old-logs":
                case "market-cache":
                case "runtime-cache":
                    RemoveFiles(category, ref removed, ref freed, failures);
                    break;
                case "conversation-backups":
                    RemoveFiles(category, ref removed, ref freed, failures);
                    break;
                case "plugin-snapshots":
                case "session-exports":
                    RemoveDirectories(category, ref removed, ref freed, failures);
                    break;
                default:
                    failures.Add($"{category.Title}：没有对应的清理实现");
                    break;
            }
        }

        return new LauncherStorageCleanResult(removed, freed, failures);
    }

    /// <summary>崩溃日志：删掉轮转副本并把当前文件清空（保留文件本身，崩溃处理继续追加）。</summary>
    private void TrimCrashLogs(ref int removed, ref long freed, List<string> failures)
    {
        var logDirectory = LauncherLog.LogDirectory;
        var current = Path.Combine(logDirectory, "crash.log");
        var old = current + ".old";
        if (File.Exists(old))
        {
            RemovePath(old, isDirectory: false, ref removed, ref freed, failures);
        }

        try
        {
            if (File.Exists(current))
            {
                freed += new FileInfo(current).Length;
                File.WriteAllText(current, string.Empty);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"crash.log：{ex.Message}");
        }
    }

    /// <summary>
    /// 安全体检导出物的保留上限：只保留最近 <see cref="MaximumAuditExports"/> 份（按修改时间倒序），
    /// 其余回收。默认走回收站（可恢复）；<paramref name="deleteOverride"/> 仅供自测注入。
    /// 只处理 <c>*.json</c>，不碰目录里的其它文件。
    /// </summary>
    public int PruneAuditExports(Action<string>? deleteOverride = null)
    {
        var directory = AuditExportDirectory;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(directory)
                .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var pruned = 0;
        foreach (var file in files.Skip(MaximumAuditExports))
        {
            try
            {
                if (deleteOverride is not null)
                {
                    deleteOverride(file.FullName);
                }
                else
                {
                    SendFileToRecycleBin(file.FullName);
                }

                pruned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 保留上限是"尽力而为"：单个文件删不掉不影响导出本身。
            }
        }

        return pruned;
    }

    private void RemoveFiles(LauncherStorageCategory category, ref int removed, ref long freed, List<string> failures)
    {
        foreach (var file in EnumerateCategoryFiles(category))
        {
            RemovePath(file, isDirectory: false, ref removed, ref freed, failures);
        }
    }

    private void RemoveDirectories(LauncherStorageCategory category, ref int removed, ref long freed, List<string> failures)
    {
        foreach (var directory in EnumerateCategoryDirectories(category))
        {
            RemovePath(directory, isDirectory: true, ref removed, ref freed, failures);
        }
    }

    private void RemovePath(string path, bool isDirectory, ref int removed, ref long freed, List<string> failures)
    {
        try
        {
            var size = Measure(path, out var fileCount);
            if (isDirectory)
            {
                SendDirectoryToRecycleBin(path);
            }
            else
            {
                SendFileToRecycleBin(path);
            }

            removed += Math.Max(1, fileCount);
            freed += size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failures.Add($"{Path.GetFileName(path)}：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除走回收站（可恢复）。用 <c>Microsoft.VisualBasic.FileIO</c>，它在 Windows 桌面运行时可用，
    /// 比 P/Invoke SHFileOperation 少一大截样板代码。
    /// </summary>
    private static void SendFileToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    private static void SendDirectoryToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    private static IEnumerable<string> EnumerateCategoryFiles(LauncherStorageCategory category)
    {
        // 变更集 153：文件类别可能登记多条路径（轮转旧日志 = launcher.log.old + watchdog.log.1；
        // 市场缓存 = marketplace-cache.json + skill-market-cache.json）。原来只看 category.Path（第一条），
        // 第一条不存在时整个类别就静默不删 —— 改为逐个路径处理：存在即产出、不存在跳过。
        foreach (var path in category.Paths ?? new[] { category.Path })
        {
            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            // 目录类别在 BuildDirectoryCategory 阶段已经把范围收窄，这里再按“排除子目录”的约定过滤。
            foreach (var file in SafeEnumerateFiles(path))
            {
                var relative = Path.GetRelativePath(path, file);
                if (relative.Contains(Path.DirectorySeparatorChar))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static IEnumerable<string> EnumerateCategoryDirectories(LauncherStorageCategory category)
    {
        if (!Directory.Exists(category.Path))
        {
            yield break;
        }

        var prefixes = category.IncludeDirectoryPrefixes;
        foreach (var directory in SafeEnumerateDirectories(category.Path))
        {
            // 过滤条件必须和统计时一致：会话导出目录只认 session-backup-*，
            // 否则会把 instances/<id>/dsh-home 这类实例数据也当成清理目标。
            if (prefixes is { Count: > 0 }
                && !prefixes.Any(prefix => Path.GetFileName(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return directory;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static LauncherStorageCategory BuildFileCategory(
        string id,
        string title,
        string description,
        IEnumerable<string> paths,
        bool cleanable,
        string? note)
    {
        // 变更集 153：一个文件类别可能登记多条路径（如 轮转旧日志 = launcher.log.old + watchdog.log.1），
        // 这里保留完整列表并在删除时逐条处理 —— 原来只留第一条，第一条不存在时删除会静默变成空操作。
        var pathList = paths as IReadOnlyList<string> ?? paths.ToArray();
        long size = 0;
        var count = 0;
        DateTimeOffset? lastWrite = null;
        var primary = string.Empty;
        foreach (var path in pathList)
        {
            primary = string.IsNullOrEmpty(primary) ? path : primary;
            if (!File.Exists(path))
            {
                continue;
            }

            var info = new FileInfo(path);
            size += info.Length;
            count++;
            var stamp = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastWrite is null || stamp > lastWrite)
            {
                lastWrite = stamp;
            }
        }

        return new LauncherStorageCategory(id, title, description, primary, size, count, lastWrite, cleanable, note, Paths: pathList);
    }

    private static LauncherStorageCategory BuildDirectoryCategory(
        string id,
        string title,
        string description,
        IEnumerable<string> directories,
        bool cleanable,
        string? note,
        IReadOnlyList<string>? excludeDirectoryNames = null,
        IReadOnlyList<string>? includeDirectoryNames = null)
    {
        long size = 0;
        var count = 0;
        DateTimeOffset? lastWrite = null;
        var primary = string.Empty;
        foreach (var directory in directories)
        {
            primary = string.IsNullOrEmpty(primary) ? directory : primary;
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (excludeDirectoryNames is not null)
            {
                // 只数“直接子文件”（例如会话备份），跳过 snapshots/plugins 等子目录。
                foreach (var file in SafeEnumerateFiles(directory))
                {
                    size += new FileInfo(file).Length;
                    count++;
                    var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                    if (lastWrite is null || stamp > lastWrite)
                    {
                        lastWrite = stamp;
                    }
                }

                continue;
            }

            if (includeDirectoryNames is not null)
            {
                foreach (var child in SafeEnumerateDirectories(directory)
                             .Where(child => includeDirectoryNames.Any(prefix =>
                                 Path.GetFileName(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                {
                    size += Measure(child, out var files);
                    count += files;
                    var stamp = new DateTimeOffset(Directory.GetLastWriteTimeUtc(child), TimeSpan.Zero);
                    if (lastWrite is null || stamp > lastWrite)
                    {
                        lastWrite = stamp;
                    }
                }

                continue;
            }

            size += Measure(directory, out var fileCount);
            count += fileCount;
            var directoryStamp = new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero);
            if (lastWrite is null || directoryStamp > lastWrite)
            {
                lastWrite = directoryStamp;
            }
        }

        return new LauncherStorageCategory(
            id,
            title,
            description,
            primary,
            size,
            count,
            lastWrite,
            cleanable,
            note,
            includeDirectoryNames,
            excludeDirectoryNames);
    }

    /// <summary>递归统计目录大小/文件数（读不到的子树跳过）。</summary>
    public static long Measure(string path, out int fileCount)
    {
        fileCount = 0;
        long size = 0;
        if (File.Exists(path))
        {
            size = new FileInfo(path).Length;
            fileCount = 1;
            return size;
        }

        if (!Directory.Exists(path))
        {
            return 0;
        }

        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in SafeEnumerateFiles(directory))
            {
                try
                {
                    size += new FileInfo(file).Length;
                    fileCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件读不到时跳过。
                }
            }

            foreach (var child in SafeEnumerateDirectories(directory))
            {
                pending.Push(child);
            }
        }

        return size;
    }
}
