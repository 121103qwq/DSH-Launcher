using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>扫描到的 home 来源。</summary>
public enum DshHomeSource
{
    /// <summary>%USERPROFILE%\.dsh*（.dsh、.dsh-dev 等）。</summary>
    UserProfile,

    /// <summary>DSH_HOME 环境变量指向的目录。</summary>
    DshHomeEnvironment
}

/// <summary>profile 分类：web 可启动、tui 暂不支持启动、other 未知。</summary>
public enum DshProfileKind
{
    Web,
    Tui,
    Other
}

public sealed record ScannedDshProfile(string Name, DshProfileKind Kind);

public sealed record ScannedDshHome(
    string Path,
    DshHomeSource Source,
    IReadOnlyList<ScannedDshProfile> Profiles,
    bool AlreadyRegistered);

public sealed record DshEnvironmentScanResult(
    IReadOnlyList<ScannedDshHome> Homes,
    IReadOnlyList<string> Warnings)
{
    public static DshEnvironmentScanResult Empty { get; } =
        new(Array.Empty<ScannedDshHome>(), Array.Empty<string>());
}

/// <summary>
/// 扫描本机 DSH 环境（work-log/50，借鉴上游 dsh-plugins scan.rs 的思路）：
/// 只负责"发现 + 分类"，不登记实例；登记复用 DshHomeImportService + InstanceRegistry。
/// 与既有启动时自动导入的区别：这里枚举所有 .dsh* home（不止当前 DSH_HOME），
/// 且由用户勾选。WSL 扫描已按需求移除（本机 WSL 服务不可用，见 work-log/50 第五节）。
/// </summary>
public sealed class DshEnvironmentScanner
{
    /// <summary>TUI bundle（第三方 scope；上游 process.rs 同名常量）。</summary>
    public const string TuiBundle = "@deepseek-harness-tui/dsh-tui";

    private const string ProfilesDirectoryName = "profiles";

    private readonly string? _userProfileRoot;
    private readonly string? _dshHomeEnvironment;
    private readonly Func<string, string?> _readManifest;

    /// <summary>生产用构造：读真实 %USERPROFILE% / DSH_HOME / 文件系统。</summary>
    public DshEnvironmentScanner()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("DSH_HOME"),
            ReadManifestFromDisk)
    {
    }

    /// <summary>可注入构造（harness 用：不依赖真实环境变量与磁盘）。</summary>
    public DshEnvironmentScanner(
        string? userProfileRoot,
        string? dshHomeEnvironment,
        Func<string, string?> readManifest)
    {
        _userProfileRoot = userProfileRoot;
        _dshHomeEnvironment = dshHomeEnvironment;
        _readManifest = readManifest;
    }

    public Task<DshEnvironmentScanResult> ScanAsync(
        IReadOnlyCollection<ManagerInstance> existingInstances,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var homes = new List<ScannedDshHome>();
        var warnings = new List<string>();
        ScanLocalHomes(homes, warnings, BuildRegisteredHomeSet(existingInstances));
        return Task.FromResult<DshEnvironmentScanResult>(new(homes, warnings));
    }

    /// <summary>
    /// 枚举 %USERPROFILE%\.dsh* 目录 + DSH_HOME 环境变量目标（去重；DSH_HOME 命中已有项时不重复）。
    /// </summary>
    private void ScanLocalHomes(
        ICollection<ScannedDshHome> homes,
        ICollection<string> warnings,
        IReadOnlySet<string> registered)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_userProfileRoot) && Directory.Exists(_userProfileRoot))
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateDirectories(_userProfileRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"读取用户目录失败：{ex.Message}");
                entries = Array.Empty<string>();
            }

            foreach (var directory in entries
                         .Where(static path => Path.GetFileName(path)
                             .StartsWith(".dsh", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (IsReparsePoint(directory))
                {
                    warnings.Add($"跳过符号链接/重解析点 home：{directory}。");
                    continue;
                }

                var normalized = NormalizeForCompare(directory);
                if (!seen.Add(normalized))
                {
                    continue;
                }

                homes.Add(BuildHome(directory, DshHomeSource.UserProfile, registered));
            }
        }

        if (string.IsNullOrWhiteSpace(_dshHomeEnvironment))
        {
            return;
        }

        var envHome = TryNormalizeDirectory(ExpandHome(_dshHomeEnvironment));
        if (envHome is null)
        {
            warnings.Add($"DSH_HOME 指向的目录不存在：{_dshHomeEnvironment}。");
            return;
        }

        if (seen.Add(NormalizeForCompare(envHome)))
        {
            homes.Add(BuildHome(envHome, DshHomeSource.DshHomeEnvironment, registered));
        }
    }

    private ScannedDshHome BuildHome(
        string homePath,
        DshHomeSource source,
        IReadOnlySet<string> registered)
    {
        var profiles = EnumerateProfiles(homePath)
            .Select(name => new ScannedDshProfile(name, ClassifyProfile(homePath, name)))
            .ToArray();
        return new ScannedDshHome(
            homePath,
            source,
            profiles,
            registered.Contains(NormalizeForCompare(homePath)));
    }

    /// <summary>列 &lt;home&gt;/profiles 下的目录（跳过 node_modules / __temp__），按名字排序。</summary>
    private IEnumerable<string> EnumerateProfiles(string homePath)
    {
        var profilesDirectory = Path.Combine(homePath, ProfilesDirectoryName);
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(profilesDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }

        return entries
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .Where(static name => !string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "__temp__", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>读 profiles/&lt;name&gt;/package.json 的 dsh.profile.bundles 判类（与上游 profile_kind 同口径）。</summary>
    private DshProfileKind ClassifyProfile(string homePath, string profileName)
    {
        var manifestPath = Path.Combine(
            homePath,
            ProfilesDirectoryName,
            profileName,
            "package.json");
        var manifest = _readManifest(manifestPath);
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return DshProfileKind.Other;
        }

        try
        {
            if (JsonNode.Parse(manifest) is not JsonObject root
                || root["dsh"] is not JsonObject dsh
                || dsh["profile"] is not JsonObject profile
                || profile["bundles"] is not JsonArray bundles)
            {
                return DshProfileKind.Other;
            }

            var names = bundles
                .OfType<JsonValue>()
                .Select(static node => node.GetValue<string>())
                .ToArray();
            if (names.Any(static name =>
                    string.Equals(name, DshCoreBundles.WebApp, StringComparison.OrdinalIgnoreCase)))
            {
                return DshProfileKind.Web;
            }

            return names.Any(static name =>
                string.Equals(name, TuiBundle, StringComparison.OrdinalIgnoreCase))
                ? DshProfileKind.Tui
                : DshProfileKind.Other;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return DshProfileKind.Other;
        }
    }

    /// <summary>把已登记实例的 ImportedFromDshHome 收成归一化集合，用于标记"已登记"。</summary>
    private static IReadOnlySet<string> BuildRegisteredHomeSet(
        IReadOnlyCollection<ManagerInstance> existingInstances)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in existingInstances)
        {
            var imported = TryNormalizePath(instance.ImportedFromDshHome);
            if (imported is not null)
            {
                set.Add(NormalizeForCompare(imported));
            }
        }

        return set;
    }

    /// <summary>比较用归一化：大小写、斜杠方向、尾部分隔符。</summary>
    internal static string NormalizeForCompare(string path) =>
        path.Trim()
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryNormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException)
        {
            return null;
        }
    }

    private static string ExpandHome(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (trimmed == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (trimmed.StartsWith("~\\", StringComparison.Ordinal)
            || trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                trimmed[2..]);
        }

        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static string? ReadManifestFromDisk(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
