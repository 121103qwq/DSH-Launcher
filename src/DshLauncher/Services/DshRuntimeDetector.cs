using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DshLauncher.Models;

namespace DshLauncher.Services;

public sealed class DshRuntimeDetector
{
    private const int MaximumConcurrentChecks = 4;
    private const int MaximumConcurrentPrefixChecks = 2;
    private const int MaximumManualDirectories = 20_000;
    private const int MaximumManualDepth = 6;
    private static readonly TimeSpan DirectCandidateTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ElectronCandidateTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PrefixTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly Regex ReportedVersion = new(
        @"(?<![0-9A-Za-z])v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)(?![0-9A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LauncherPaths _paths;

    public DshRuntimeDetector(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    public Task<DshRuntimeInfo> DetectAsync(CancellationToken cancellationToken = default) =>
        DetectAsync(preferredInstallDirectory: null, cancellationToken);

    public async Task<DshRuntimeInfo> DetectAsync(
        string? preferredInstallDirectory,
        CancellationToken cancellationToken = default)
    {
        // A caller that supplies a directory is explicitly asking us to inspect
        // that location now. Do not let a machine-wide cache hide a newly copied
        // or updated runtime in the selected directory.
        var scan = await ScanAsync(
            preferredInstallDirectory,
            forceRefresh: !string.IsNullOrWhiteSpace(preferredInstallDirectory),
            cancellationToken);
        return scan.PrimaryRuntime;
    }

    internal async Task<DshRuntimeInfo> DetectAsync(
        string? preferredInstallDirectory,
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations,
        CancellationToken cancellationToken = default)
    {
        var scan = await ScanFreshAsync(
            preferredInstallDirectory,
            desktopInstallations,
            includePathCandidates: true,
            includeManagedLocations: false,
            additionalPackageRoots: null,
            progress: null,
            cancellationToken);
        return scan.PrimaryRuntime;
    }

    public async Task<IReadOnlyList<DshRuntimeInfo>> DetectAllAsync(
        string? preferredInstallDirectory,
        CancellationToken cancellationToken = default)
    {
        var scan = await ScanAsync(preferredInstallDirectory, forceRefresh: false, cancellationToken);
        return scan.Runtimes;
    }

    internal Task<DshRuntimeScanResult> ScanAsync(
        string? preferredInstallDirectory,
        CancellationToken cancellationToken = default) =>
        ScanAsync(preferredInstallDirectory, forceRefresh: false, cancellationToken);

    internal async Task<DshRuntimeScanResult> ScanAsync(
        string? preferredInstallDirectory,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryReadCache(out var cached))
        {
            return cached;
        }

        var result = await ScanFreshAsync(
            preferredInstallDirectory,
            DeepSeekDesktopDetector.DetectInstallations(),
            includePathCandidates: true,
            includeManagedLocations: true,
            additionalPackageRoots: null,
            progress: null,
            cancellationToken);
        TryWriteCache(result);
        return result;
    }

    internal Task<DshRuntimeScanResult> ScanAsync(
        string? preferredInstallDirectory,
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations,
        CancellationToken cancellationToken = default) =>
        ScanFreshAsync(
            preferredInstallDirectory,
            desktopInstallations,
            includePathCandidates: true,
            includeManagedLocations: false,
            additionalPackageRoots: null,
            progress: null,
            cancellationToken);

    public async Task<DshRuntimeScanResult> ScanDirectoryAsync(
        string directory,
        IProgress<DshRuntimeScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = TryNormalizeDirectory(directory)
            ?? throw new DirectoryNotFoundException("所选目录不存在或路径无效。");
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"所选目录不存在：{normalized}");
        }

        var packageRoots = await Task.Run(
            () => FindPackageRoots(normalized, progress, cancellationToken),
            cancellationToken);
        var desktops = new List<DeepSeekDesktopInstallation>();
        AddDesktopInstallation(desktops, DeepSeekDesktopDetector.TryDetect(normalized));
        foreach (var packageRoot in packageRoots)
        {
            AddDesktopInstallation(desktops, TryFindDesktopForPackageRoot(packageRoot));
        }

        foreach (var installation in DeepSeekDesktopDetector.DetectInstallations()
                     .Where(item => IsInsideDirectory(item.InstallRoot, normalized)))
        {
            AddDesktopInstallation(desktops, installation);
        }

