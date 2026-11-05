using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshLauncher.Models;
using Microsoft.Win32;

namespace DshLauncher.Services;

public static class DeepSeekDesktopDetector
{
    private static readonly Regex VersionFilePattern = new(
        @"(?im)^(?:DeepSeek|DSH) Desktop\s+v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<DeepSeekDesktopInstallation> DetectInstallations() =>
        DetectInstallations(GetInstallRootCandidates());

    internal static IReadOnlyList<DeepSeekDesktopInstallation> DetectInstallations(IEnumerable<string> roots)
    {
        var installations = new List<DeepSeekDesktopInstallation>();
        foreach (var root in roots)
        {
            var installation = TryDetect(root);
            if (installation is not null
                && !installations.Any(item => string.Equals(
                    item.InstallRoot,
                    installation.InstallRoot,
                    StringComparison.OrdinalIgnoreCase)))
            {
                installations.Add(installation);
            }
        }

        return installations;
    }

    internal static DeepSeekDesktopInstallation? TryDetect(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return null;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(installRoot.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return TryDetectOfficialHarnessDesktop(normalizedRoot)
            ?? TryDetectDshDesktopV2(normalizedRoot)
            ?? TryDetectLegacyDesktop(normalizedRoot);
    }

    internal static string? TryResolveDshHome(
        DeepSeekDesktopInstallation installation,
        string? localApplicationData = null)
    {
        if (installation.IsOfficial)
        {
            return OfficialDesktopService.ResolveDefaultHome();
        }

        var modulesFile = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(installation.DshPackageRoot)) ?? string.Empty,
            ".modules.yaml");
        var fromModules = TryReadDshHomeFromModulesFile(modulesFile);
        if (fromModules is not null)
        {
            return fromModules;
        }

        if (!string.Equals(installation.ProductName, "DeepSeek Desktop", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var localData = string.IsNullOrWhiteSpace(localApplicationData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationData;
        return TryNormalizeDshHome(Path.Combine(localData, "DeepSeek Harness Data"));
    }

    private static string? TryReadDshHomeFromModulesFile(string modulesFile)
    {
        try
        {
            if (!File.Exists(modulesFile))
            {
                return null;
            }

            var match = Regex.Match(
                File.ReadAllText(modulesFile, Encoding.UTF8),
                "(?m)^\\s*[\"']?virtualStoreDir[\"']?\\s*:\\s*(?<value>\"(?:\\\\.|[^\"])*\"|'[^']*'|[^\\r\\n#]+)");
            if (!match.Success)
            {
                return null;
            }

            var value = match.Groups["value"].Value.Trim();
            var virtualStore = value.StartsWith('"')
                ? JsonSerializer.Deserialize<string>(value)
                : value.Trim('\'');
            var current = string.IsNullOrWhiteSpace(virtualStore)
                ? null
                : new DirectoryInfo(virtualStore);
            for (var depth = 0; depth < 4 && current is not null; depth++)
            {
                current = current.Parent;
            }

            return current is null ? null : TryNormalizeDshHome(current.FullName);
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryNormalizeDshHome(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(normalized)
                || (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            return Directory.Exists(Path.Combine(normalized, "sessions"))
                || Directory.Exists(Path.Combine(normalized, "storages"))
                || Directory.Exists(Path.Combine(normalized, "profiles"))
                || File.Exists(Path.Combine(normalized, "settings.yaml"))
                || File.Exists(Path.Combine(normalized, ".credentials.yaml"))
                    ? normalized
                    : null;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return null;
        }
    }

    internal static DeepSeekDesktopInstallation? TryDetectOfficialHarnessDesktop(string installRoot)
    {
        try
        {
            var root = Path.GetFullPath(installRoot);
            if (!File.Exists(Path.Combine(root, "resources", "app.asar")))
            {
                return null;
            }

            // electron-builder normally derives this name from productName.
            // Renamed executables must identify the official product in PE metadata.
            var executable = Path.Combine(root, "DeepSeek Harness.exe");
            if (!File.Exists(executable))
            {
                executable = Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(IsOfficialHarnessExecutable);
            }

            if (executable is null)
            {
                return null;
            }

            var version = TryReadFileVersion(executable)
                ?? GetRegisteredInstallations().FirstOrDefault(item =>
                    IsOfficialDisplayName(item.DisplayName)
                    && ExternalDshHomeGuard.SameHome(item.Root, root))?.Version;
            var (updateChannel, updateUrl) = TryReadAppUpdateMetadata(root);
            return new DeepSeekDesktopInstallation(
                root, version,
                NodeExecutablePath: string.Empty,
                DshExecutablePath: string.Empty,
                DshPackageRoot: string.Empty,
                DshVersion: string.Empty,
                LaunchSpec: new DshRuntimeLaunchSpec(
                    DshRuntimeLaunchMode.OfficialDesktopApp,
                    executable,
                    ProductName: "DeepSeek Harness",
                    ProductVersion: version),
                ProductName: "DeepSeek Harness",
                IsOfficial: true,
                UpdateChannel: updateChannel,
                UpdateUrl: updateUrl);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException or Win32Exception)
        {
            return null;
        }
    }

    private static (string? Channel, string? Url) TryReadAppUpdateMetadata(string installRoot)
    {
        try
        {
            var path = Path.Combine(installRoot, "resources", "app-update.yml");
            return File.Exists(path) ? ParseAppUpdateMetadata(File.ReadAllText(path, Encoding.UTF8)) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Optional display metadata must not prevent detection of the application.
            return default;
        }
    }

    internal static (string? Channel, string? Url) ParseAppUpdateMetadata(string yaml)
    {
        string? channel = null;
        string? url = null;
        using var reader = new StringReader(yaml);
        while (reader.ReadLine() is { } line)
        {
            // Only simple top-level scalars from electron-builder's update file.
            line = line.TrimStart('\uFEFF');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator].TrimEnd();
            if (key is not ("channel" or "url")) continue;
            var value = ReadUpdateScalar(line[(separator + 1)..].Trim());
            if (key == "channel") channel = value;
            else url = value;
        }
        return (channel, url);
    }

    private static string? ReadUpdateScalar(string value)
    {
        if (value.StartsWith('"'))
        {
            var quoted = Regex.Match(value, "^\\\"(?:\\\\.|[^\\\"\\\\])*\\\"(?=\\s*(?:#.*)?$)");
            if (!quoted.Success) return null;
            try { return FirstNonEmpty(JsonSerializer.Deserialize<string>(quoted.Value)); }
            catch (JsonException) { return null; }
        }
        if (value.StartsWith('\''))
        {
            var quoted = Regex.Match(value, "^'(?<value>(?:''|[^'])*)'\\s*(?:#.*)?$");
            return quoted.Success ? FirstNonEmpty(quoted.Groups["value"].Value.Replace("''", "'")) : null;
        }
        var plain = Regex.Replace(value, @"(?:^|\s)#.*$", string.Empty).Trim();
        return plain is "" or "null" or "~" || plain[0] is '|' or '>' or '[' or '{' or '&' or '*'
            ? null : plain;
    }

    private static bool IsOfficialHarnessExecutable(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.ProductName, "DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.FileDescription, "DeepSeek Harness", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DeepSeekDesktopInstallation? TryDetectDshDesktopV2(string installRoot)
    {
        var hostExecutable = Path.Combine(installRoot, "DSH Desktop.exe");
        var unpackedRoot = Path.Combine(installRoot, "resources", "app.asar.unpacked");
        var packageRoot = Path.Combine(
            unpackedRoot,
            "node_modules",
            "@deepseek-ai",
            "dsh");
        var dshEntryPoint = Path.Combine(packageRoot, "lib", "bin.js");
        var desktopCli = Path.Combine(
            unpackedRoot,
            "lib",
            "desktop-cli.js");
        var pnpmScript = Path.Combine(
            unpackedRoot,
            "node_modules",
            "pnpm",
            "bin",
            "pnpm.mjs");
        var dshVersion = DshRuntimeDetector.TryReadPackageVersion(packageRoot);
        if (!File.Exists(hostExecutable)
            || !File.Exists(dshEntryPoint)
            || !File.Exists(desktopCli)
            || !File.Exists(pnpmScript)
            || string.IsNullOrWhiteSpace(dshVersion))
        {
            return null;
        }

        var normalizedHost = Path.GetFullPath(hostExecutable);
        var normalizedEntryPoint = Path.GetFullPath(dshEntryPoint);
        var normalizedDesktopCli = Path.GetFullPath(desktopCli);
        var normalizedPnpmScript = Path.GetFullPath(pnpmScript);
        var desktopVersion = TryReadFileVersion(normalizedHost)
            ?? TryReadDesktopVersion(installRoot);
        var launchSpec = new DshRuntimeLaunchSpec(
            DshRuntimeLaunchMode.ElectronBootstrap,
            normalizedHost,
            EntryPointPath: normalizedDesktopCli,
            NodeExecutablePath: normalizedHost,
            PnpmScriptPath: normalizedPnpmScript,
            ProductName: "DSH Desktop",
            ProductVersion: desktopVersion);

        return new DeepSeekDesktopInstallation(
            installRoot,
            desktopVersion,
            normalizedHost,
            normalizedEntryPoint,
            Path.GetFullPath(packageRoot),
            dshVersion,
            launchSpec,
            "DSH Desktop");
    }

    private static DeepSeekDesktopInstallation? TryDetectLegacyDesktop(string installRoot)
    {
        if (!File.Exists(Path.Combine(installRoot, "DeepSeek Desktop.exe")))
        {
            return null;
        }

        var nodeExecutable = Path.Combine(installRoot, "runtime", "node.exe");
        var packageRoot = Path.Combine(
            installRoot,
            "app",
            "node_modules",
            "@deepseek-ai",
            "dsh");
        var dshVersion = DshRuntimeDetector.TryReadPackageVersion(packageRoot);
        var dshExecutable = FindDshExecutable(installRoot);
        if (!File.Exists(nodeExecutable)
            || dshExecutable is null
            || string.IsNullOrWhiteSpace(dshVersion))
        {
            return null;
        }

        var normalizedNode = Path.GetFullPath(nodeExecutable);
        var normalizedDshExecutable = Path.GetFullPath(dshExecutable);
        var desktopVersion = TryReadDesktopVersion(installRoot);
        return new DeepSeekDesktopInstallation(
            installRoot,
            desktopVersion,
            normalizedNode,
            normalizedDshExecutable,
            Path.GetFullPath(packageRoot),
            dshVersion,
            new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.DirectCommand,
                normalizedDshExecutable,
                NodeExecutablePath: normalizedNode,
                ProductName: "DeepSeek Desktop",
                ProductVersion: desktopVersion));
    }

    internal static IEnumerable<string> GetInstallRootCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDirectory in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                continue;
            }

            foreach (var relative in new[]
            {
                Path.Combine("Programs", "DeepSeek Desktop"),
                "DeepSeek Desktop",
                Path.Combine("Programs", "DSH Desktop"),
                "DSH Desktop",
                Path.Combine("Programs", "DeepSeek Harness"),
                "DeepSeek Harness"
            })
            {
                var candidate = Path.Combine(baseDirectory, relative);
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var registered in GetRegisteredInstallations())
        {
            if (seen.Add(registered.Root))
            {
                yield return registered.Root;
            }
        }
    }

    private sealed record RegisteredInstallation(string Root, string? DisplayName, string? Version);

    private static IReadOnlyList<RegisteredInstallation> GetRegisteredInstallations()
    {
        var results = new List<RegisteredInstallation>();
        if (!OperatingSystem.IsWindows()) return results;
        foreach (var (root, path) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        })
        {
            try
            {
                using var uninstall = root.OpenSubKey(path, writable: false);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var application = uninstall.OpenSubKey(subKeyName, writable: false);
                    if (application is null)
                    {
                        continue;
                    }

                    var displayName = application.GetValue("DisplayName") as string;
                    if (!IsSupportedDisplayName(displayName))
                    {
                        continue;
                    }

                    var installLocation = application.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        results.Add(new RegisteredInstallation(
                            installLocation.Trim().Trim('"'), displayName,
                            FirstNonEmpty(application.GetValue("DisplayVersion") as string)));
                    }
                }
            }
            catch
            {
                // Registry discovery is only an optional fallback. Standard
                // install directories remain available when a hive is denied.
            }
        }

