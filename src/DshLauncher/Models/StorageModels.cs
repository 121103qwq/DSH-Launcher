using System;
using System.Collections.Generic;
using System.Linq;

namespace DshLauncher.Models;

public enum InstanceStorageCategory
{
    Sessions,
    Snapshots,
    Reports,
    PluginsAndDependencies,
    Cache,
    Other
}

public sealed record StorageCategoryUsage(
    InstanceStorageCategory Category,
    long Bytes,
    long FileCount)
{
    public long SizeBytes => Bytes;
}

public sealed record InstanceStorageUsage(
    string InstanceId,
    string InstanceName,
    string DshHome,
    DateTimeOffset ScannedAt,
    IReadOnlyList<StorageCategoryUsage> Categories)
{
    public long TotalBytes => Categories.Sum(category => category.Bytes);

    public long TotalFiles => Categories.Sum(category => category.FileCount);

    public StorageCategoryUsage GetCategory(InstanceStorageCategory category) =>
        Categories.FirstOrDefault(item => item.Category == category)
        ?? new StorageCategoryUsage(category, 0, 0);
}

public sealed record StorageCleanupCandidate(
    InstanceStorageCategory Category,
    string Path,
    long Bytes,
    long FileCount)
{
    public long SizeBytes => Bytes;

    public string FullPath => Path;
}

public sealed record StorageCleanupPreview(
    string InstanceId,
    string InstanceName,
    string DshHome,
    DateTimeOffset ScannedAt,
    IReadOnlyList<StorageCleanupCandidate> Candidates)
{
    public long ReclaimableBytes => Candidates.Sum(candidate => candidate.Bytes);

    public long ReclaimableFiles => Candidates.Sum(candidate => candidate.FileCount);

    public long TotalBytes => ReclaimableBytes;

    public long TotalFiles => ReclaimableFiles;
}
