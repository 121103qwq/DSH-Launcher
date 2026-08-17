using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

/// <summary>
/// Local rollback snapshots are deliberately different from shareable
/// .dshpack files. They preserve exact configuration (including official DSh
/// credentials), encrypt it for the current Windows user, and never include
/// conversations or runtime dependencies.
/// </summary>
public sealed class VersionSnapshotService
{
    private const int CurrentFormatVersion = 1;
    private const long MaximumFileSize = 16 * 1024 * 1024;
    private const long MaximumPayloadSize = 64 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DSHSNAP1");
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("DSH Launcher version snapshot v1"));
    private static readonly string[] SnapshotFiles =
    {
        "settings.yaml",
        ".credentials.yaml",
        "launcher.patch.yml",
        ".dsh-launcher/version-settings.json",
        ".dsh-launcher/providers.json",
        ".dsh-launcher/mcp.json",
        "profiles/web/package.json",
        "profiles/web/pnpm-lock.yaml",
        "profiles/web/package-lock.json",
        "profiles/web/yarn.lock",
        "profiles/web/cordis.patch.yml"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly LauncherPaths _paths;
    private readonly Func<string, bool> _isRunning;

    public VersionSnapshotService(
        LauncherPaths? paths = null,
        Func<string, bool>? isRunning = null)
    {
        _paths = paths ?? new LauncherPaths();
        _isRunning = isRunning ?? (_ => false);
    }

    public VersionSnapshotInfo CreateSnapshot(ManagerInstance instance, string reason)
    {
        EnsureCanMutate(instance);
        EnsureSafeHome(instance);
        var normalizedReason = NormalizeReason(reason);
        var createdAt = DateTimeOffset.UtcNow;
        var presentFiles = new List<string>();

        using var plainStream = new MemoryStream();
        using (var archive = new ZipArchive(plainStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var relativePath in SnapshotFiles)
            {
                var source = ResolveManagedPath(instance, relativePath);
                if (!File.Exists(source))
                {
                    continue;
                }

                RejectReparsePoint(source, relativePath);
                var fileLength = new FileInfo(source).Length;
                if (fileLength > MaximumFileSize)
                {
                    throw new InvalidDataException($"快照文件过大，已拒绝保存：{relativePath}");
                }

                var entry = archive.CreateEntry($"files/{relativePath.Replace('\\', '/')}", CompressionLevel.Fastest);
                using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var entryStream = entry.Open();
                sourceStream.CopyTo(entryStream);
                presentFiles.Add(relativePath.Replace('\\', '/'));
            }

            var manifest = new SnapshotManifest(
                CurrentFormatVersion,
                instance.Id,
                instance.Name,
                createdAt,
                normalizedReason,
                SnapshotFiles.Select(path => path.Replace('\\', '/')).ToArray(),
                presentFiles.ToArray());
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            using var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
        }

        if (plainStream.Length > MaximumPayloadSize)
        {
            throw new InvalidDataException("版本快照超过 64 MiB 安全上限。 ");
        }

        var encrypted = ProtectedData.Protect(plainStream.ToArray(), Entropy, DataProtectionScope.CurrentUser);
        var directory = _paths.GetVersionSnapshotDirectory(instance.Id);
        Directory.CreateDirectory(directory);
        var fileName = $"{createdAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.dshsnapshot";
        var path = Path.Combine(directory, fileName);
        var temporary = $"{path}.tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(Magic);
                output.Write(encrypted);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return new VersionSnapshotInfo(path, createdAt, normalizedReason, new FileInfo(path).Length);
    }

    public IReadOnlyList<VersionSnapshotInfo> ListSnapshots(ManagerInstance instance)
    {
        var directory = _paths.GetVersionSnapshotDirectory(instance.Id);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<VersionSnapshotInfo>();
        }

        var results = new List<VersionSnapshotInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.dshsnapshot", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var manifest = ReadSnapshot(path, instance.Id).Manifest;
                results.Add(new VersionSnapshotInfo(
                    path,
                    manifest.CreatedAt,
                    manifest.Reason,
                    new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or JsonException)
            {
                // A partial or foreign-user snapshot cannot be restored and is
                // omitted instead of breaking the whole version control page.
            }
        }

        return results
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ToArray();
    }

    public VersionSnapshotInfo RestoreSnapshot(ManagerInstance instance, string snapshotPath)
    {
        EnsureCanMutate(instance);
        EnsureSafeHome(instance);
        var snapshot = NormalizeSnapshotPath(instance, snapshotPath);
        var rollbackPoint = CreateSnapshot(instance, "回滚前自动快照");
        using var payload = ReadSnapshot(snapshot, instance.Id);

        foreach (var relativePath in payload.Manifest.ManagedFiles)
        {
            if (!SnapshotFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"快照包含未知的受管路径：{relativePath}");
            }

            var target = ResolveManagedPath(instance, relativePath);
            RejectExistingReparsePoint(target, relativePath);
            var entry = payload.Archive.GetEntry($"files/{relativePath.Replace('\\', '/')}");
            if (entry is null)
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                continue;
            }

            if (entry.Length > MaximumFileSize)
            {
                throw new InvalidDataException($"快照条目过大：{relativePath}");
            }

            var directory = Path.GetDirectoryName(target)
                ?? throw new InvalidDataException($"快照目标没有父目录：{relativePath}");
            Directory.CreateDirectory(directory);
            var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var source = entry.Open())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return rollbackPoint;
    }

    private SnapshotPayload ReadSnapshot(string snapshotPath, string expectedInstanceId)
    {
        var bytes = File.ReadAllBytes(snapshotPath);
        if (bytes.Length <= Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("不是有效的 DSH Launcher 版本快照。 ");
        }

        var encrypted = bytes.AsSpan(Magic.Length).ToArray();
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        if (plain.LongLength > MaximumPayloadSize)
        {
            throw new InvalidDataException("版本快照解密后超过安全上限。 ");
        }

        var stream = new MemoryStream(plain, writable: false);
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("版本快照缺少 manifest.json。 ");
            SnapshotManifest manifest;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                manifest = JsonSerializer.Deserialize<SnapshotManifest>(reader.ReadToEnd(), JsonOptions)
                    ?? throw new InvalidDataException("版本快照 manifest 无效。 ");
            }

            if (manifest.FormatVersion != CurrentFormatVersion
                || !string.Equals(manifest.InstanceId, expectedInstanceId, StringComparison.Ordinal)
                || manifest.ManagedFiles is null
                || manifest.PresentFiles is null)
            {
                throw new InvalidDataException("版本快照格式或实例归属不匹配。 ");
            }

            var payload = new SnapshotPayload(manifest, archive);
            archive = null;
            return payload;
        }
        finally
        {
            archive?.Dispose();
            if (archive is not null)
            {
                stream.Dispose();
            }
        }
    }

    private string NormalizeSnapshotPath(ManagerInstance instance, string snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new ArgumentException("快照路径不能为空。", nameof(snapshotPath));
        }

        var root = Path.GetFullPath(_paths.GetVersionSnapshotDirectory(instance.Id));
        var path = Path.GetFullPath(snapshotPath);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.GetExtension(path) is not ".dshsnapshot"
            || !File.Exists(path))
        {
            throw new InvalidDataException("快照不属于当前版本。 ");
        }

        RejectReparsePoint(path, "版本快照");
        return path;
    }

    private void EnsureCanMutate(ManagerInstance instance)
    {
        if (_isRunning(instance.Id)
            || instance.RuntimeStatus == InstanceRuntimeStatus.Running
            || instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            throw new InvalidOperationException("运行中或 Attached 版本不能创建或恢复快照。 ");
        }
    }

    private static void EnsureSafeHome(ManagerInstance instance)
    {
        if (!Directory.Exists(instance.DshHome))
        {
            Directory.CreateDirectory(instance.DshHome);
        }

        RejectReparsePoint(instance.DshHome, "DSH_HOME");
    }

    private static string ResolveManagedPath(ManagerInstance instance, string relativePath)
    {
        var root = Path.GetFullPath(instance.DshHome);
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"快照路径越过 DSH_HOME：{relativePath}");
        }

        return target;
    }

    private static void RejectExistingReparsePoint(string path, string label)
    {
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            RejectReparsePoint(current, label);
            current = Path.GetDirectoryName(current);
        }

        if (File.Exists(path))
        {
            RejectReparsePoint(path, label);
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{label}不能是符号链接或重解析点。 ");
        }
    }

    private static string NormalizeReason(string reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? "手动快照" : reason.Trim();
        normalized = new string(normalized.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private sealed record SnapshotManifest(
        int FormatVersion,
        string InstanceId,
        string InstanceName,
        DateTimeOffset CreatedAt,
        string Reason,
        IReadOnlyList<string> ManagedFiles,
        IReadOnlyList<string> PresentFiles);

    private sealed class SnapshotPayload : IDisposable
    {
        public SnapshotPayload(SnapshotManifest manifest, ZipArchive archive)
        {
            Manifest = manifest;
            Archive = archive;
        }

        public SnapshotManifest Manifest { get; }

        public ZipArchive Archive { get; }

        public void Dispose() => Archive.Dispose();
    }
}
