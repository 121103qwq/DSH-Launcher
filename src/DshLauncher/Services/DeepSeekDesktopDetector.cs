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

    public static IReadOnlyList<DeepSeekDesktopInstallation> DetectInstallations()
    {
        var installations = new List<DeepSeekDesktopInstallation>();
        foreach (var root in GetInstallRootCandidates())
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

        return TryDetectDshDesktopV2(normalizedRoot)
            ?? TryDetectLegacyDesktop(normalizedRoot);
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
                "DSH Desktop"
            })
            {
                var candidate = Path.Combine(baseDirectory, relative);
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var registeredRoot in GetRegisteredInstallRoots())
        {
            if (seen.Add(registeredRoot))
            {
                yield return registeredRoot;
            }
        }
    }

    private static IReadOnlyList<string> GetRegisteredInstallRoots()
    {
        var results = new List<string>();
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
                        results.Add(installLocation.Trim().Trim('"'));
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
        || displayName?.StartsWith("DSH Desktop", StringComparison.OrdinalIgnoreCase) == true;

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
