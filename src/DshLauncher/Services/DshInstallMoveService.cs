using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>安装目录移动结果（用于 UI 提示与结构化日志）。</summary>
public sealed record DshInstallMoveResult(
    bool Ok,
    string Message,
    string? OldDirectory = null,
    string? NewDirectory = null,
    int ReboundInstances = 0)
{
    public static DshInstallMoveResult Failure(
        string message,
        string? oldDirectory = null,
        string? newDirectory = null) =>
        new(false, message, oldDirectory, newDirectory);
}

/// <summary>
/// 把「DSh 安装位置」整体搬到新目录（变更集 44）：
/// 移动 runtime 目录树（含 versions/），更新 launcher-settings.json，
/// 并把所有引用旧路径的实例（RootPath / DshExecutablePath / DshLaunchSpec）
/// 重写到新路径。同盘用 Directory.Move（原子重命名）；跨盘复制+校验+删除源。
/// 任一环节失败会尽力回滚，绝不把用户留在“半移动”状态。
/// </summary>
public sealed class DshInstallMoveService
{
    private readonly LauncherPaths _paths;
    private readonly InstanceRegistry _registry;
    private readonly VersionSettingsService _settings;

    public DshInstallMoveService(
        LauncherPaths? paths = null,
        InstanceRegistry? registry = null,
        VersionSettingsService? settings = null)
    {
        _paths = paths ?? new LauncherPaths();
        _registry = registry ?? new InstanceRegistry(_paths);
        _settings = settings ?? new VersionSettingsService(_paths);
    }

    /// <summary>当前实际安装目录：优先已配置且确实存在的，其次默认 managed 目录；都没有返回 null。</summary>
    public string? ResolveCurrentInstallDirectory()
    {
        var configured = _settings.ResolveDshInstallDirectory();
        if (LooksLikeInstall(configured))
        {
            return configured;
        }

        var managed = _paths.ManagedDshRuntimeDirectory;
        return LooksLikeInstall(managed) ? managed : null;
    }

    internal static bool LooksLikeInstall(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && (File.Exists(Path.Combine(directory, "dsh.cmd"))
            || File.Exists(Path.Combine(directory, "dsh"))
            || Directory.Exists(Path.Combine(directory, "versions"))
            || Directory.Exists(Path.Combine(directory, "node_modules", "@deepseek-ai", "dsh")));

    public async Task<DshInstallMoveResult> MoveAsync(
        string targetDirectory,
        Func<string, bool> isInstanceRunning,
        CancellationToken cancellationToken = default,
        string? sourceDirectory = null)
    {
        string? source = null;
        string? target = null;
        try
        {
            target = DshInstallService.NormalizeInstallDirectory(targetDirectory);
            if (target is null)
            {
                return DshInstallMoveResult.Failure("请先填写目标安装位置。");
            }

            // 显式指定源（如“当前选中实例的运行时”）优先；未指定时用配置的安装目录。
            source = !string.IsNullOrWhiteSpace(sourceDirectory) && LooksLikeInstall(sourceDirectory)
                ? Path.GetFullPath(sourceDirectory)
                : ResolveCurrentInstallDirectory();
            if (source is null)
            {
                return DshInstallMoveResult.Failure("没有找到可移动的 DSh 安装目录（可能尚未安装）。", null, target);
            }

            if (PathsEqual(source, target))
            {
                return DshInstallMoveResult.Failure("目标位置与当前安装位置相同，无需移动。", source, target);
            }

            if (IsUnder(target, source))
            {
                return DshInstallMoveResult.Failure("目标位置不能位于当前安装目录内部。", source, target);
            }

            if (IsUnder(source, target))
            {
                return DshInstallMoveResult.Failure("当前安装目录不能位于目标位置内部。", source, target);
            }

            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                return DshInstallMoveResult.Failure("目标目录已存在且不为空，请换一个位置或先清空它。", source, target);
            }

            var running = _registry.Load()
                .Where(instance => isInstanceRunning(instance.Id))
                .Select(instance => instance.Name)
                .ToArray();
            if (running.Length > 0)
            {
                return DshInstallMoveResult.Failure(
                    $"请先停止正在运行的实例：{string.Join("、", running)}。",
                    source,
                    target);
            }

            // 必须在移动前先把重绑结果算好：InstanceRegistry.Load() 会把指向
            // 已不存在文件的路径清空，移动后旧路径就无从重写了。
            var pendingRebinds = _registry.Load()
                .Select(instance => (Original: instance, Updated: RebindPaths(instance, source, target)))
                .Where(pair => !ReferenceEquals(pair.Original, pair.Updated))
                .ToArray();

            await Task.Run(() => MoveDirectory(source, target), cancellationToken);

            var launcherSettings = _settings.ReadLauncherSettings();
            launcherSettings.DshInstallDirectory = target;
            _settings.SaveLauncherSettings(launcherSettings);

            foreach (var pair in pendingRebinds)
            {
                _registry.Update(pair.Updated);
            }

            var rebound = pendingRebinds.Length;
            LauncherLog.Info(
                "DSh 安装位置已移动。",
                null,
                new { from = source, to = target, reboundInstances = rebound });
            return new DshInstallMoveResult(
                true,
                $"安装位置已移动到 {target}；已更新 {rebound} 个实例的运行时引用。",
                source,
                target,
                rebound);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DshInstallMoveResult.Failure("移动已取消。", source, target);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            // 目录移动已发生但后续步骤失败时，尽力搬回去，避免半移动。
            if (source is not null
                && target is not null
                && !Directory.Exists(source)
                && Directory.Exists(target))
            {
                try
                {
                    MoveDirectory(target, source);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    // 回滚失败时保留现场，提示用户手动处理。
                }
            }

            LauncherLog.Error(
                "DSh 安装位置移动失败。",
                ErrorCodes.E1008,
                new { from = source, to = target, error = ex.Message });
            return DshInstallMoveResult.Failure($"移动失败：{ex.Message}", source, target);
        }
    }

