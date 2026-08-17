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
        @"(?im)^DeepSeek Desktop\s+v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)\s*$",
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

        if (!File.Exists(Path.Combine(normalizedRoot, "DeepSeek Desktop.exe")))
        {
            return null;
        }

        var nodeExecutable = Path.Combine(normalizedRoot, "runtime", "node.exe");
        var packageRoot = Path.Combine(
            normalizedRoot,
            "app",
            "node_modules",
            "@deepseek-ai",
            "dsh");
        var dshVersion = DshRuntimeDetector.TryReadPackageVersion(packageRoot);
        var dshExecutable = FindDshExecutable(normalizedRoot);
        if (!File.Exists(nodeExecutable)
            || dshExecutable is null
            || string.IsNullOrWhiteSpace(dshVersion))
        {
            return null;
        }

        return new DeepSeekDesktopInstallation(
            normalizedRoot,
            TryReadDesktopVersion(normalizedRoot),
            Path.GetFullPath(nodeExecutable),
            Path.GetFullPath(dshExecutable),
            Path.GetFullPath(packageRoot),
            dshVersion);
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
                "DeepSeek Desktop"
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
                    if (displayName?.StartsWith("DeepSeek Desktop", StringComparison.OrdinalIgnoreCase) != true)
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
}
