using System.IO;
using System.Text;

namespace DshLauncher.Services;

public sealed class LauncherLogService
{
    private static readonly object FileGate = new();
    private readonly LauncherPaths _paths;

    public LauncherLogService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    public string LogDirectory => _paths.LogsDirectory;

    public void Write(string category, string message, Exception? exception = null)
    {
        var safeCategory = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        var normalized = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        var line = $"[{DateTimeOffset.Now:O}] [{safeCategory}] {normalized}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(_paths.LogsDirectory);
                File.AppendAllText(
                    Path.Combine(_paths.LogsDirectory, $"launcher-{DateTime.Now:yyyy-MM-dd}.log"),
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
                PruneCore(DateTimeOffset.Now.AddDays(-7));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the user operation it is observing.
        }
    }

    public IReadOnlyList<string> ReadRecent(int maximumLines = 500, string? category = null)
    {
        maximumLines = Math.Clamp(maximumLines, 1, 5_000);
        try
        {
            lock (FileGate)
            {
                if (!Directory.Exists(_paths.LogsDirectory))
                {
                    return Array.Empty<string>();
                }

                var filter = string.IsNullOrWhiteSpace(category) ? null : $"[{category.Trim()}]";
                return Directory.EnumerateFiles(_paths.LogsDirectory, "launcher-*.log", SearchOption.TopDirectoryOnly)
                    .Where(static path => !IsReparsePoint(path))
                    .OrderBy(File.GetLastWriteTimeUtc)
                    .SelectMany(File.ReadLines)
                    .Where(line => filter is null || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .TakeLast(maximumLines)
                    .ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private void PruneCore(DateTimeOffset threshold)
    {
        foreach (var path in Directory.EnumerateFiles(_paths.LogsDirectory, "launcher-*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsReparsePoint(path) && File.GetLastWriteTimeUtc(path) < threshold.UtcDateTime)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }
}
