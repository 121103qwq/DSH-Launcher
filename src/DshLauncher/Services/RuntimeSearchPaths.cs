using System.IO;

namespace DshLauncher.Services;

internal static class RuntimeSearchPaths
{
    public static IReadOnlyList<string> GetCurrentDirectories() =>
        GetDirectories(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));

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
}
