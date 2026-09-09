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
    DshHomeEnvironment,

    /// <summary>WSL 发行版里的 ~/.dsh*（路径是 \\wsl$\... UNC）。</summary>
    Wsl
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
    string? WslDistro,
    IReadOnlyList<ScannedDshProfile> Profiles,
    bool AlreadyRegistered);

public sealed record DshEnvironmentScanResult(
    IReadOnlyList<ScannedDshHome> Homes,
    IReadOnlyList<string> Warnings)
{
    public static DshEnvironmentScanResult Empty { get; } =
        new(Array.Empty<ScannedDshHome>(), Array.Empty<string>());
}

/// <summary>WSL 发行版列表探测结果（失败时带错误文案，用于 UI 提示而不是抛异常）。</summary>
public sealed record WslDistroListResult(IReadOnlyList<string> Distros, string? Error);

/// <summary>
/// 扫描本机 DSH 环境（work-log/50，借鉴上游 dsh-plugins scan.rs 的思路）：
/// 只负责"发现 + 分类"，不登记实例；登记复用 DshHomeImportService + InstanceRegistry。
/// 与既有启动时自动导入的区别：这里枚举所有 .dsh* home（不止当前 DSH_HOME）、支持 WSL，
/// 且由用户勾选。WSL home 走"拷出来用 Windows 运行时跑"（方案 A）。
/// </summary>
public sealed class DshEnvironmentScanner
{
    /// <summary>WSL 里的 home 通过 UNC 共享访问。</summary>
    public const string WslUncPrefix = @"\\wsl$\";

    /// <summary>WSL2 的另一个 UNC 别名，比较路径时归一化成 <see cref="WslUncPrefix"/>。</summary>
    public const string WslLocalhostUncPrefix = @"\\wsl.localhost\";

    /// <summary>TUI bundle（第三方 scope；上游 process.rs 同名常量）。</summary>
    public const string TuiBundle = "@deepseek-harness-tui/dsh-tui";

    private const string ProfilesDirectoryName = "profiles";

    private readonly string? _userProfileRoot;
    private readonly string? _dshHomeEnvironment;
    private readonly Func<CancellationToken, Task<WslDistroListResult>> _listWslDistros;
    private readonly Func<string, string, CancellationToken, Task<string?>> _runInWslDistro;
    private readonly Func<string, string?> _readManifest;

