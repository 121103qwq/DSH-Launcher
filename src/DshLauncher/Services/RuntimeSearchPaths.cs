using System.IO;

namespace DshLauncher.Services;

internal static class RuntimeSearchPaths
{
    public static IReadOnlyList<string> GetCurrentDirectories() =>
        GetDirectories(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));

    internal static IReadOnlyList<string> GetNodeRuntimeDirectories()
    {
        var directories = GetCurrentDirectories().ToList();
        var seen = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);

        void AddDirectory(string? directory)
        {
            var normalized = TryNormalizeDirectory(directory);
            if (normalized is not null && seen.Add(normalized))
            {
                directories.Add(normalized);
            }
        }

        void AddNvmHome(string? home)
        {
            var normalizedHome = TryNormalizeDirectory(home);
            if (normalizedHome is null)
            {
                return;
            }

            AddDirectory(normalizedHome);
            foreach (var versionDirectory in GetChildDirectories(normalizedHome))
            {
                AddDirectory(versionDirectory);
            }
        }

        void AddVoltaHome(string? home)
        {
            var normalizedHome = TryNormalizeDirectory(home);
            if (normalizedHome is null)
            {
                return;
            }

            AddDirectory(Path.Combine(normalizedHome, "bin"));
            var nodeImages = Path.Combine(normalizedHome, "tools", "image", "node");
            foreach (var versionDirectory in GetChildDirectories(nodeImages))
            {
                AddDirectory(versionDirectory);
            }
        }

        void AddFnmHome(string? home)
        {
            var normalizedHome = TryNormalizeDirectory(home);
            if (normalizedHome is null)
            {
                return;
            }

            var nodeVersions = Path.Combine(normalizedHome, "node-versions");
            foreach (var versionDirectory in GetChildDirectories(nodeVersions))
            {
                AddDirectory(Path.Combine(versionDirectory, "installation"));
            }
        }

        void AddScoopRoot(string? root)
        {
            var normalizedRoot = TryNormalizeDirectory(root);
            if (normalizedRoot is null)
            {
                return;
            }

            AddDirectory(Path.Combine(normalizedRoot, "shims"));
            var appsDirectory = Path.Combine(normalizedRoot, "apps");
            foreach (var packageDirectory in GetChildDirectories(appsDirectory, "nodejs*"))
            {
                AddDirectory(packageDirectory);
                foreach (var versionDirectory in GetChildDirectories(packageDirectory))
                {
                    AddDirectory(versionDirectory);
                }
            }
        }

        foreach (var symlinkDirectory in GetEnvironmentDirectories("NVM_SYMLINK"))
        {
            AddDirectory(symlinkDirectory);
        }

        foreach (var home in GetEnvironmentDirectories("NVM_HOME"))
        {
            AddNvmHome(home);
        }

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            AddNvmHome(Path.Combine(applicationData, "nvm"));
        }

        foreach (var home in GetEnvironmentDirectories("VOLTA_HOME"))
        {
            AddVoltaHome(home);
        }

        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            AddVoltaHome(Path.Combine(localApplicationData, "Volta"));
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddVoltaHome(Path.Combine(userProfile, ".volta"));
        }

        foreach (var activeDirectory in GetEnvironmentDirectories("FNM_MULTISHELL_PATH"))
        {
            AddDirectory(activeDirectory);
        }

        foreach (var home in GetEnvironmentDirectories("FNM_DIR"))
        {
            AddFnmHome(home);
        }

        if (!string.IsNullOrWhiteSpace(applicationData))
        {
            AddFnmHome(Path.Combine(applicationData, "fnm"));
        }

        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            AddFnmHome(Path.Combine(localApplicationData, "fnm"));
        }

        foreach (var root in GetEnvironmentDirectories("SCOOP"))
        {
            AddScoopRoot(root);
        }

        foreach (var root in GetEnvironmentDirectories("SCOOP_GLOBAL"))
        {
            AddScoopRoot(root);
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddScoopRoot(Path.Combine(userProfile, "scoop"));
        }

        if (!string.IsNullOrWhiteSpace(commonApplicationData))
        {
            AddScoopRoot(Path.Combine(commonApplicationData, "scoop"));
        }

        return directories;
    }

    internal static IReadOnlyList<string> GetDirectories(
        string? processPath,
        string? userPath,
        string? machinePath)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pathValue in new[] { processPath, userPath, machinePath })
        {
            foreach (var rawDirectory in (pathValue ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = TryNormalizeDirectory(rawDirectory);
                if (directory is not null && seen.Add(directory))
                {
                    directories.Add(directory);
                }
            }
        }

        return directories;
    }

    public static string BuildCurrentPath(string? preferredExecutablePath = null)
    {
        var directories = GetCurrentDirectories().ToList();
        if (!string.IsNullOrWhiteSpace(preferredExecutablePath))
        {
            var preferredDirectory = TryNormalizeDirectory(
                Path.GetDirectoryName(Path.GetFullPath(preferredExecutablePath)));
            if (preferredDirectory is not null)
            {
                directories.RemoveAll(directory => string.Equals(
                    directory,
                    preferredDirectory,
                    StringComparison.OrdinalIgnoreCase));
                directories.Insert(0, preferredDirectory);
            }
        }

        return string.Join(Path.PathSeparator, directories);
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

    private static IEnumerable<string> GetEnvironmentDirectories(string variableName)
    {
        foreach (var target in new[]
                 {
                     EnvironmentVariableTarget.Process,
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Machine
                 })
        {
            var directory = TryNormalizeDirectory(
                Environment.GetEnvironmentVariable(variableName, target));
            if (directory is not null)
            {
                yield return directory;
            }
        }
    }

    private static IReadOnlyList<string> GetChildDirectories(
        string directory,
        string searchPattern = "*")
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetDirectories(directory, searchPattern, SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            return Array.Empty<string>();
        }
    }
}