    /// <summary>
    /// 多源移动：勾选一个时直接搬到目标目录；勾选多个时，每个运行时分别搬到
    /// 目标目录下的同名子目录（避免不同版本互相覆盖），最后把安装位置指向目标根。
    /// </summary>
    public async Task<DshInstallMoveResult> MoveManyAsync(
        IReadOnlyList<string> sourceDirectories,
        string targetDirectory,
        Func<string, bool> isInstanceRunning,
        CancellationToken cancellationToken = default)
    {
        var sources = sourceDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
        {
            return DshInstallMoveResult.Failure("请先勾选要移动的运行时。");
        }

        if (sources.Length == 1)
        {
            return await MoveAsync(targetDirectory, isInstanceRunning, cancellationToken, sources[0]);
        }

        var target = DshInstallService.NormalizeInstallDirectory(targetDirectory);
        if (target is null)
        {
            return DshInstallMoveResult.Failure("请先填写目标位置。");
        }

        var moved = new List<string>();
        var rebound = 0;
        foreach (var source in sources)
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (string.IsNullOrWhiteSpace(leaf))
            {
                leaf = "dsh";
            }

            var destination = Path.Combine(target, leaf);
            var result = await MoveAsync(destination, isInstanceRunning, cancellationToken, source);
            if (!result.Ok)
            {
                return DshInstallMoveResult.Failure(
                    $"移动 {source} 失败：{result.Message}（已完成 {moved.Count} 个）",
                    source,
                    destination);
            }

            rebound += result.ReboundInstances;
            moved.Add(destination);
        }

        // 各运行时已分别落在目标目录的同名子目录下；安装位置指向目标根。
        var launcherSettings = _settings.ReadLauncherSettings();
        launcherSettings.DshInstallDirectory = target;
        _settings.SaveLauncherSettings(launcherSettings);
        return new DshInstallMoveResult(
            true,
            $"已移动 {moved.Count} 个运行时到 {target} 下的同名子目录；共更新 {rebound} 个实例的引用。",
            null,
            target,
            rebound);
    }

    /// <summary>把实例中所有位于旧安装目录下的路径重写到新目录（纯函数，便于回归测试）。</summary>
    internal static ManagerInstance RebindPaths(ManagerInstance instance, string oldRoot, string newRoot)
    {
        var spec = instance.DshLaunchSpec;
        var updatedSpec = spec is null
            ? null
            : spec with
            {
                HostPath = RewritePrefix(spec.HostPath, oldRoot, newRoot) ?? spec.HostPath,
                EntryPointPath = RewritePrefix(spec.EntryPointPath, oldRoot, newRoot),
                NodeExecutablePath = RewritePrefix(spec.NodeExecutablePath, oldRoot, newRoot),
                PnpmScriptPath = RewritePrefix(spec.PnpmScriptPath, oldRoot, newRoot)
            };
        var rootPath = RewritePrefix(instance.RootPath, oldRoot, newRoot) ?? instance.RootPath;
        var executablePath = RewritePrefix(instance.DshExecutablePath, oldRoot, newRoot);
        if (string.Equals(rootPath, instance.RootPath, StringComparison.Ordinal)
            && string.Equals(executablePath, instance.DshExecutablePath, StringComparison.Ordinal)
            && Equals(spec, updatedSpec))
        {
            return instance;
        }

        return instance with
        {
            RootPath = rootPath,
            DshExecutablePath = executablePath,
            DshLaunchSpec = updatedSpec
        };
    }

    /// <summary>把 oldRoot 前缀替换为 newRoot；不在其下（或无法解析）时原样返回。</summary>
    internal static string? RewritePrefix(string? path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string fullPath;
        string normalizedOld;
        try
        {
            fullPath = Path.GetFullPath(path);
            normalizedOld = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                normalizedOld,
                StringComparison.OrdinalIgnoreCase))
        {
            return newRoot;
        }

        if (fullPath.Length > normalizedOld.Length
            && fullPath.StartsWith(normalizedOld, StringComparison.OrdinalIgnoreCase)
            && (fullPath[normalizedOld.Length] == Path.DirectorySeparatorChar
                || fullPath[normalizedOld.Length] == Path.AltDirectorySeparatorChar))
        {
            return Path.Combine(newRoot, fullPath[(normalizedOld.Length + 1)..]);
        }

        return path;
    }

    internal static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return normalizedCandidate.Length > normalizedRoot.Length
            && normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && (normalizedCandidate[normalizedRoot.Length] == Path.DirectorySeparatorChar
                || normalizedCandidate[normalizedRoot.Length] == Path.AltDirectorySeparatorChar);
    }

    private static void MoveDirectory(string source, string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(source)),
            Path.GetPathRoot(Path.GetFullPath(target)),
            StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            Directory.Move(source, target);
            return;
        }

        // 跨盘：复制 → 校验 → 删除源；任一步失败都保持源目录完整。
        CopyDirectory(source, target);
        if (!LooksLikeInstall(target))
        {
            TryDeleteDirectory(target);
            throw new IOException("跨盘复制后的目录不完整，已回滚（原目录未删除）。");
        }

        FileSystemCleanup.DeleteDirectoryRecursive(source);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("安装目录包含符号链接/联接点，无法跨盘移动；请改用「准备运行环境」在新位置重新安装。");
            }

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("安装目录包含符号链接/联接点，无法跨盘移动；请改用「准备运行环境」在新位置重新安装。");
            }

            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                FileSystemCleanup.DeleteDirectoryRecursive(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主结果。
        }
    }
}
