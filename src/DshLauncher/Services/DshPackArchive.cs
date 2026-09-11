using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DshLauncher.Services;

/// <summary>读取整合包的结果（失败原因分类，便于界面给出可操作提示）。</summary>
public enum PackArchiveOutcome
{
    Ok,

    /// <summary>不是可识别的归档（既非 ZIP 也非 gzip）。</summary>
    NotAnArchive,

    /// <summary>容器层被拒：缺 dspack.json / format 不对 / 版本不支持 / 与 manifest 配对失败。</summary>
    ContainerRejected,

    /// <summary>manifest.json 缺失或解析/字段校验失败。</summary>
    ManifestRejected,

    /// <summary>条目数 / 单条 / 总大小超限。</summary>
    LimitsExceeded,

    /// <summary>存在越界或非法路径的条目（zip-slip 防护）。</summary>
    EntryPathRejected,

    /// <summary>归档本身读取失败（损坏、IO 错误）。</summary>
    ReadFailed
}

/// <summary>归档内一个条目的清单信息（不含内容）。</summary>
public sealed record PackArchiveEntry(string Path, long Size);

/// <summary>
/// 读取并校验整合包容器后的只读视图。只做识别、清单与文本条目读取；
/// 不解压到磁盘、不联网、不写任何数据（解压与安装属于后续步骤）。
/// </summary>
public sealed class PackArchive
{
    internal PackArchive(
        string sourcePath,
        PackContainerKind container,
        PackManifest manifest,
        IReadOnlyList<PackArchiveEntry> entries,
        bool hasPackageJson,
        bool hasPnpmLock,
        bool hasPnpmWorkspace,
        bool hasCordisPatch)
    {
        SourcePath = sourcePath;
        Container = container;
        Manifest = manifest;
        Entries = entries;
        HasPackageJson = hasPackageJson;
        HasPnpmLock = hasPnpmLock;
        HasPnpmWorkspace = hasPnpmWorkspace;
        HasCordisPatch = hasCordisPatch;
    }

    public string SourcePath { get; }

    public PackContainerKind Container { get; }

    public PackManifest Manifest { get; }

    public IReadOnlyList<PackArchiveEntry> Entries { get; }

    /// <summary>可选快照：package.json（导入时由 manifest 权威重建）。</summary>
    public bool HasPackageJson { get; }

    public bool HasPnpmLock { get; }

    public bool HasPnpmWorkspace { get; }

    /// <summary>旧 .tgz 的扁平 cordis.patch.yml（v4 起改由 overrides/ 携带）。</summary>
    public bool HasCordisPatch { get; }

    /// <summary>容器里 overrides/ 下的条目（相对 overrides/ 的路径）。</summary>
    public IReadOnlyList<string> OverridePaths =>
        Entries.Select(entry => entry.Path)
            .Where(path => path.StartsWith(PackFormat.OverridesDirectoryName + "/", StringComparison.Ordinal))
            .Select(path => path[(PackFormat.OverridesDirectoryName.Length + 1)..])
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>v5 profile 形态可携带的 home/ 覆盖条目（相对 home/ 的路径）。</summary>
    public IReadOnlyList<string> HomePaths =>
        Entries.Select(entry => entry.Path)
            .Where(path => path.StartsWith(PackFormat.HomeDirectoryName + "/", StringComparison.Ordinal))
            .Select(path => path[(PackFormat.HomeDirectoryName.Length + 1)..])
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>总大小（清单求和）。</summary>
    public long TotalSize => Entries.Sum(entry => entry.Size);

    /// <summary>按清单里的相对路径取文本条目（用于 manifest/package.json/patch 等小文本）。</summary>
    public bool TryReadTextEntry(string entryPath, out string? text, out string? error)
    {
        text = null;
        error = null;
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            error = "条目路径为空。";
            return false;
        }

        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        var entry = Entries.FirstOrDefault(candidate => string.Equals(candidate.Path, normalized, StringComparison.Ordinal));
        if (entry is null)
        {
            error = $"归档中没有条目：{normalized}";
            return false;
        }

        if (entry.Size > PackArchiveLimits.MaximumTextEntryBytes)
        {
            error = $"条目过大，不作为文本读取：{normalized}";
            return false;
        }

        try
        {
            using var stream = OpenEntryStream(normalized, out var openError);
            if (stream is null)
            {
                error = openError;
                return false;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = $"读取条目失败：{ex.Message}";
            return false;
        }
    }

    private Stream? OpenEntryStream(string normalizedPath, out string? error)
    {
        error = null;
        switch (Container)
        {
            case PackContainerKind.DspackV2:
            case PackContainerKind.DspackV3:
            {
                var archive = ZipFile.OpenRead(SourcePath);
                var entry = archive.Entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullName.Replace('\\', '/'), normalizedPath, StringComparison.Ordinal));
                if (entry is null)
                {
                    archive.Dispose();
                    error = $"归档中没有条目：{normalizedPath}";
                    return null;
                }

                // 让 ZipArchive 随流一起释放。
                return new EntryStream(entry.Open(), archive);
            }

