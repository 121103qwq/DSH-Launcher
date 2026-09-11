using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>目标运行版本解析结果。</summary>
public sealed record InstanceVersionTargetResolution(
    string Version,
    DshRuntimeInfo Runtime,
    bool AlreadyInstalled,
    string VersionDirectory)
{
    public string PackageRoot => Runtime.PackageRoot ?? string.Empty;
}

public enum VersionSwitchDirection
{
    Same,
    Upgrade,
    Downgrade,
    Unknown
}

/// <summary>切换预检报告（UI 展示 + 服务侧据此拒绝不可继续的情况）。</summary>
public sealed record InstanceVersionSwitchPrecheck(
    string CurrentVersion,
    string TargetVersion,
    VersionSwitchDirection Direction,
    string? TargetNodeEngine,
    NodeRuntimeCompatibility NodeCompatibility,
    IReadOnlyList<string> IncompatiblePlugins,
    int VersionedSessionCount,
    bool TargetReadsVersionedSessions,
    IReadOnlyList<string> Warnings)
{
    /// <summary>目标读不了现有带版本会话（降级场景）：需要先导出会话备份。</summary>
    public bool RequiresSessionBackup => VersionedSessionCount > 0 && !TargetReadsVersionedSessions;

    /// <summary>Node 引擎不兼容时不允许继续（其它情况可继续 + 警告）。</summary>
    public bool CanProceed => NodeCompatibility != NodeRuntimeCompatibility.Incompatible;

    public string DirectionText => Direction switch
    {
        VersionSwitchDirection.Upgrade => "升级",
        VersionSwitchDirection.Downgrade => "降级",
        VersionSwitchDirection.Same => "同版本重绑",
        _ => "版本变更"
    };
}

public sealed record InstanceVersionSwitchResult(
    bool Ok,
    ManagerInstance? Instance,
    string Summary,
    string? Error)
{
    public static InstanceVersionSwitchResult Success(ManagerInstance instance, string summary) =>
        new(true, instance, summary, null);

    public static InstanceVersionSwitchResult Failure(string error) =>
        new(false, null, "更换运行版本失败。", error);
}

/// <summary>
/// 更换某个实例使用的 dsh 运行版本（升级/降级），保留该实例的 DSH_HOME（work-log/52）。
/// 只改运行时的绑定（RootPath/入口/启动描述/版本号），不动配置、插件与会话；会话格式迁移交给 dsh。
/// </summary>
public sealed class InstanceVersionSwitchService
{
    private readonly VersionSettingsService _settings;
    private readonly DshInstallService _installer;
    private readonly InstanceRegistry _registry;
    private readonly VersionSnapshotService? _snapshots;
    private readonly ConversationService _conversations;
    private readonly Func<string, bool> _isRunning;

    public InstanceVersionSwitchService(
        VersionSettingsService? settings = null,
        DshInstallService? installer = null,
        InstanceRegistry? registry = null,
        VersionSnapshotService? snapshots = null,
        ConversationService? conversations = null,
        Func<string, bool>? isRunning = null)
    {
        _settings = settings ?? new VersionSettingsService();
        _installer = installer ?? new DshInstallService();
        _registry = registry ?? new InstanceRegistry();
        _snapshots = snapshots;
        _conversations = conversations ?? new ConversationService();
        _isRunning = isRunning ?? (_ => false);
    }

