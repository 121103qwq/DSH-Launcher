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
    private const int MaximumAutomaticSnapshotCount = 10;
    private const long MaximumAutomaticSnapshotBytes = 256 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DSHSNAP1");
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("DSH Launcher version snapshot v1"));
    private static readonly string[] SnapshotFiles =
    {
        "settings.yaml",
        ".credentials.yaml",
        "launcher.patch.yml",
        ".dsh-launcher/version-settings.json",
        ".dsh-launcher/providers.json",
        ".dsh-launcher/mcp.json"
    };
    private static readonly string[] PluginProfileFileNames =
    {
        "package.json",
        "pnpm-lock.yaml",
        "pnpm-workspace.yaml",
        "package-lock.json",
        "yarn.lock",
        "cordis.patch.yml"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly LauncherPaths _paths;
    private readonly Func<string, bool> _isRunning;
    private readonly PasswordSnapshotEncryptionService _passwordSnapshotEncryption;

    public VersionSnapshotService(
        LauncherPaths? paths = null,
        Func<string, bool>? isRunning = null,
        PasswordSnapshotEncryptionService? passwordSnapshotEncryption = null)
    {
        _paths = paths ?? new LauncherPaths();
        _isRunning = isRunning ?? (_ => false);
        _passwordSnapshotEncryption = passwordSnapshotEncryption ?? new PasswordSnapshotEncryptionService();
    }

    public VersionSnapshotInfo CreateSnapshot(
        ManagerInstance instance,
        string reason,
        bool automatic = false)
    {
        EnsureCanMutate(instance);
        return CreateSnapshotCore(instance, reason, automatic, BuildSnapshotFiles(instance, includeBaseFiles: true));
    }

    public PasswordSnapshotInfo ExportPasswordSnapshot(
        ManagerInstance instance,
        string outputPath,
        string password)
    {
        EnsureCanMutate(instance);
        EnsureSafeHome(instance);
        var destination = NormalizePasswordSnapshotOutputPath(outputPath);
        var createdAt = DateTimeOffset.UtcNow;
        var plain = BuildSnapshotPayload(
            instance,
            reason: "跨电脑密码快照",
            automatic: false,
            managedFiles: BuildSnapshotFiles(instance, includeBaseFiles: true),
            createdAt: createdAt);
        byte[] encrypted;
        try
        {
            encrypted = _passwordSnapshotEncryption.Encrypt(plain, password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        try
        {
            WriteSnapshotFile(destination, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
        return new PasswordSnapshotInfo(destination, createdAt, new FileInfo(destination).Length);
    }

    public VersionSnapshotInfo CreateLivePluginSnapshot(
        ManagerInstance instance,
        string reason)
    {
        if (instance.RuntimeOwnership == InstanceRuntimeOwnership.Attached)
        {
            throw new InvalidOperationException("Attached 版本不能创建 Launcher 热加载存档。 ");
        }

        return CreateSnapshotCore(instance, reason, automatic: true, BuildSnapshotFiles(instance, includeBaseFiles: false));
    }

    private VersionSnapshotInfo CreateSnapshotCore(
        ManagerInstance instance,
        string reason,
        bool automatic,
        IReadOnlyList<string> managedFiles)
    {
        EnsureSafeHome(instance);
        var normalizedReason = NormalizeReason(reason);
        var createdAt = DateTimeOffset.UtcNow;
        var plain = BuildSnapshotPayload(instance, normalizedReason, automatic, managedFiles, createdAt);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var directory = _paths.GetVersionSnapshotDirectory(instance.Id);
        Directory.CreateDirectory(directory);
        var fileName = $"{(automatic ? "auto" : "manual")}-{createdAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.dshsnapshot";
        var path = Path.Combine(directory, fileName);
        try
        {
            WriteSnapshotFile(path, encrypted, overwrite: false, prefix: Magic);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }

        if (automatic)
        {
            PruneAutomaticSnapshots(directory, path);
        }

        return new VersionSnapshotInfo(path, createdAt, normalizedReason, new FileInfo(path).Length);
    }

    private byte[] BuildSnapshotPayload(
        ManagerInstance instance,
        string reason,
        bool automatic,
        IReadOnlyList<string> managedFiles,
        DateTimeOffset createdAt)
    {
        var normalizedReason = NormalizeReason(reason);
        var presentFiles = new List<string>();
        using var plainStream = new MemoryStream();
        using (var archive = new ZipArchive(plainStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var relativePath in managedFiles)
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
                using var sourceStream = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
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
                managedFiles.Select(path => path.Replace('\\', '/')).ToArray(),
                presentFiles.ToArray(),
                automatic);
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            using var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
        }

        if (plainStream.Length > MaximumPayloadSize)
        {
            throw new InvalidDataException("版本快照超过 64 MiB 安全上限。 ");
        }

        return plainStream.ToArray();
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
                using var payload = ReadSnapshot(path, instance.Id);
                var manifest = payload.Manifest;
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
        using var payload = ReadSnapshot(snapshot, instance.Id);
        return RestoreSnapshotPayload(instance, payload);
    }

    public VersionSnapshotInfo RestorePasswordSnapshot(
        ManagerInstance instance,
        string snapshotPath,
        string password)
    {
        EnsureCanMutate(instance);
        EnsureSafeHome(instance);
        var snapshot = NormalizePasswordSnapshotPath(snapshotPath);
        using var payload = ReadPasswordSnapshot(snapshot, password);
        return RestoreSnapshotPayload(instance, payload);
    }

    private VersionSnapshotInfo RestoreSnapshotPayload(
        ManagerInstance instance,
        SnapshotPayload payload)
    {
        using var stagedTarget = StageSnapshot(instance, payload);
        var rollbackPoint = CreateSnapshot(instance, "回滚前自动快照", automatic: true);
        using var rollbackPayload = ReadSnapshot(rollbackPoint.FilePath, instance.Id);
        using var stagedRollback = StageSnapshot(instance, rollbackPayload);
        try
        {
            ApplyStagedSnapshot(instance, stagedTarget);
        }
        catch (Exception restoreError)
        {
            try
            {
                ApplyStagedSnapshot(instance, stagedRollback);
            }
            catch (Exception rollbackError)
            {
                throw new IOException(
                    $"快照恢复失败，自动恢复修改前状态时也遇到错误：{rollbackError.Message}",
                    restoreError);
            }

            throw;
        }

        return rollbackPoint;
    }

    private StagedSnapshot StageSnapshot(ManagerInstance instance, SnapshotPayload payload)
    {
        var stagingRoot = Path.Combine(
            _paths.GetVersionSnapshotDirectory(instance.Id),
            $".stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        var files = new List<StagedSnapshotFile>();
        try
        {
            foreach (var relativePath in payload.Manifest.ManagedFiles)
            {
                if (!IsAllowedManagedPath(relativePath))
                {
                    throw new InvalidDataException($"快照包含未知的受管路径：{relativePath}");
                }

                var target = ResolveManagedPath(instance, relativePath);
                RejectExistingReparsePoint(instance.DshHome, target, relativePath);
                var entry = payload.Archive.GetEntry($"files/{relativePath.Replace('\\', '/')}");
                if (entry is null)
                {
                    files.Add(new StagedSnapshotFile(relativePath, null));
                    continue;
                }

                if (entry.Length > MaximumFileSize)
                {
                    throw new InvalidDataException($"快照条目过大：{relativePath}");
                }

                var stagedPath = Path.Combine(stagingRoot, $"{files.Count:D2}.stage");
                using (var source = entry.Open())
                using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

                files.Add(new StagedSnapshotFile(relativePath, stagedPath));
            }

            return new StagedSnapshot(stagingRoot, files);
        }
        catch
        {
            DeleteStagingFiles(stagingRoot, files);
            throw;
        }
    }

    private static void ApplyStagedSnapshot(ManagerInstance instance, StagedSnapshot snapshot)
    {
        foreach (var file in snapshot.Files)
        {
            var target = ResolveManagedPath(instance, file.RelativePath);
            RejectExistingReparsePoint(instance.DshHome, target, file.RelativePath);
            if (file.StagedPath is null)
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                continue;
            }

            if (File.Exists(target) && FilesEqual(target, file.StagedPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(target)
                ?? throw new InvalidDataException($"快照目标没有父目录：{file.RelativePath}");
            Directory.CreateDirectory(directory);
            var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var source = new FileStream(file.StagedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
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
    }

    private static IReadOnlyList<string> BuildSnapshotFiles(
        ManagerInstance instance,
        bool includeBaseFiles)
    {
        var files = includeBaseFiles
            ? new List<string>(SnapshotFiles)
            : new List<string>();
        foreach (var profile in DshProfileService.ListProfiles(instance))
        {
            files.AddRange(PluginProfileFileNames.Select(file => $"profiles/{profile}/{file}"));
        }

        return files;
    }

    private static bool IsAllowedManagedPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (SnapshotFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = normalized.Split('/');
        return parts.Length == 3
            && string.Equals(parts[0], "profiles", StringComparison.OrdinalIgnoreCase)
            && DshProfileService.TryNormalizeName(parts[1], out _)
            && PluginProfileFileNames.Contains(parts[2], StringComparer.OrdinalIgnoreCase);
    }

    private static bool FilesEqual(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> leftBuffer = stackalloc byte[8192];
        Span<byte> rightBuffer = stackalloc byte[8192];
        while (true)
        {
            var leftRead = left.Read(leftBuffer);
            var rightRead = right.Read(rightBuffer);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead]))
            {
                return false;
            }
        }
    }

    private static void WriteSnapshotFile(
        string path,
        byte[] payload,
        bool overwrite = true,
        byte[]? prefix = null)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (prefix is not null)
                {
                    output.Write(prefix);
                }

                output.Write(payload);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private SnapshotPayload ReadSnapshot(string snapshotPath, string expectedInstanceId)
    {
        var bytes = File.ReadAllBytes(snapshotPath);
        if (bytes.Length <= Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("不是有效的 DSH Launcher 版本快照。 ");
        }

        var encrypted = bytes.AsSpan(Magic.Length).ToArray();
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
        if (plain.LongLength > MaximumPayloadSize)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new InvalidDataException("版本快照解密后超过安全上限。 ");
        }

        return ReadSnapshotPayload(plain, expectedInstanceId);
    }

    private SnapshotPayload ReadPasswordSnapshot(string snapshotPath, string password)
    {
        var fileInfo = new FileInfo(snapshotPath);
        if (fileInfo.Length > PasswordSnapshotEncryptionService.MaximumPlaintextBytes + 1024)
        {
            throw new InvalidDataException("跨电脑密码快照超过安全上限。 ");
        }

        var encrypted = File.ReadAllBytes(snapshotPath);
        if (encrypted.LongLength > PasswordSnapshotEncryptionService.MaximumPlaintextBytes + 1024)
        {
            CryptographicOperations.ZeroMemory(encrypted);
            throw new InvalidDataException("跨电脑密码快照超过安全上限。 ");
        }

        byte[] plain;
        try
        {
            plain = _passwordSnapshotEncryption.Decrypt(encrypted, password);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("跨电脑快照密码错误或文件已损坏。 ");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }

        if (plain.LongLength > MaximumPayloadSize)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new InvalidDataException("跨电脑密码快照解密后超过安全上限。 ");
        }

        return ReadSnapshotPayload(plain, expectedInstanceId: null);
    }

    private SnapshotPayload ReadSnapshotPayload(byte[] plain, string? expectedInstanceId)
    {
        var stream = new MemoryStream(plain, writable: false);
        ZipArchive? archive = null;
        var transferredToPayload = false;
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
                || (expectedInstanceId is not null
                    && !string.Equals(manifest.InstanceId, expectedInstanceId, StringComparison.Ordinal))
                || manifest.ManagedFiles is null
                || manifest.PresentFiles is null)
            {
                throw new InvalidDataException("版本快照格式或实例归属不匹配。 ");
            }

            var payload = new SnapshotPayload(manifest, archive, plain);
            archive = null;
            transferredToPayload = true;
            return payload;
        }
        finally
        {
            if (!transferredToPayload)
            {
                archive?.Dispose();
                stream.Dispose();
                CryptographicOperations.ZeroMemory(plain);
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

    private static string NormalizePasswordSnapshotPath(string snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            throw new ArgumentException("跨电脑快照路径不能为空。", nameof(snapshotPath));
        }

        var path = Path.GetFullPath(snapshotPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到跨电脑密码快照。", path);
        }

        RejectReparsePoint(path, "跨电脑密码快照");
        return path;
    }

    private static string NormalizePasswordSnapshotOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("跨电脑快照导出路径不能为空。", nameof(outputPath));
        }

        var path = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("跨电脑快照没有父目录。 ");
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            RejectReparsePoint(path, "跨电脑密码快照");
        }

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

    private static void RejectExistingReparsePoint(string rootPath, string path, string label)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var current = File.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrWhiteSpace(current))
        {
            var relative = Path.GetRelativePath(root, current);
            if (Path.IsPathRooted(relative)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"快照路径越过 DSH_HOME：{label}");
            }

            if (File.Exists(current) || Directory.Exists(current))
            {
                RejectReparsePoint(current, label);
            }

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current),
                    root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{label}不能是符号链接或重解析点。 ");
        }
    }

    private static void PruneAutomaticSnapshots(string directory, string newestPath)
    {
        var automatic = Directory.EnumerateFiles(directory, "auto-*.dshsnapshot", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        var keptCount = 0;
        long keptBytes = 0;
        foreach (var file in automatic)
        {
            var isNewest = string.Equals(file.FullName, newestPath, StringComparison.OrdinalIgnoreCase);
            var keep = isNewest
                || (keptCount < MaximumAutomaticSnapshotCount
                    && keptBytes + file.Length <= MaximumAutomaticSnapshotBytes);
            if (keep)
            {
                keptCount++;
                keptBytes += file.Length;
                continue;
            }

            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temporarily locked old snapshot is retried on the next
                // automatic snapshot instead of failing the user's change.
            }
        }
    }

    private static void DeleteStagingFiles(
        string stagingRoot,
        IEnumerable<StagedSnapshotFile> files)
    {
        foreach (var file in files)
        {
            if (file.StagedPath is not null && File.Exists(file.StagedPath))
            {
                File.Delete(file.StagedPath);
            }
        }

        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: false);
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
        IReadOnlyList<string> PresentFiles,
        bool IsAutomatic = false);

    private sealed record StagedSnapshotFile(string RelativePath, string? StagedPath);

    private sealed class StagedSnapshot : IDisposable
    {
        public StagedSnapshot(string stagingRoot, IReadOnlyList<StagedSnapshotFile> files)
        {
            StagingRoot = stagingRoot;
            Files = files;
        }

        public string StagingRoot { get; }

        public IReadOnlyList<StagedSnapshotFile> Files { get; }

        public void Dispose() => DeleteStagingFiles(StagingRoot, Files);
    }

    private sealed class SnapshotPayload : IDisposable
    {
        public SnapshotPayload(SnapshotManifest manifest, ZipArchive archive, byte[] plaintext)
        {
            Manifest = manifest;
            Archive = archive;
            Plaintext = plaintext;
        }

        public SnapshotManifest Manifest { get; }

        public ZipArchive Archive { get; }

        private byte[] Plaintext { get; }

        public void Dispose()
        {
            Archive.Dispose();
            CryptographicOperations.ZeroMemory(Plaintext);
        }
    }
}