            default:
            {
                using var file = File.OpenRead(SourcePath);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (tar.GetNextEntry() is { } tarEntry)
                {
                    var name = NormalizeTarEntryName(tarEntry.Name);
                    var dataStream = tarEntry.DataStream;
                    if (!string.Equals(name, normalizedPath, StringComparison.Ordinal)
                        || dataStream is null
                        || !dataStream.CanRead)
                    {
                        continue;
                    }

                    var buffer = new MemoryStream();
                    dataStream.CopyTo(buffer);
                    buffer.Position = 0;
                    return buffer;
                }

                error = $"归档中没有条目：{normalizedPath}";
                return null;
            }
        }
    }

    internal static string NormalizeTarEntryName(string name)
    {
        var value = name.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.TrimEnd('/');
    }

    private sealed class EntryStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>读取整合包的资源上限（沿用本仓既有口径：条目数 / 单条 / 总大小）。</summary>
public static class PackArchiveLimits
{
    public const int MaximumEntries = 4096;

    public const long MaximumEntryBytes = 32L * 1024 * 1024;

    public const long MaximumTotalBytes = 256L * 1024 * 1024;

    /// <summary>允许整体读进内存的文本条目上限（manifest / package.json / patch）。</summary>
    public const long MaximumTextEntryBytes = 4L * 1024 * 1024;
}

/// <summary>
/// 整合包读取器：识别容器（.dspack 的 ZIP 或旧 .tgz）、校验清单与路径安全、解析 manifest。
/// </summary>
public static class PackArchiveReader
{
    private static readonly string[] OptionalRootFiles =
    {
        "package.json",
        "pnpm-lock.yaml",
        "pnpm-workspace.yaml",
        "cordis.patch.yml"
    };

    /// <summary>读取并校验整合包；失败时给出可直接展示的原因。</summary>
    public static bool TryRead(string filePath, out PackArchive? archive, out PackArchiveOutcome outcome, out string? error)
    {
        archive = null;
        outcome = PackArchiveOutcome.ReadFailed;
        error = null;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            error = "文件不存在。";
            return false;
        }

        byte[] header;
        try
        {
            using var stream = File.OpenRead(filePath);
            header = new byte[4];
            var read = stream.Read(header, 0, header.Length);
            if (read < header.Length)
            {
                header = header[..read];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取文件失败：{ex.Message}";
            return false;
        }

        if (PackFormat.HasZipHeader(header))
        {
            return TryReadZip(filePath, out archive, out outcome, out error);
        }

        if (PackFormat.HasGzipHeader(header))
        {
            return TryReadTgz(filePath, out archive, out outcome, out error);
        }

        outcome = PackArchiveOutcome.NotAnArchive;
        error = "既不是 .dspack（ZIP）也不是旧的 .tgz（gzip），无法识别为整合包。";
        return false;
    }

    private static bool TryReadZip(string filePath, out PackArchive? archive, out PackArchiveOutcome outcome, out string? error)
    {
        archive = null;
        outcome = PackArchiveOutcome.ReadFailed;
        error = null;
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(filePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            error = $"ZIP 打开失败：{ex.Message}";
            return false;
        }

        using (zip)
        {
            var entries = new List<PackArchiveEntry>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.EndsWith('/'))
                {
                    continue; // 目录条目不进清单
                }

                if (!PackFormat.IsSafeRelativePath(path))
                {
                    outcome = PackArchiveOutcome.EntryPathRejected;
                    error = $"归档内存在非法路径的条目：{path}";
                    return false;
                }

                if (entry.Length > PackArchiveLimits.MaximumEntryBytes)
                {
                    outcome = PackArchiveOutcome.LimitsExceeded;
                    error = $"单条超过上限（{PackArchiveLimits.MaximumEntryBytes / 1024 / 1024} MB）：{path}";
                    return false;
                }

                total += entry.Length;
                if (total > PackArchiveLimits.MaximumTotalBytes)
                {
                    outcome = PackArchiveOutcome.LimitsExceeded;
                    error = $"解包总大小超过上限（{PackArchiveLimits.MaximumTotalBytes / 1024 / 1024} MB）。";
                    return false;
                }

                if (entries.Count >= PackArchiveLimits.MaximumEntries)
                {
                    outcome = PackArchiveOutcome.LimitsExceeded;
                    error = $"条目数超过上限（{PackArchiveLimits.MaximumEntries}）。";
                    return false;
                }

                entries.Add(new PackArchiveEntry(path, entry.Length));
            }