    /// <summary>
    /// 生产用构造：读真实 %USERPROFILE% / DSH_HOME / wsl.exe / 文件系统。
    /// </summary>
    public DshEnvironmentScanner()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("DSH_HOME"),
            WslBridge.ListDistrosAsync,
            WslBridge.RunShellAsync,
            ReadManifestFromDisk)
    {
    }

    /// <summary>可注入构造（harness 用：不依赖真实环境变量、WSL 或磁盘）。</summary>
    public DshEnvironmentScanner(
        string? userProfileRoot,
        string? dshHomeEnvironment,
        Func<CancellationToken, Task<WslDistroListResult>> listWslDistros,
        Func<string, string, CancellationToken, Task<string?>> runInWslDistro,
        Func<string, string?> readManifest)
    {
        _userProfileRoot = userProfileRoot;
        _dshHomeEnvironment = dshHomeEnvironment;
        _listWslDistros = listWslDistros;
        _runInWslDistro = runInWslDistro;
        _readManifest = readManifest;
    }

    public async Task<DshEnvironmentScanResult> ScanAsync(
        IReadOnlyCollection<ManagerInstance> existingInstances,
        CancellationToken cancellationToken = default)
    {
        var homes = new List<ScannedDshHome>();
        var warnings = new List<string>();
        var registered = BuildRegisteredHomeSet(existingInstances);

        ScanLocalHomes(homes, warnings, registered);

        try
        {
            var wsl = await ScanWslHomesAsync(registered, warnings, cancellationToken);
            homes.AddRange(wsl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            warnings.Add($"扫描 WSL 失败：{ex.Message}");
        }

        return new DshEnvironmentScanResult(homes, warnings);
    }

    /// <summary>
    /// 枚举 %USERPROFILE%\.dsh* 目录 + DSH_HOME 环境变量目标（去重；DSH_HOME 命中已有项时只改来源标记）。
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

                homes.Add(BuildHome(directory, DshHomeSource.UserProfile, null, registered));
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
            homes.Add(BuildHome(envHome, DshHomeSource.DshHomeEnvironment, null, registered));
        }
    }

    /// <summary>探测每个已安装 WSL 发行版里的 ~/.dsh*（发行版少，每次一个短进程）。</summary>
    private async Task<IReadOnlyList<ScannedDshHome>> ScanWslHomesAsync(
        IReadOnlySet<string> registered,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = new List<ScannedDshHome>();
        var listing = await _listWslDistros(cancellationToken);
        if (!string.IsNullOrWhiteSpace(listing.Error))
        {
            warnings.Add($"WSL 不可用：{listing.Error}");
            return result;
        }

        foreach (var distro in listing.Distros)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await _runInWslDistro(distro, WslHomeListScript, cancellationToken);
            if (output is null)
            {
                warnings.Add($"无法读取 WSL 发行版 {distro} 的 home（wsl.exe 调用失败）。");
                continue;
            }

            foreach (var (homePath, profileNames) in ParseWslListing(output))
            {
                var uncPath = ToUncPath(distro, homePath);
                var profiles = profileNames
                    .Select(name => new ScannedDshProfile(name, ClassifyProfile(uncPath, name)))
                    .ToArray();
                result.Add(new ScannedDshHome(
                    uncPath,
                    DshHomeSource.Wsl,
                    distro,
                    profiles,
                    registered.Contains(NormalizeForCompare(uncPath))));
            }
        }

        return result;
    }

    private ScannedDshHome BuildHome(
        string homePath,
        DshHomeSource source,
        string? wslDistro,
        IReadOnlySet<string> registered)
    {
        var profiles = EnumerateProfiles(homePath)
            .Select(name => new ScannedDshProfile(name, ClassifyProfile(homePath, name)))
            .ToArray();
        return new ScannedDshHome(
            homePath,
            source,
            wslDistro,
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
            // 不要求源路径当前可访问：WSL home 在发行版停止时不可访问，但"已登记"标记应保留。
            var imported = TryNormalizePath(instance.ImportedFromDshHome);
            if (imported is not null)
            {
                set.Add(NormalizeForCompare(imported));
            }
        }

        return set;
    }

    /// <summary>WSL 侧列 home + profile 的一行一条协议（与上游 scan_wsl_homes 相同）。</summary>
    internal const string WslHomeListScript =
        "for h in \"$HOME\"/.dsh*; do [ -d \"$h\" ] || continue; echo \"H\t$h\"; "
        + "for p in \"$h\"/profiles/*; do [ -d \"$p\" ] || continue; "
        + "n=$(basename \"$p\"); [ \"$n\" = node_modules ] && continue; [ \"$n\" = __temp__ ] && continue; "
        + "echo \"P\t$h\t$n\"; done; done";

    internal static IReadOnlyList<(string Home, IReadOnlyList<string> Profiles)> ParseWslListing(
        string output)
    {
        var homes = new List<(string Home, List<string> Profiles)>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var parts = line.Split('\t');
            if (parts.Length >= 2 && parts[0] == "H")
            {
                var home = parts[1].Trim();
                if (home.Length > 0
                    && !homes.Any(item => string.Equals(item.Home, home, StringComparison.Ordinal)))
                {
                    homes.Add((home, new List<string>()));
                }
            }
            else if (parts.Length >= 3 && parts[0] == "P")
            {
                var home = parts[1].Trim();
                var profile = parts[2].Trim();
                if (profile.Length == 0)
                {
                    continue;
                }

                var entry = homes.FirstOrDefault(item =>
                    string.Equals(item.Home, home, StringComparison.Ordinal));
                if (entry.Home is null)
                {
                    continue;
                }

                if (!entry.Profiles.Contains(profile, StringComparer.Ordinal))
                {
                    entry.Profiles.Add(profile);
                }
            }
        }

        return homes
            .Select(static item => (item.Home, (IReadOnlyList<string>)item.Profiles
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray()))
            .ToArray();
    }

    internal static string ToUncPath(string distro, string linuxPath)
    {
        var relative = linuxPath.Replace('/', '\\').TrimStart('\\');
        return $"{WslUncPrefix}{distro}\\{relative}";
    }

    /// <summary>比较用归一化：大小写、斜杠方向、wsl.localhost 别名、尾部分隔符。</summary>
    internal static string NormalizeForCompare(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        if (normalized.StartsWith(WslLocalhostUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = WslUncPrefix + normalized[WslLocalhostUncPrefix.Length..];
        }

        return normalized
            .TrimEnd('\\')
            .ToLowerInvariant();
    }

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

    /// <summary>wsl.exe 调用封装（生产实现；测试可整段替换）。</summary>
    private static class WslBridge
    {
        public static async Task<WslDistroListResult> ListDistrosAsync(CancellationToken cancellationToken)
        {
            var result = await RunAsync(["-l", "-q"], Encoding.Unicode, cancellationToken);
            if (result is null)
            {
                return new WslDistroListResult(Array.Empty<string>(), "无法启动 wsl.exe。");
            }

            if (result.Value.ExitCode != 0)
            {
                var error = FirstNonEmptyLine(result.Value.Error) ?? FirstNonEmptyLine(result.Value.Output);
                return new WslDistroListResult(
                    Array.Empty<string>(),
                    string.IsNullOrWhiteSpace(error) ? $"wsl.exe 退出码 {result.Value.ExitCode}。" : error);
            }

            var distros = result.Value.Output
                .Split('\n')
                .Select(static line => line.Trim('\r', ' ', '\0'))
                .Where(static line => line.Length > 0)
                .ToArray();
            return new WslDistroListResult(distros, null);
        }

        public static async Task<string?> RunShellAsync(
            string distro,
            string script,
            CancellationToken cancellationToken)
        {
            var result = await RunAsync(
                ["-d", distro, "--", "sh", "-c", script],
                Encoding.UTF8,
                cancellationToken);
            return result is { ExitCode: 0 } ? result.Value.Output : null;
        }

        private static async Task<(int ExitCode, string Output, string Error)?> RunAsync(
            string[] arguments,
            Encoding outputEncoding,
            CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = outputEncoding,
                    StandardErrorEncoding = outputEncoding
                };
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                return (process.ExitCode, await outputTask, await errorTask);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or IOException)
            {
                return null;
            }
        }

        private static string? FirstNonEmptyLine(string text) =>
            text.Split('\n')
                .Select(static line => line.Trim('\r', ' ', '\0'))
                .FirstOrDefault(static line => line.Length > 0);
    }
}
