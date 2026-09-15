using System.IO;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Discovers existing DSH_HOME directories without changing them or registering
/// anything. The caller supplies the runtimes and instances it already knows;
/// standard Desktop locations are refreshed here because a cached runtime list
/// can otherwise miss a newly installed Desktop.
/// </summary>
public sealed class ExistingDshHomeDiscoveryService
{
    private readonly LauncherPaths _paths;
    private readonly string? _userProfileDirectory;

    public ExistingDshHomeDiscoveryService(
        LauncherPaths? paths = null,
        string? userProfileDirectory = null)
    {
        _paths = paths ?? new LauncherPaths();
        _userProfileDirectory = NormalizePath(userProfileDirectory);
    }

    public Task<IReadOnlyList<ExistingDshHomeCandidate>> DiscoverAsync(
        IEnumerable<DshRuntimeInfo> runtimes,
        IEnumerable<ManagerInstance> instances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(instances);

        // Directory and registry reads are synchronous APIs. Keep the discovery
        // off the WPF thread while retaining an awaitable, cancellable boundary.
        var runtimeSnapshot = runtimes.ToArray();
        var instanceSnapshot = instances.ToArray();
        return Task.Run(
            () => Discover(runtimeSnapshot, instanceSnapshot, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<ExistingDshHomeCandidate> Discover(
        IReadOnlyList<DshRuntimeInfo> runtimes,
        IReadOnlyList<ManagerInstance> instances,
        CancellationToken cancellationToken)
    {
        var builders = new Dictionary<string, CandidateBuilder>(StringComparer.OrdinalIgnoreCase);
        var installationRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddExclusionRoot(installationRoots, _paths.RootDirectory);
        // Reuse the detector's bounded list of standard and registered Desktop
        // install roots, including roots whose installation is currently
        // incomplete and therefore cannot produce a DeepSeekDesktopInstallation.
        var desktopRoots = DeepSeekDesktopDetector.GetInstallRootCandidates().ToArray();
        foreach (var root in desktopRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExclusionRoot(installationRoots, root);
        }
        foreach (var runtime in runtimes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExclusionRoot(installationRoots, runtime.PackageRoot);
        }

        var availableRuntimes = runtimes
            .Where(static runtime => runtime.IsAvailable)
            .ToArray();
        var runtimesByRoot = BuildRuntimeIndex(availableRuntimes);

        // The explicit DSH_HOME variable is a useful source in its own right.
        // Keep ~/.dsh as a separate candidate too, so changing the variable does
        // not hide the user's ordinary DSh home.
        var configuredHome = ResolveConfiguredHome();
        if (configuredHome is not null)
        {
            AddCandidate(
                builders,
                configuredHome,
                "当前 DSH_HOME 环境变量",
                "当前 DSH_HOME",
                runtime: null,
                nameRank: 60,
                installationRoots,
                allowRegisteredHome: false);
        }

        var defaultHome = ResolveDefaultHome();
        if (defaultHome is not null)
        {
            AddCandidate(
                builders,
                defaultHome,
                "默认 ~/.dsh",
                "默认 ~/.dsh",
                runtime: null,
                nameRank: 50,
                installationRoots,
                allowRegisteredHome: false);
        }

        foreach (var runtime in availableRuntimes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCandidate(
                builders,
                runtime.ExistingDshHome,
                $"DSh Runtime：{runtime.DisplayVersionText}",
                runtime.SuggestedInstanceName,
                runtime,
                nameRank: 80,
                installationRoots,
                allowRegisteredHome: false);
        }

        // DetectInstallations only checks known Desktop install locations and
        // uninstall metadata; it does not execute the Desktop or DSh process.
        IReadOnlyList<DeepSeekDesktopInstallation> desktopInstallations;
        try
        {
            desktopInstallations = DeepSeekDesktopDetector.DetectInstallations(desktopRoots);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            desktopInstallations = Array.Empty<DeepSeekDesktopInstallation>();
        }

        foreach (var installation in desktopInstallations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExclusionRoot(installationRoots, installation.InstallRoot);
            AddExclusionRoot(installationRoots, installation.DshPackageRoot);
        }

        foreach (var installation in desktopInstallations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? home;
            try
            {
                home = DeepSeekDesktopDetector.TryResolveDshHome(installation);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                home = null;
            }

            var runtime = TryFindRuntime(installation.DshPackageRoot, runtimesByRoot);
            var desktopName = installation.ProductName
                + (string.IsNullOrWhiteSpace(installation.DesktopVersion)
                    ? string.Empty
                    : $" {installation.DesktopVersion}");
            AddCandidate(
                builders,
                home,
                $"{desktopName} 已检测",
                desktopName,
                runtime,
                nameRank: 70,
                installationRoots,
                allowRegisteredHome: false);
        }

        // A running desktop may use an explicitly selected HOME that differs
        // from its install metadata and from this Launcher's environment.
        try
        {
            foreach (var owner in ExternalDshHomeGuard.ReadDesktopOwners(null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddCandidate(
                    builders,
                    owner.DshHome,
                    $"运行中 {owner.ProductName}（PID {owner.ProcessId}）",
                    owner.ProductName,
                    runtime: null,
                    nameRank: 75,
                    installationRoots,
                    allowRegisteredHome: false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            // Discovery remains useful when a process cannot be inspected.
            // Launch/edit checks separately fail closed for unknown owners.
        }

        // Registered external homes are trusted as explicit user choices and
        // remain visible even when their runtime directory is a conventional
        // install location. The Launcher-owned tree is always excluded.
        foreach (var instance in instances.Where(static item => item.UsesExternalDshHome))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = TryFindRuntime(instance.RootPath, runtimesByRoot);
            AddCandidate(
                builders,
                instance.DshHome,
                "已登记的外部 DSH_HOME",
                instance.Name,
                runtime,
                nameRank: 100,
                installationRoots,
                allowRegisteredHome: true,
                isRegistered: true);
        }

        return builders.Values
            .Select(static builder => builder.ToCandidate())
            .ToArray();
    }

    private void AddCandidate(
        IDictionary<string, CandidateBuilder> builders,
        string? home,
        string source,
        string name,
        DshRuntimeInfo? runtime,
        int nameRank,
        ISet<string> installationRoots,
        bool allowRegisteredHome,
        bool isRegistered = false)
    {
        var normalized = TryNormalizeDshHome(home);
        if (normalized is null
            || IsLauncherPath(normalized)
            || (!allowRegisteredHome && IsInsideAny(normalized, installationRoots)))
        {
            return;
        }

        if (!builders.TryGetValue(normalized, out var builder))
        {
            builder = new CandidateBuilder(normalized);
            builders.Add(normalized, builder);
        }

        builder.Add(source, name, nameRank, runtime, isRegistered);
    }

    private Dictionary<string, DshRuntimeInfo> BuildRuntimeIndex(
        IEnumerable<DshRuntimeInfo> runtimes)
    {
        var result = new Dictionary<string, DshRuntimeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in runtimes)
        {
            var root = NormalizePath(runtime.PackageRoot);
            if (root is not null && !result.ContainsKey(root))
            {
                result[root] = runtime;
            }
        }

        return result;
    }

    private static DshRuntimeInfo? TryFindRuntime(
        string? root,
        IReadOnlyDictionary<string, DshRuntimeInfo> runtimesByRoot)
    {
        var normalized = NormalizePath(root);
        if (normalized is null)
        {
            return null;
        }

        return runtimesByRoot.TryGetValue(normalized, out var runtime) ? runtime : null;
    }

    private string? ResolveConfiguredHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var value = configured.Trim().Trim('"');
        if (value == "~")
        {
            value = UserProfileDirectory() ?? value;
        }
        else if (value.StartsWith("~/", StringComparison.Ordinal)
            || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            var profile = UserProfileDirectory();
            value = profile is null ? value : Path.Combine(profile, value[2..]);
        }

        return TryNormalizeDshHome(value);
    }

    private string? ResolveDefaultHome()
    {
        var profile = UserProfileDirectory();
        return profile is null
            ? null
            : TryNormalizeDshHome(Path.Combine(profile, ".dsh"));
    }

    private string? UserProfileDirectory() =>
        _userProfileDirectory
        ?? NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private string? TryNormalizeDshHome(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null
            || !Directory.Exists(normalized)
            || IsReparsePoint(normalized))
        {
            return null;
        }

        return HasDshHomeStructure(normalized) ? normalized : null;
    }

    private static bool HasDshHomeStructure(string home) =>
        IsRegularFile(Path.Combine(home, "settings.yaml"))
        || IsRegularFile(Path.Combine(home, ".credentials.yaml"))
        || IsRegularDirectory(Path.Combine(home, "profiles"))
        || IsRegularDirectory(Path.Combine(home, "sessions"))
        || IsRegularDirectory(Path.Combine(home, "storages"))
        || IsRegularDirectory(Path.Combine(home, "skills"))
        || IsRegularDirectory(Path.Combine(home, ".agents"));

    private static bool IsRegularFile(string path) =>
        File.Exists(path) && !IsReparsePoint(path);

    private static bool IsRegularDirectory(string path) =>
        Directory.Exists(path) && !IsReparsePoint(path);

    private bool IsLauncherPath(string path) =>
        PathsOverlap(path, _paths.RootDirectory);

    private static void AddExclusionRoot(ISet<string> roots, string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is not null)
        {
            roots.Add(normalized);
        }
    }

    private static bool IsInsideAny(string path, IEnumerable<string> roots) =>
        roots.Any(root => PathsOverlap(path, root));

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || IsInside(left, right)
        || IsInside(right, left);

    private static bool IsInside(string path, string parent)
    {
        try
        {
            var relative = Path.GetRelativePath(parent, path);
            return !Path.IsPathRooted(relative)
                && relative != "."
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            var full = Path.GetFullPath(expanded);
            var root = Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class CandidateBuilder
    {
        private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
        private int _nameRank = int.MinValue;

        public CandidateBuilder(string home)
        {
            DshHome = home;
            Name = new DirectoryInfo(home).Name;
        }

        public string Name { get; private set; }

        public string DshHome { get; }

        public DshRuntimeInfo? Runtime { get; private set; }

        public bool IsRegistered { get; private set; }

        public void Add(
            string source,
            string name,
            int nameRank,
            DshRuntimeInfo? runtime,
            bool isRegistered)
        {
            if (!string.IsNullOrWhiteSpace(source))
            {
                _sources.Add(source.Trim());
            }

            if (!string.IsNullOrWhiteSpace(name) && nameRank >= _nameRank)
            {
                Name = name.Trim();
                _nameRank = nameRank;
            }

            Runtime ??= runtime;
            IsRegistered |= isRegistered;
        }

        public ExistingDshHomeCandidate ToCandidate() =>
            new(Name, DshHome, string.Join("；", _sources), Runtime, IsRegistered);
    }
}