    /// <summary>
    /// 解析目标版本：先看本机 <c>versions/&lt;version&gt;</c>、主运行根以及实例当前绑定的根
    /// （避免把已存在的运行时又下载一份），都没有且允许下载时从官方 npm 取。
    /// </summary>
    public async Task<InstanceVersionTargetResolution> ResolveTargetAsync(
        string requestedVersion,
        NodeRuntimeInfo nodeRuntime,
        bool allowDownload,
        ManagerInstance? instance = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var version = (requestedVersion ?? string.Empty).Trim().TrimStart('v', 'V');
        if (!DshInstallService.IsSafePackageVersion(version))
        {
            throw new InvalidDataException("DSh 版本号格式无效。 ");
        }

        var installDirectory = _settings.ResolveDshInstallDirectory();
        var versionDirectory = Path.Combine(installDirectory, "versions", version);
        var candidates = new List<string> { versionDirectory };
        if (Directory.Exists(installDirectory))
        {
            candidates.Add(installDirectory);   // 主运行根（如 dsh_runtime 本身就是某个版本）
        }

        if (instance is not null && !string.IsNullOrWhiteSpace(instance.RootPath))
        {
            candidates.Add(instance.RootPath);  // 实例当前绑定的运行树（切回原版本时不重复下载）
        }

        var packageRoot = FindMatchingPackageRoot(candidates, version);
        var alreadyInstalled = packageRoot is not null;
        if (!alreadyInstalled)
        {
            if (!allowDownload)
            {
                throw new InvalidOperationException($"本机没有 DSh {version}，且当前未允许下载。 ");
            }

            if (!nodeRuntime.IsAvailable || string.IsNullOrWhiteSpace(nodeRuntime.ExecutablePath))
            {
                throw new InvalidOperationException("缺少可用的 Node.js，无法下载所选 DSh 版本。 ");
            }

            progress?.Report($"本机没有 DSh {version}，正在从官方 npm 包下载…");
            var install = await _installer.InstallVersionAsync(
                nodeRuntime,
                version,
                DshInstallService.OfficialRegistry,
                versionDirectory,
                cancellationToken);
            if (!install.IsSuccess)
            {
                throw new InvalidOperationException(install.Error ?? $"DSh {version} 下载失败。 ");
            }

            packageRoot = FindMatchingPackageRoot(new[] { versionDirectory }, version);
        }

        if (packageRoot is null)
        {
            throw new InvalidOperationException($"没有找到 DSh {version} 的有效运行目录。 ");
        }

        var launchSpec = DshRuntimeDetector.CreateLaunchSpecForPackageRoot(packageRoot);
        if (!DshRuntimeCommandFactory.IsUsable(launchSpec))
        {
            throw new InvalidOperationException($"DSh {version} 的启动入口不可用。 ");
        }

        var runtime = new DshRuntimeInfo(
            IsAvailable: true,
            ExecutablePath: launchSpec!.HostPath,
            Version: version,
            PackageRoot: packageRoot,
            Error: null,
            NodeEngine: DshRuntimeDetector.TryReadNodeEngine(packageRoot),
            LaunchSpec: launchSpec);
        return new InstanceVersionTargetResolution(version, runtime, alreadyInstalled, packageRoot);
    }

    /// <summary>在候选目录里找版本号匹配的 dsh 包根。</summary>
    internal static string? FindMatchingPackageRoot(IEnumerable<string> candidates, string version)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(candidate);
            if (packageRoot is null)
            {
                continue;
            }