            var markerEntry = zip.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullName.Replace('\\', '/'), PackFormat.ContainerMarkerFileName, StringComparison.Ordinal));
            if (markerEntry is null)
            {
                outcome = PackArchiveOutcome.ContainerRejected;
                error = $"ZIP 根部缺少 {PackFormat.ContainerMarkerFileName}，不是 .dspack 容器。";
                return false;
            }

            string markerText;
            string? manifestText;
            try
            {
                using var markerStream = markerEntry.Open();
                using var markerReader = new StreamReader(markerStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                markerText = markerReader.ReadToEnd();

                var manifestEntry = zip.Entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullName.Replace('\\', '/'), PackFormat.ManifestFileName, StringComparison.Ordinal));
                if (manifestEntry is null)
                {
                    outcome = PackArchiveOutcome.ManifestRejected;
                    error = $"归档缺少 {PackFormat.ManifestFileName}。";
                    return false;
                }

                if (manifestEntry.Length > PackArchiveLimits.MaximumTextEntryBytes)
                {
                    outcome = PackArchiveOutcome.LimitsExceeded;
                    error = $"{PackFormat.ManifestFileName} 过大。";
                    return false;
                }

                using var manifestStream = manifestEntry.Open();
                using var manifestReader = new StreamReader(manifestStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                manifestText = manifestReader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                error = $"读取容器内容失败：{ex.Message}";
                return false;
            }

            if (!PackFormat.TryParseContainerMarker(markerText, out var container, out var markerError))
            {
                outcome = PackArchiveOutcome.ContainerRejected;
                error = markerError;
                return false;
            }

            if (!PackFormat.TryParseManifest(manifestText, out var manifest, out var manifestError))
            {
                outcome = PackArchiveOutcome.ManifestRejected;
                error = manifestError;
                return false;
            }

            if (!PackFormat.ValidatePairing(manifest!, container, out var pairingError))
            {
                outcome = PackArchiveOutcome.ContainerRejected;
                error = pairingError;
                return false;
            }

            return Build(filePath, container, manifest!, entries, out archive, out outcome, out error);
        }
    }

    private static bool TryReadTgz(string filePath, out PackArchive? archive, out PackArchiveOutcome outcome, out string? error)
    {
        archive = null;
        outcome = PackArchiveOutcome.ReadFailed;
        error = null;
        var entries = new List<PackArchiveEntry>();
        long total = 0;
        string? manifestText = null;
        try
        {
            using var file = File.OpenRead(filePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory)
                {
                    continue;
                }

                var path = PackArchive.NormalizeTarEntryName(entry.Name);
                if (path.Length == 0)
                {
                    continue;
                }

                if (!PackFormat.IsSafeRelativePath(path))
                {
                    outcome = PackArchiveOutcome.EntryPathRejected;
                    error = $"归档内存在非法路径的条目：{path}";
                    return false;
                }

                var size = entry.Length;
                if (size > PackArchiveLimits.MaximumEntryBytes)
                {
                    outcome = PackArchiveOutcome.LimitsExceeded;
                    error = $"单条超过上限（{PackArchiveLimits.MaximumEntryBytes / 1024 / 1024} MB）：{path}";
                    return false;
                }

                total += size;
                if (total > PackArchiveLimits.MaximumTotalBytes)
                {
                    outcome = PackArchiveOutcome.LimitsExceeded;
                    error = $"解包总大小超过上限（{PackArchiveLimits.MaximumTotalBytes / 1024 / 1024} MB）。";
                    return false;
                }

                if (entries.Count >= PackArchiveLimits.MaximumEntries)
                {
                    outcome = PackArchiveOutcome.LimitsExceeded;
                    error = $"条目数超过上限（{PackArchiveLimits.MaximumEntries}）。";
                    return false;
                }

                entries.Add(new PackArchiveEntry(path, size));

                if (string.Equals(path, PackFormat.ManifestFileName, StringComparison.Ordinal)
                    && entry.DataStream is { } dataStream
                    && size <= PackArchiveLimits.MaximumTextEntryBytes)
                {
                    // leaveOpen: tar 的条目流由 TarReader 管理，提前释放会让后续 GetNextEntry 抛 ObjectDisposedException。
                    using var reader = new StreamReader(
                        dataStream,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true,
                        bufferSize: 1024,
                        leaveOpen: true);
                    manifestText = reader.ReadToEnd();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = $"读取 .tgz 失败：{ex.Message}";
            return false;
        }

        if (manifestText is null)
        {
            outcome = PackArchiveOutcome.ManifestRejected;
            error = $".tgz 缺少可读取的 {PackFormat.ManifestFileName}。";
            return false;
        }

        if (!PackFormat.TryParseManifest(manifestText, out var manifest, out var manifestError))
        {
            outcome = PackArchiveOutcome.ManifestRejected;
            error = manifestError;
            return false;
        }

        return Build(filePath, PackContainerKind.LegacyTgz, manifest!, entries, out archive, out outcome, out error);
    }

    private static bool Build(
        string filePath,
        PackContainerKind container,
        PackManifest manifest,
        List<PackArchiveEntry> entries,
        out PackArchive? archive,
        out PackArchiveOutcome outcome,
        out string? error)
    {
        outcome = PackArchiveOutcome.Ok;
        error = null;
        var paths = entries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        archive = new PackArchive(
            filePath,
            container,
            manifest,
            entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray(),
            paths.Contains(OptionalRootFiles[0]),
            paths.Contains(OptionalRootFiles[1]),
            paths.Contains(OptionalRootFiles[2]),
            paths.Contains(OptionalRootFiles[3]));
        return true;
    }
}