        return await ScanFreshAsync(
            normalized,
            desktops,
            includePathCandidates: false,
            includeManagedLocations: false,
            additionalPackageRoots: packageRoots,
            progress,
            cancellationToken);
    }

    private static DeepSeekDesktopInstallation? TryFindDesktopForPackageRoot(string packageRoot)
    {
        DirectoryInfo? current = new(packageRoot);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var installation = DeepSeekDesktopDetector.TryDetect(current.FullName);
            if (installation is not null
                && string.Equals(
                    Path.GetFullPath(installation.DshPackageRoot),
                    Path.GetFullPath(packageRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return installation;
            }
        }

        return null;
    }

    private static void AddDesktopInstallation(
        ICollection<DeepSeekDesktopInstallation> installations,
        DeepSeekDesktopInstallation? installation)
    {
        if (installation is null
            || installations.Any(item => string.Equals(
                item.InstallRoot,
                installation.InstallRoot,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        installations.Add(installation);
    }

    private async Task<DshRuntimeScanResult> ScanFreshAsync(
        string? preferredInstallDirectory,
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations,
        bool includePathCandidates,
        bool includeManagedLocations,
        IReadOnlyCollection<string>? additionalPackageRoots,
        IProgress<DshRuntimeScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = new List<DshRuntimeCandidate>();
        var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installation in desktopInstallations)
        {
            var spec = installation.LaunchSpec
                ?? new DshRuntimeLaunchSpec(
                    DshRuntimeLaunchMode.DirectCommand,
                    installation.DshExecutablePath,
                    NodeExecutablePath: installation.NodeExecutablePath,
                    ProductName: installation.ProductName,
                    ProductVersion: installation.DesktopVersion);
            AddCandidate(candidates, seenCandidates, installation.DshPackageRoot, spec);
        }

        foreach (var command in GetCandidates(
                     preferredInstallDirectory,
                     desktopInstallations,
                     includePathCandidates ? null : Array.Empty<string>()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(command))
            {
                continue;
            }

            var packageRoot = TryFindPackageRoot(command);
            if (packageRoot is not null)
            {
                AddCandidate(
                    candidates,
                    seenCandidates,
                    packageRoot,
                    new DshRuntimeLaunchSpec(DshRuntimeLaunchMode.DirectCommand, command));
            }
        }

        var packageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in additionalPackageRoots ?? Array.Empty<string>())
        {
            if (IsDshPackageRoot(root))
            {
                packageRoots.Add(Path.GetFullPath(root));
            }
        }

        if (includeManagedLocations)
        {
            foreach (var root in await FindNpmGlobalPackageRootsAsync(cancellationToken))
            {
                packageRoots.Add(root);
            }
        }

        var preferred = TryNormalizeDirectory(preferredInstallDirectory);
        if (preferred is not null)
        {
            foreach (var root in FindKnownPackageRoots(preferred))
            {
                packageRoots.Add(root);
            }
        }

        foreach (var packageRoot in packageRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spec = CreateLaunchSpecForPackageRoot(packageRoot);
            if (spec is not null)
            {
                AddCandidate(candidates, seenCandidates, packageRoot, spec);
            }
        }

        progress?.Report(new DshRuntimeScanProgress(0, candidates.Count, "正在验证 DSh 运行环境…"));
        using var gate = new SemaphoreSlim(MaximumConcurrentChecks);
        var completed = 0;
        var checks = candidates.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var runtime = await ValidateCandidateAsync(candidate, cancellationToken);
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new DshRuntimeScanProgress(done, candidates.Count, $"已检查 {done} / {candidates.Count}"));
                return runtime;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        var runtimes = (await Task.WhenAll(checks))
            .Where(static runtime => runtime is not null)
            .Cast<DshRuntimeInfo>()
            .GroupBy(static runtime => Path.GetFullPath(runtime.PackageRoot!), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(item =>
                item.EffectiveLaunchSpec?.Mode == DshRuntimeLaunchMode.ElectronBootstrap).First())
            .OrderByDescending(runtime => preferred is not null
                && runtime.PackageRoot is not null
                && IsInsideDirectory(runtime.PackageRoot, preferred))
            .ThenByDescending(static runtime => runtime.EffectiveLaunchSpec?.UsesPackagedNode == true)
            .ThenBy(static runtime => runtime.SuggestedInstanceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DshRuntimeScanResult(runtimes, candidates.Count > 0);
    }

    private static void AddCandidate(
        ICollection<DshRuntimeCandidate> candidates,
        ISet<string> seen,
        string packageRoot,
        DshRuntimeLaunchSpec spec)
    {
        if (!IsDshPackageRoot(packageRoot))
        {
            return;
        }

        var root = Path.GetFullPath(packageRoot);
        var key = $"{root}|{spec.Mode}|{Path.GetFullPath(spec.HostPath)}|{spec.EntryPointPath}";
        if (seen.Add(key))
        {
            candidates.Add(new DshRuntimeCandidate(root, spec));
        }
    }

    private static async Task<DshRuntimeInfo?> ValidateCandidateAsync(
        DshRuntimeCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!DshRuntimeCommandFactory.IsUsable(candidate.LaunchSpec))
        {
            return null;
        }

        var packageVersion = TryReadPackageVersion(candidate.PackageRoot);
        if (string.IsNullOrWhiteSpace(packageVersion))
        {
            return null;
        }

        var reportedVersion = await ReadVersionAsync(candidate.LaunchSpec, candidate.PackageRoot, cancellationToken);
        if (!string.Equals(reportedVersion, packageVersion, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var legacyDesktopVersion = string.Equals(
            candidate.LaunchSpec.ProductName,
            "DeepSeek Desktop",
            StringComparison.OrdinalIgnoreCase)
                ? candidate.LaunchSpec.ProductVersion
                : null;
        return new DshRuntimeInfo(
            true,
            candidate.LaunchSpec.HostPath,
            packageVersion,
            candidate.PackageRoot,
            null,
            TryReadNodeEngine(candidate.PackageRoot),
            legacyDesktopVersion,
            candidate.LaunchSpec.NodeExecutablePath,
            candidate.LaunchSpec);
    }

    private static async Task<string?> ReadVersionAsync(
        DshRuntimeLaunchSpec spec,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = DshRuntimeCommandFactory.Create(spec, new[] { "--version" }, workingDirectory)
        };
        var timeout = spec.Mode == DshRuntimeLaunchMode.ElectronBootstrap
            ? ElectronCandidateTimeout
            : DirectCandidateTimeout;
        try
        {
            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var completion = Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask);
            if (await Task.WhenAny(completion, Task.Delay(timeout, cancellationToken)) != completion)
            {
                TryKill(process);
                await WaitForExitSafelyAsync(completion);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            await completion;
            if (process.ExitCode != 0)
            {
                return null;
            }

            var match = ReportedVersion.Match(outputTask.Result.Trim());
            return match.Success ? match.Groups["version"].Value : null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            throw;
        }
        catch
        {
            TryKill(process);
            await WaitForExitSafelyAsync(process);
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> FindNpmGlobalPackageRootsAsync(
        CancellationToken cancellationToken)
    {
        var nodeDirectories = NodeRuntimeDetector.GetCandidates()
            .Where(File.Exists)
            .Select(Path.GetDirectoryName)
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        using var gate = new SemaphoreSlim(MaximumConcurrentPrefixChecks);
        var checks = nodeDirectories.Select(async directory =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await TryReadNpmPrefixAsync(directory, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });
        var prefixes = (await Task.WhenAll(checks))
            .Where(static prefix => !string.IsNullOrWhiteSpace(prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        return prefixes
            .Select(prefix => Path.Combine(prefix, "node_modules", "@deepseek-ai", "dsh"))
            .Where(IsDshPackageRoot)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<string?> TryReadNpmPrefixAsync(
        string nodeDirectory,
        CancellationToken cancellationToken)
    {
        var npm = new[] { "npm.cmd", "npm.exe", "npm" }
            .Select(name => Path.Combine(nodeDirectory, name))
            .FirstOrDefault(File.Exists);
        if (npm is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (Path.GetExtension(npm).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{npm}\" prefix -g\"";
        }
        else
        {
            startInfo.FileName = npm;
            startInfo.ArgumentList.Add("prefix");
            startInfo.ArgumentList.Add("-g");
        }

        startInfo.Environment["PATH"] = RuntimeSearchPaths.BuildCurrentPath(Path.Combine(nodeDirectory, "node.exe"));
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var completion = Task.WhenAll(process.WaitForExitAsync(), output, error);
            if (await Task.WhenAny(completion, Task.Delay(PrefixTimeout, cancellationToken)) != completion)
            {
                TryKill(process);
                await WaitForExitSafelyAsync(completion);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            await completion;
            if (process.ExitCode != 0)
            {
                return null;
            }

            var prefix = output.Result
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim();
            return TryNormalizeDirectory(prefix);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            return null;
        }
    }

    public static string? TryFindPackageRoot(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var directory = Directory.Exists(executablePath)
            ? Path.GetFullPath(executablePath)
            : Path.GetDirectoryName(Path.GetFullPath(executablePath));
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            if (IsDshPackageRoot(directory))
            {
                return directory;
            }

            var nested = Path.Combine(directory, "node_modules", "@deepseek-ai", "dsh");
            if (IsDshPackageRoot(nested))
            {
                return nested;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    public static string? TryResolvePackageRoot(string directory)
    {
        var normalized = TryNormalizeDirectory(directory);
        if (normalized is null)
        {
            return null;
        }

        if (IsDshPackageRoot(normalized))
        {
            return normalized;
        }

        foreach (var candidate in FindKnownPackageRoots(normalized))
        {
            return candidate;
        }

        return DeepSeekDesktopDetector.TryDetect(normalized)?.DshPackageRoot;
    }

    public static string? TryReadPackageVersion(string packageRoot) =>
        TryReadPackageString(packageRoot, static root =>
            root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null);

    public static string? TryReadNodeEngine(string packageRoot) =>
        TryReadPackageString(packageRoot, static root =>
            root.TryGetProperty("engines", out var engines)
            && engines.ValueKind == JsonValueKind.Object
            && engines.TryGetProperty("node", out var node)
            && node.ValueKind == JsonValueKind.String
                ? node.GetString()
                : null);

    private static string? TryReadPackageString(
        string packageRoot,
        Func<JsonElement, string?> reader)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return null;
        }

        var normalized = Path.GetFullPath(packageRoot.Trim());
        if (!IsDshPackageRoot(normalized))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(normalized, "package.json"), Encoding.UTF8));
            return reader(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ResolveNodeEngine(ManagerInstance? instance, string? detectedNodeEngine)
    {
        if (instance?.Kind == InstanceKind.Source)
        {
            return SourceProjectInspector.TryReadNodeEngine(instance.RootPath);
        }

        if (instance?.Kind == InstanceKind.Installed)
        {
            var packageRoot = TryResolvePackageRoot(instance.RootPath);
            return packageRoot is null ? detectedNodeEngine : TryReadNodeEngine(packageRoot);
        }

        return detectedNodeEngine;
    }

    public static string? FindExecutableForPackageRoot(string packageRoot) =>
        FindDirectCommand(packageRoot);

    public static DshRuntimeLaunchSpec? CreateLaunchSpecForPackageRoot(
        string packageRoot,
        string? preferredNodeExecutablePath = null)
    {
        if (!IsDshPackageRoot(packageRoot))
        {
            return null;
        }

        var direct = FindDirectCommand(packageRoot);
        if (direct is not null)
        {
            return new DshRuntimeLaunchSpec(DshRuntimeLaunchMode.DirectCommand, direct);
        }

        var entry = TryReadBinEntry(packageRoot);
        var node = FindNodeForPackage(packageRoot, preferredNodeExecutablePath);
        return entry is null || node is null
            ? null
            : new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.NodeScript,
                node,
                entry,
                NodeExecutablePath: node);
    }

    private static string? FindDirectCommand(string packageRoot)
    {
        var normalized = Path.GetFullPath(packageRoot.Trim());
        var scope = Directory.GetParent(normalized)?.FullName;
        var nodeModules = scope is null ? null : Directory.GetParent(scope)?.FullName;
        if (nodeModules is null)
        {
            return null;
        }

        var prefix = Directory.GetParent(nodeModules)?.FullName;
        foreach (var directory in new[] { Path.Combine(nodeModules, ".bin"), prefix, nodeModules })
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var name in new[] { "dsh.cmd", "dsh.exe", "dsh" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string? TryReadBinEntry(string packageRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json"), Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("bin", out var bin))
            {
                return null;
            }

            var relative = bin.ValueKind switch
            {
                JsonValueKind.String => bin.GetString(),
                JsonValueKind.Object when bin.TryGetProperty("dsh", out var dsh) && dsh.ValueKind == JsonValueKind.String => dsh.GetString(),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            var path = Path.GetFullPath(Path.Combine(packageRoot, relative));
            return File.Exists(path) && IsInsideDirectory(path, packageRoot) ? path : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FindNodeForPackage(string packageRoot, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
        {
            return Path.GetFullPath(preferred);
        }

        var current = packageRoot;
        for (var depth = 0; depth < 7; depth++)
        {
            var node = Path.Combine(current, "node.exe");
            if (File.Exists(node))
            {
                return Path.GetFullPath(node);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            current = parent;
        }

        return NodeRuntimeDetector.GetCandidates().FirstOrDefault(File.Exists);
    }

    public static IEnumerable<string> GetCandidates() =>
        GetCandidates(preferredInstallDirectory: null);

    public static IEnumerable<string> GetCandidates(string? preferredInstallDirectory) =>
        GetCandidates(preferredInstallDirectory, DeepSeekDesktopDetector.DetectInstallations());

    internal static IEnumerable<string> GetCandidates(
        string? preferredInstallDirectory,
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations,
        IReadOnlyList<string>? pathDirectories = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        var preferred = TryNormalizeDirectory(preferredInstallDirectory);
        if (preferred is not null)
        {
            directories.Add(preferred);
        }

        directories.AddRange(pathDirectories ?? RuntimeSearchPaths.GetCurrentDirectories());
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var directory in directories)
        {
            foreach (var name in new[] { "dsh.cmd", "dsh.exe", "dsh" })
            {
                var candidate = Path.Combine(directory, name);
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var installation in desktopInstallations)
        {
            if (seen.Add(installation.DshExecutablePath))
            {
                yield return installation.DshExecutablePath;
            }
        }
    }

    public static bool IsExecutableInInstallDirectory(string? executablePath, string? installDirectory)
    {
        var directory = TryNormalizeDirectory(installDirectory);
        return !string.IsNullOrWhiteSpace(executablePath)
            && directory is not null
            && IsInsideDirectory(executablePath, directory);
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string? bundledNodeExecutablePath = null) =>
        DshRuntimeCommandFactory.Create(
            new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.DirectCommand,
                executablePath,
                NodeExecutablePath: bundledNodeExecutablePath),
            new[] { "--version" },
            Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory);

    private static IReadOnlyList<string> FindKnownPackageRoots(string root)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in new[]
        {
            string.Empty,
            Path.Combine("node_modules", "@deepseek-ai", "dsh"),
            Path.Combine("app", "node_modules", "@deepseek-ai", "dsh"),
            Path.Combine("resources", "app.asar.unpacked", "node_modules", "@deepseek-ai", "dsh")
        })
        {
            var candidate = string.IsNullOrEmpty(relative) ? root : Path.Combine(root, relative);
            if (IsDshPackageRoot(candidate))
            {
                results.Add(Path.GetFullPath(candidate));
            }
        }

        return results.ToArray();
    }

    private static IReadOnlyList<string> FindPackageRoots(
        string root,
        IProgress<DshRuntimeScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new HashSet<string>(FindKnownPackageRoots(root), StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < MaximumManualDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Dequeue();
            visited++;
            if ((visited & 127) == 0)
            {
                progress?.Report(new DshRuntimeScanProgress(visited, MaximumManualDirectories, $"正在扫描目录：{visited:N0}"));
            }

            if (IsDshPackageRoot(directory))
            {
                results.Add(Path.GetFullPath(directory));
                continue;
            }

            if (depth >= MaximumManualDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!IsReparsePoint(child) && !ShouldSkipDirectory(child))
                    {
                        pending.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One inaccessible subtree must not abort a user-selected scan.
            }
        }

        return results.ToArray();
    }

    private static bool ShouldSkipDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cache", StringComparison.OrdinalIgnoreCase)
            || name.Equals("caches", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tmp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("temp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDshPackageRoot(string directory)
    {
        var package = Path.Combine(directory, "package.json");
        if (!File.Exists(package) || IsReparsePoint(package))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(package, Encoding.UTF8));
            return document.RootElement.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), "@deepseek-ai/dsh", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryReadCache(out DshRuntimeScanResult result)
    {
        result = new DshRuntimeScanResult(Array.Empty<DshRuntimeInfo>(), false);
        try
        {
            if (!File.Exists(_paths.RuntimeCachePath))
            {
                return false;
            }

            var cache = JsonSerializer.Deserialize<DshRuntimeCacheDocument>(
                File.ReadAllText(_paths.RuntimeCachePath, Encoding.UTF8),
                CacheJsonOptions);
            if (cache is null
                || DateTimeOffset.UtcNow - cache.SavedAt > CacheLifetime
                || cache.Runtimes.Count == 0)
            {
                return false;
            }

            var runtimes = new List<DshRuntimeInfo>();
            foreach (var entry in cache.Runtimes)
            {
                var runtime = entry.Runtime;
                if (!runtime.IsAvailable
                    || string.IsNullOrWhiteSpace(runtime.PackageRoot)
                    || !IsDshPackageRoot(runtime.PackageRoot)
                    || !DshRuntimeCommandFactory.IsUsable(runtime.EffectiveLaunchSpec)
                    || !entry.Fingerprints.All(static fingerprint => fingerprint.IsCurrent()))
                {
                    return false;
                }

                runtimes.Add(runtime);
            }

            result = new DshRuntimeScanResult(runtimes, true, FromCache: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private void TryWriteCache(DshRuntimeScanResult result)
    {
        if (result.Runtimes.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            var cache = new DshRuntimeCacheDocument
            {
                SavedAt = DateTimeOffset.UtcNow,
                Runtimes = result.Runtimes.Select(runtime => new DshRuntimeCacheEntry
                {
                    Runtime = runtime,
                    Fingerprints = GetFingerprintPaths(runtime)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(File.Exists)
                        .Select(DshRuntimeFileFingerprint.Create)
                        .ToList()
                }).ToList()
            };
            var temporary = _paths.RuntimeCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(cache, CacheJsonOptions), new UTF8Encoding(false));
                File.Move(temporary, _paths.RuntimeCachePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Runtime discovery still succeeds when its optional cache cannot be written.
        }
    }

    private static IEnumerable<string> GetFingerprintPaths(DshRuntimeInfo runtime)
    {
        if (!string.IsNullOrWhiteSpace(runtime.PackageRoot))
        {
            yield return Path.Combine(runtime.PackageRoot, "package.json");
        }

        var spec = runtime.EffectiveLaunchSpec;
        if (spec is null)
        {
            yield break;
        }

        yield return spec.HostPath;
        if (!string.IsNullOrWhiteSpace(spec.EntryPointPath)) yield return spec.EntryPointPath;
        if (!string.IsNullOrWhiteSpace(spec.PnpmScriptPath)) yield return spec.PnpmScriptPath;
    }

    private static string? TryNormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, candidate);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task WaitForExitSafelyAsync(Process process)
    {
        try { await process.WaitForExitAsync(); } catch { }
    }

    private static async Task WaitForExitSafelyAsync(Task task)
    {
        try { await task; } catch { }
    }

    private sealed record DshRuntimeCandidate(
        string PackageRoot,
        DshRuntimeLaunchSpec LaunchSpec);

    private sealed class DshRuntimeCacheDocument
    {
        public DateTimeOffset SavedAt { get; set; }
        public List<DshRuntimeCacheEntry> Runtimes { get; set; } = new();
    }

    private sealed class DshRuntimeCacheEntry
    {
        public DshRuntimeInfo Runtime { get; set; } = DshRuntimeInfo.Missing();
        public List<DshRuntimeFileFingerprint> Fingerprints { get; set; } = new();
    }

    private sealed class DshRuntimeFileFingerprint
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }

        public static DshRuntimeFileFingerprint Create(string path)
        {
            var file = new FileInfo(path);
            return new DshRuntimeFileFingerprint
            {
                Path = file.FullName,
                Length = file.Length,
                LastWriteTimeUtc = file.LastWriteTimeUtc
            };
        }

        public bool IsCurrent()
        {
            try
            {
                var file = new FileInfo(Path);
                return file.Exists && file.Length == Length && file.LastWriteTimeUtc == LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}

public sealed record DshRuntimeScanProgress(int Completed, int Total, string Message);

public sealed record DshRuntimeScanResult(
    IReadOnlyList<DshRuntimeInfo> Runtimes,
    bool FoundCandidate,
    bool FromCache = false)
{
    public DshRuntimeInfo PrimaryRuntime => Runtimes.FirstOrDefault()
        ?? DshRuntimeInfo.Missing(FoundCandidate
            ? "找到了 DSh 文件，但安装包无法解析、启动方式不能运行，或命令版本与 package.json 不一致。"
            : "常见安装位置中没有找到可运行的 DeepSeek Harness；可以选择目录进行手动扫描。");
}