        return results;
    }

    private static bool IsSupportedDisplayName(string? displayName) =>
        displayName?.StartsWith("DeepSeek Desktop", StringComparison.OrdinalIgnoreCase) == true
        || displayName?.StartsWith("DSH Desktop", StringComparison.OrdinalIgnoreCase) == true
        || IsOfficialDisplayName(displayName);

    // electron-builder NSIS writes "<productName> <version>", e.g. "DeepSeek Harness 0.2.1-rc.5".
    internal static bool IsOfficialDisplayName(string? displayName) =>
        string.Equals(displayName, "DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
        || (displayName is not null
            && displayName.StartsWith("DeepSeek Harness ", StringComparison.OrdinalIgnoreCase)
            && displayName.Length > "DeepSeek Harness ".Length
            && char.IsDigit(displayName["DeepSeek Harness ".Length]));

    private static string? FindDshExecutable(string installRoot)
    {
        var binDirectory = Path.Combine(installRoot, "app", "node_modules", ".bin");
        foreach (var fileName in new[] { "dsh.cmd", "dsh.exe", "dsh" })
        {
            var candidate = Path.Combine(binDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryReadDesktopVersion(string installRoot)
    {
        var versionFile = Path.Combine(installRoot, "VERSION.txt");
        try
        {
            if (File.Exists(versionFile))
            {
                var match = VersionFilePattern.Match(File.ReadAllText(versionFile, Encoding.UTF8));
                if (match.Success)
                {
                    return match.Groups["version"].Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to desktop-settings.json below.
        }

        var settingsFile = Path.Combine(installRoot, "desktop-settings.json");
        try
        {
            if (!File.Exists(settingsFile))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile, Encoding.UTF8));
            return document.RootElement.TryGetProperty("desktopVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadFileVersion(string executablePath)
    {
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            return FirstNonEmpty(versionInfo.FileVersion, versionInfo.ProductVersion);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
