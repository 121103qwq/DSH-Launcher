using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Reads per-instance storage usage and produces a non-mutating cleanup
/// preview. The preview is deliberately limited to files that Launcher owns:
/// automatic snapshots, failure reports and known cache locations.
/// </summary>
public sealed class InstanceStorageService
{
    private static readonly InstanceStorageCategory[] Categories = Enum.GetValues<InstanceStorageCategory>();
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> CacheDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cache",
        "cache",
        "caches",
        "webview2"
    };

    private readonly LauncherPaths _paths;

    public InstanceStorageService(LauncherPaths? paths = null)
    {
        _paths = paths ?? new LauncherPaths();
    }

    /// <summary>
    /// Scans the instance DSH_HOME and its Launcher-managed snapshot directory.
    /// Unreadable entries are omitted, and reparse points are never followed.
    /// </summary>
    public Task<InstanceStorageUsage> GetUsageAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Task.Run(
            () => Scan(instance, cancellationToken).Usage,
            cancellationToken);
    }

    /// <summary>
    /// Returns file-level cleanup candidates without deleting or changing any
    /// file. Sessions, credentials, manual snapshots and DSH_HOME itself are
    /// intentionally excluded.
    /// </summary>
    public Task<StorageCleanupPreview> PreviewCleanupAsync(
        ManagerInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Task.Run(
            () => Scan(instance, cancellationToken).CleanupPreview,
            cancellationToken);
    }

    private ScanResult Scan(ManagerInstance instance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scannedAt = DateTimeOffset.UtcNow;
        var home = TryGetFullPath(instance.DshHome);
        var snapshotRoot = TryGetSnapshotRoot(instance);
        var entries = new List<StorageFile>();
        var visitedPaths = new HashSet<string>(PathComparer);

        if (home is not null)
        {
            ScanTree(
                home,
                forcedCategory: null,
                home,
                entries,
                visitedPaths,
                cancellationToken);
        }

        if (snapshotRoot is not null)
        {
            ScanTree(
                snapshotRoot,
                InstanceStorageCategory.Snapshots,
                home,
                entries,
                visitedPaths,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var usage = new InstanceStorageUsage(
            instance.Id,
            instance.Name,
            home ?? instance.DshHome,
            scannedAt,
            Categories
                .Select(category => new StorageCategoryUsage(
                    category,
                    entries
                        .Where(entry => entry.Category == category)
                        .Sum(entry => entry.Bytes),
                    entries.Count(entry => entry.Category == category)))
                .ToArray());

        var reportsRoot = home is null
            ? null
            : Path.Combine(home, ".dsh-launcher", "reports");
        var candidates = entries
            .Where(entry => IsCleanupCandidate(entry, home, snapshotRoot, reportsRoot))
            .OrderBy(entry => entry.Category)
            .ThenBy(entry => entry.FullPath, PathComparer)
            .Select(entry => new StorageCleanupCandidate(
                entry.Category,
                entry.FullPath,
                entry.Bytes,
                FileCount: 1L))
            .ToArray();

        var cleanupPreview = new StorageCleanupPreview(
            instance.Id,
            instance.Name,
            home ?? instance.DshHome,
            scannedAt,
            candidates);

        return new ScanResult(usage, cleanupPreview);
    }

    private static void ScanTree(
        string root,
        InstanceStorageCategory? forcedCategory,
        string? home,
        ICollection<StorageFile> entries,
        ISet<string> visitedPaths,
        CancellationToken cancellationToken)
    {
        if (!TryReadAttributes(root, out var rootAttributes)
            || (rootAttributes & FileAttributes.ReparsePoint) != 0
            || (rootAttributes & FileAttributes.Directory) == 0)
        {
            return;
        }

        var pending = new Stack<string>();
        if (visitedPaths.Add(root))
        {
            pending.Push(root);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (IsRecoverableFileSystemException(ex))
            {
                continue;
            }

            try
            {
                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryGetFullPath(child, out var fullChild)
                        || !visitedPaths.Add(fullChild)
                        || !TryReadAttributes(fullChild, out var attributes)
                        || (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(fullChild);
                        continue;
                    }

                    long bytes;
                    try
                    {
                        bytes = new FileInfo(fullChild).Length;
                    }
                    catch (Exception ex) when (IsRecoverableFileSystemException(ex))
                    {
                        continue;
                    }

                    var category = forcedCategory
                        ?? ClassifyHomeFile(home!, fullChild);
                    entries.Add(new StorageFile(category, fullChild, bytes));
                }
            }
            catch (Exception ex) when (IsRecoverableFileSystemException(ex))
            {
                // An enumeration can fail after yielding accessible siblings.
                // Keep those results and continue with the rest of the tree.
            }
        }
    }

    private static InstanceStorageCategory ClassifyHomeFile(string home, string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(home, path);
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            return InstanceStorageCategory.Other;
        }

        var parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return InstanceStorageCategory.Other;
        }

        if (string.Equals(parts[0], "sessions", StringComparison.OrdinalIgnoreCase))
        {
            return InstanceStorageCategory.Sessions;
        }

        if (string.Equals(parts[0], ".dsh-launcher", StringComparison.OrdinalIgnoreCase)
            && parts.Length > 1
            && string.Equals(parts[1], "reports", StringComparison.OrdinalIgnoreCase))
        {
            return InstanceStorageCategory.Reports;
        }

        if (string.Equals(parts[0], "profiles", StringComparison.OrdinalIgnoreCase)
            || parts.Any(part => string.Equals(part, "node_modules", StringComparison.OrdinalIgnoreCase)))
        {
            return InstanceStorageCategory.PluginsAndDependencies;
        }

        return IsCachePath(parts)
            ? InstanceStorageCategory.Cache
            : InstanceStorageCategory.Other;
    }

    private static bool IsCachePath(IReadOnlyList<string> parts)
    {
        if (parts.Count == 2
            && string.Equals(parts[0], "storages", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "session_projcache.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // CacheDirectoryNames only applies to directory segments, never to a
        // similarly named file in an arbitrary location.
        return parts.Take(parts.Count - 1).Any(CacheDirectoryNames.Contains);
    }

    private static bool IsCleanupCandidate(
        StorageFile entry,
        string? home,
        string? snapshotRoot,
        string? reportsRoot)
    {
        if (IsCredentialFile(entry.FullPath))
        {
            return false;
        }

        if (entry.Category == InstanceStorageCategory.Reports)
        {
            return reportsRoot is not null && IsDirectlyUnderOrEqual(entry.FullPath, reportsRoot);
        }

        if (entry.Category == InstanceStorageCategory.Snapshots)
        {
            if (snapshotRoot is null
                || !string.Equals(Path.GetDirectoryName(entry.FullPath), snapshotRoot, GetPathComparison()))
            {
                return false;
            }

            var fileName = Path.GetFileName(entry.FullPath);
            return fileName.StartsWith("auto-", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(fileName), ".dshsnapshot", StringComparison.OrdinalIgnoreCase);
        }

        return entry.Category == InstanceStorageCategory.Cache
            && home is not null
            && IsManagedCachePath(home, entry.FullPath);
    }

    private static bool IsManagedCachePath(string home, string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(home, path);
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            return false;
        }

        var parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && string.Equals(parts[0], "storages", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "session_projcache.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (parts.Length < 2)
        {
            return false;
        }

        if (string.Equals(parts[0], ".dsh-launcher", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(parts[1], "cache", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[1], "caches", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(parts[0], "cache", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parts[0], "caches", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parts[0], ".cache", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parts[0], "webview2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCredentialFile(string path) =>
        string.Equals(Path.GetFileName(path), ".credentials.yaml", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), ".credentials.yml", StringComparison.OrdinalIgnoreCase);

    private string? TryGetSnapshotRoot(ManagerInstance instance)
    {
        try
        {
            var root = TryGetFullPath(_paths.GetVersionSnapshotDirectory(instance.Id));
            var backups = TryGetFullPath(_paths.BackupsDirectory);
            return root is not null && backups is not null && IsDirectlyUnderOrEqual(root, backups)
                ? root
                : null;
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            return null;
        }
    }

    private static bool IsDirectlyUnderOrEqual(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var comparison = GetPathComparison();
        return string.Equals(normalizedPath, normalizedRoot, comparison)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.AltDirectorySeparatorChar,
                comparison);
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string? TryGetFullPath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            return null;
        }
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        var normalized = TryGetFullPath((string?)path);
        if (normalized is null)
        {
            return false;
        }

        fullPath = normalized;
        return true;
    }

    private static bool TryReadAttributes(string path, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (IsRecoverableFileSystemException(ex))
        {
            return false;
        }
    }

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException;

    private sealed record StorageFile(
        InstanceStorageCategory Category,
        string FullPath,
        long Bytes);

    private sealed record ScanResult(
        InstanceStorageUsage Usage,
        StorageCleanupPreview CleanupPreview);
}