            var actual = DshRuntimeDetector.TryReadPackageVersion(packageRoot);
            if (string.Equals(actual, version, StringComparison.OrdinalIgnoreCase))
            {
                return packageRoot;
            }
        }

        return null;
    }

    /// <summary>切换前预检：Node 引擎、插件 peerDependencies、会话代际与切换方向。</summary>
    public InstanceVersionSwitchPrecheck Precheck(
        ManagerInstance instance,
        InstanceVersionTargetResolution target,
        NodeRuntimeInfo nodeRuntime)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(target);

        var currentVersion = instance.DetectedVersion?.Trim().TrimStart('v', 'V') ?? string.Empty;
        var comparison = PluginCompatibility.Compare(target.Version, currentVersion);
        var direction = comparison > 0
            ? VersionSwitchDirection.Upgrade
            : comparison < 0
                ? VersionSwitchDirection.Downgrade
                : currentVersion.Length == 0
                    ? VersionSwitchDirection.Unknown
                    : VersionSwitchDirection.Same;

        var nodeCompatibility = nodeRuntime.GetCompatibility(target.Runtime.NodeEngine);
        var incompatiblePlugins = CheckInstalledPlugins(instance, target.Runtime);
        var sessionsRoot = Path.Combine(instance.DshHome, "sessions");
        var versionedSessions = CountVersionedSessions(sessionsRoot);
        var targetReadsVersioned = SessionFileNames.SupportsVersionedGenerations(
            target.Runtime.PackageRoot,
            target.Version);

        var warnings = new List<string>();
        if (nodeCompatibility == NodeRuntimeCompatibility.Incompatible)
        {
            warnings.Add($"本机 Node {nodeRuntime.VersionText} 不满足 DSh {target.Version} 要求的 {target.Runtime.NodeEngine}，无法切换。");
        }
        else if (nodeCompatibility == NodeRuntimeCompatibility.Unknown && !string.IsNullOrWhiteSpace(target.Runtime.NodeEngine))
        {
            warnings.Add($"DSh {target.Version} 声明了 Node 引擎 {target.Runtime.NodeEngine}，本机版本无法确认是否满足。");
        }

        if (incompatiblePlugins.Count > 0)
        {
            warnings.Add($"以下插件声明与 DSh {target.Version} 不兼容，切换后可能无法加载：{string.Join("、", incompatiblePlugins)}。");
        }

        if (versionedSessions > 0 && !targetReadsVersioned)
        {
            warnings.Add(
                $"该实例已有 {versionedSessions} 个新格式（session.vN）会话，DSh {target.Version} 读不了它们"
                + "（旧版 dsh 没有格式迁移链）。切换前建议先「导出会话备份」；版本下新建的会话同样读不了。");
        }
        else if (versionedSessions > 0)
        {
            warnings.Add($"该实例已有 {versionedSessions} 个新格式会话，DSh {target.Version} 会在读取时自行迁移（旧代际文件会保留）。");
        }

        return new InstanceVersionSwitchPrecheck(
            currentVersion,
            target.Version,
            direction,
            target.Runtime.NodeEngine,
            nodeCompatibility,
            incompatiblePlugins,
            versionedSessions,
            targetReadsVersioned,
            warnings);
    }

    /// <summary>
    /// 执行切换：可选快照/会话备份 → 重绑定 → 失败回滚原绑定。
    /// </summary>
    public async Task<InstanceVersionSwitchResult> SwitchAsync(
        ManagerInstance instance,
        InstanceVersionTargetResolution target,
        NodeRuntimeInfo nodeRuntime,
        bool createSnapshot,
        string? sessionBackupDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(target);

        if (instance.Kind != InstanceKind.Installed)
        {
            return InstanceVersionSwitchResult.Failure("只有 Installed 实例可以更换运行版本。");
        }

        if (instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached || _isRunning(instance.Id))
        {
            return InstanceVersionSwitchResult.Failure("实例正在运行（或为外部连接），请先停止后再更换运行版本。");
        }

        var precheck = Precheck(instance, target, nodeRuntime);
        if (!precheck.CanProceed)
        {
            return InstanceVersionSwitchResult.Failure(string.Join("；", precheck.Warnings));
        }

        // 快照/导出/写台账都是磁盘 I/O，放到后台线程避免卡住 UI（progress 会自动回到创建它的上下文）。
        return await Task.Run(
            () => SwitchCore(instance, target, precheck, createSnapshot, sessionBackupDirectory, progress, cancellationToken),
            cancellationToken);
    }

    private InstanceVersionSwitchResult SwitchCore(
        ManagerInstance instance,
        InstanceVersionTargetResolution target,
        InstanceVersionSwitchPrecheck precheck,
        bool createSnapshot,
        string? sessionBackupDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (createSnapshot && _snapshots is not null)
            {
                progress?.Report("正在创建配置快照…");
                _snapshots.CreateSnapshot(instance, "更换运行版本前", automatic: false);
            }

            if (!string.IsNullOrWhiteSpace(sessionBackupDirectory))
            {
                progress?.Report("正在导出会话备份…");
                var exported = _conversations.ExportAll(instance, sessionBackupDirectory);
                progress?.Report($"已导出 {exported} 个会话。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在切换到 DSh {target.Version}…");
            var rebound = InstanceRuntimeRebinder.RebindInstalledInstance(instance, target.Runtime, force: true);
            if (rebound is null)
            {
                return InstanceVersionSwitchResult.Failure("目标运行时不完整，未执行切换。");
            }

            var persisted = _registry.Update(rebound);
            var summary = $"{precheck.DirectionText}完成：{instance.DetectedVersion ?? "未知"} → {persisted.DetectedVersion}"
                + "。DSH_HOME 未改动，配置/插件/会话保留。";
            return InstanceVersionSwitchResult.Success(persisted, summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException)
        {
            // 回滚：把原绑定写回（版本目录都还在，成本极低）。
            try
            {
                _registry.Update(instance);
            }
            catch (Exception rollbackException) when (rollbackException is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                return InstanceVersionSwitchResult.Failure(
                    $"{ex.Message}（回滚原绑定也失败：{rollbackException.Message}，请在实例设置里手动修复）");
            }

            return InstanceVersionSwitchResult.Failure($"{ex.Message}（已回滚到原版本绑定）");
        }
    }

    /// <summary>用"影子实例"（RootPath 指向目标运行树）检查已装插件的核心 peerDependencies。</summary>
    internal static IReadOnlyList<string> CheckInstalledPlugins(
        ManagerInstance instance,
        DshRuntimeInfo targetRuntime)
    {
        var manifestPath = Path.Combine(instance.DshHome, "profiles", "web", "package.json");
        if (!File.Exists(manifestPath))
        {
            return Array.Empty<string>();
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }

        if (root is null)
        {
            return Array.Empty<string>();
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root["dependencies"] is JsonObject dependencies)
        {
            foreach (var dependency in dependencies)
            {
                names.Add(dependency.Key);
            }
        }

        if (root["dsh"] is JsonObject dsh
            && dsh["profile"] is JsonObject profile
            && profile["bundles"] is JsonArray bundles)
        {
            foreach (var bundle in bundles)
            {
                if (bundle is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        if (names.Count == 0)
        {
            return Array.Empty<string>();
        }

        var probe = instance with
        {
            RootPath = targetRuntime.PackageRoot ?? instance.RootPath,
            DshExecutablePath = targetRuntime.ExecutablePath,
            DshLaunchSpec = targetRuntime.EffectiveLaunchSpec,
            DetectedVersion = targetRuntime.Version
        };
        var incompatible = new List<string>();
        foreach (var name in names.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (DshCoreBundles.IsCore(name))
            {
                continue;
            }

            var manifest = TryReadInstalledManifest(instance, name);
            if (manifest is null)
            {
                continue;
            }

            var issues = PluginCompatibility.Check(manifest, probe);
            if (issues.Count > 0)
            {
                incompatible.Add($"{name}（{PluginCompatibility.Describe(issues)}）");
            }
        }

        return incompatible;
    }

    private static string? TryReadInstalledManifest(ManagerInstance instance, string packageName)
    {
        var segments = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(static segment => segment is "." or ".." or "" || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return null;
        }

        foreach (var modules in new[]
                 {
                     Path.Combine(instance.DshHome, "profiles", "web", "node_modules"),
                     Path.Combine(instance.DshHome, "profiles", "node_modules")
                 })
        {
            var candidate = Path.Combine(new[] { modules }.Concat(segments).Append("package.json").ToArray());
            try
            {
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate, Encoding.UTF8);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个候选读失败继续下一个。
            }
        }

        return null;
    }

    /// <summary>统计"带版本号代际"（vN，N&gt;0）的会话个数。</summary>
    internal static int CountVersionedSessions(string sessionsRoot)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot) || !Directory.Exists(sessionsRoot))
        {
            return 0;
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*", SearchOption.AllDirectories))
            {
                if (SessionFileNames.TryParse(Path.GetFileName(path), out var version, out _) && version > 0)
                {
                    directories.Add(Path.GetDirectoryName(path) ?? path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return directories.Count;
        }

        return directories.Count;
    }
}
