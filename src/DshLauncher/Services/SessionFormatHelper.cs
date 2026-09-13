using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using ZstdSharp;

namespace DshLauncher.Services;

/// <summary>
/// Shared physical Session format rules. The launcher only understands the
/// released v0-v3 generations; newer generations remain visible as invalid
/// files and are never silently treated as v0.
/// </summary>
public static class SessionFormatHelper
{
    public const long CurrentSessionFormatVersion = 3;
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    private const int MaxHeaderBytes = 256_000;
    private const int ZstdReadBufferSize = 64 * 1024;
    private const string CatalogPackageName = "@deepseek-ai/dsh-session-format-catalog";
    private const string CatalogMarker = "sessionFormatCatalog";

    /// <summary>
    /// Parses the strict on-disk basename. Compression is allowed only as the
    /// lower-case physical .zstd suffix after a canonical raw basename.
    /// </summary>
    public static bool TryParseFileName(
        string? path,
        out SessionFileFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var compressed = fileName.EndsWith(".zstd", StringComparison.Ordinal);
        var rawName = compressed
            ? fileName[..^".zstd".Length]
            : fileName;
        if (!TryParseRawFileName(rawName, out var version))
        {
            return false;
        }

        format = new SessionFileFormat(version, compressed);
        return true;
    }

    /// <summary>
    /// Parses an import/export or launcher-backup name. Portable names may
    /// contain a user-selected prefix, but must still end in a JSONL suffix.
    /// Canonical names are parsed strictly and therefore carry their filename
    /// generation into header validation.
    /// </summary>
    public static bool TryParsePortableFileName(
        string? path,
        out SessionFileFormat format,
        out bool hasCanonicalGeneration)
    {
        format = default;
        hasCanonicalGeneration = false;
        if (TryParseFileName(path, out format))
        {
            hasCanonicalGeneration = true;
            return true;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        var compressed = fileName.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase);
        if (!compressed && !fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The generation of a non-canonical portable name is carried by its
        // header. Zero is the safe fallback until that header is read.
        format = new SessionFileFormat(0, compressed);
        return true;
    }

    /// <summary>
    /// Recognizes the timestamp-prefixed names produced by ConversationService
    /// backups and returns the canonical generation encoded at their suffix.
    /// </summary>
    public static bool TryParseBackupFileName(
        string? path,
        out SessionFileFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (TryParseFileName(fileName, out format))
        {
            return true;
        }

        // Backup() prefixes the original relative name with yyyyMMdd-HHmmss-.
        if (fileName.Length <= 16
            || fileName[15] != '-'
            || !DateTime.TryParseExact(
                fileName[..15],
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return false;
        }

        var originalName = fileName[16..];
        if (TryParseCanonicalSuffix(originalName, out format))
        {
            return true;
        }

        return false;
    }

    public static string GetCanonicalFileName(long version, bool compressed)
    {
        if (version < 0 || version > MaxSafeInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        var raw = version == 0
            ? "session.jsonl"
            : $"session.v{version.ToString(CultureInfo.InvariantCulture)}.jsonl";
        return compressed ? raw + ".zstd" : raw;
    }

    public static bool IsCanonicalFileName(string? path) =>
        TryParseFileName(path, out _);

    /// <summary>
    /// v0 is built into the old JSONL runtime. Historical v1+ formats require
    /// the runtime's first-party static catalog, while future versions are
    /// rejected until this launcher learns their semantics.
    /// </summary>
    public static bool IsRuntimeFormatSupported(ManagerInstance instance, long version)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (version == 0)
        {
            return true;
        }

        if (version < 0 || version > CurrentSessionFormatVersion)
        {
            return false;
        }

        return FindCatalogSupportedVersion(instance) >= version;
    }

    public static bool HasFormatCatalog(ManagerInstance instance) =>
        FindCatalogSupportedVersion(instance) > 0;

    internal static bool TryReadHeader(string path, out SessionHeaderInfo header)
    {
        header = default!;
        try
        {
            var hasCanonicalGeneration = TryParseFileName(path, out var physicalFormat)
                || TryParseBackupFileName(path, out physicalFormat);
            using var source = File.OpenRead(path);
            string? line;
            if (physicalFormat.IsCompressed
                || (!hasCanonicalGeneration
                    && path.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase)))
            {
                using var decompressor = new DecompressionStream(
                    source,
                    ZstdReadBufferSize,
                    checkEndOfStream: false,
                    leaveOpen: false);
                line = ReadHeaderLine(decompressor);
            }
            else
            {
                line = ReadHeaderLine(source);
            }

            if (!TryParseHeader(line, out header))
            {
                return false;
            }

            if (hasCanonicalGeneration && physicalFormat.Version != header.Version)
            {
                header = default!;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException
            or ZstdException
            or IOException
            or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            header = default!;
            return false;
        }
    }

    internal static bool TryParseHeader(string? line, out SessionHeaderInfo header)
    {
        header = default!;
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxHeaderBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "session", StringComparison.Ordinal)
                || !root.TryGetProperty("version", out var version)
                || !TryGetSafeInteger(version, out var versionNumber)
                || versionNumber > CurrentSessionFormatVersion
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())
                || !root.TryGetProperty("createdAt", out var createdAt)
                || !IsSafeNonNegativeInteger(createdAt)
                || !root.TryGetProperty("delegationDepth", out var delegationDepth)
                || !IsSafeNonNegativeInteger(delegationDepth))
            {
                return false;
            }

            if (root.TryGetProperty("origin", out var origin)
                && (origin.ValueKind != JsonValueKind.String
                    || !string.Equals(origin.GetString(), "subagent", StringComparison.Ordinal)))
            {
                return false;
            }

            if (root.TryGetProperty("agentPreset", out var agentPreset)
                && agentPreset.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            // These values describe the process which will execute the
            // conversation and must never be imported from a session file.
            if (root.TryGetProperty("sandboxMode", out _)
                || root.TryGetProperty("approvalPolicy", out _))
            {
                return false;
            }

            var sessionId = id.GetString()!;
            if (sessionId.Length > 256 || sessionId.Any(char.IsControl))
            {
                return false;
            }

            if (root.TryGetProperty("cwd", out var cwdValue)
                && cwdValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var cwd = root.TryGetProperty("cwd", out cwdValue)
                ? cwdValue.GetString()
                : null;
            if (cwd is not null && (cwd.Length > 4096 || cwd.Any(char.IsControl)))
            {
                return false;
            }

            header = new SessionHeaderInfo(versionNumber, sessionId, cwd);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseRawFileName(string fileName, out long version)
    {
        version = 0;
        if (fileName.Equals("session.jsonl", StringComparison.Ordinal))
        {
            return true;
        }

        const string prefix = "session.v";
        const string suffix = ".jsonl";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = fileName[prefix.Length..^suffix.Length];
        if (digits.Length == 0 || digits[0] == '0'
            || digits.Any(character => character is < '0' or > '9')
            || !long.TryParse(
                digits,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version))
        {
            return false;
        }

        return version <= MaxSafeInteger;
    }

    private static bool TryParseCanonicalSuffix(
        string fileName,
        out SessionFileFormat format)
    {
        format = default;
        var compressed = fileName.EndsWith(".zstd", StringComparison.Ordinal);
        var rawName = compressed
            ? fileName[..^".zstd".Length]
            : fileName;
        for (var index = 0; index < rawName.Length; index++)
        {
            if (rawName[index] != 's'
                || (index > 0 && rawName[index - 1] is not ('_' or '-'))
                || !rawName[index..].StartsWith("session", StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = rawName[index..];
            if (TryParseRawFileName(candidate, out var version))
            {
                format = new SessionFileFormat(version, compressed);
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSafeInteger(JsonElement value, out long number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out number))
        {
            return false;
        }

        return number >= 0 && number <= MaxSafeInteger;
    }

    private static bool IsSafeNonNegativeInteger(JsonElement value) =>
        TryGetSafeInteger(value, out _);

    private static string? ReadHeaderLine(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length <= MaxHeaderBytes)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline + 1 : read;
            if (buffer.Length + count > MaxHeaderBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, count);
            if (newline >= 0)
            {
                break;
            }
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        var line = new UTF8Encoding(false, true)
            .GetString(buffer.ToArray())
            .TrimEnd('\r', '\n');
        return line.Length > 0 && line[0] == '\uFEFF' ? line[1..] : line;
    }

    private static long FindCatalogSupportedVersion(ManagerInstance instance)
    {
        foreach (var root in RuntimeRoots(instance))
        {
            foreach (var nodeModules in NodeModulesRoots(root))
            {
                var catalogRoot = Path.Combine(
                    nodeModules,
                    "@deepseek-ai",
                    "dsh-session-format-catalog");
                var supported = ReadCatalogSupportedVersion(catalogRoot);
                if (supported > 0)
                {
                    return supported;
                }
            }
        }

        return 0;
    }

    private static IEnumerable<string> RuntimeRoots(ManagerInstance instance)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        try
        {
            var packageRoot = DshRuntimeDetector.TryResolvePackageRoot(instance.RootPath);
            if (!string.IsNullOrWhiteSpace(packageRoot))
            {
                candidates.Add(packageRoot);
            }

            // Packaged runtimes may persist a stale RootPath while their
            // launch spec still points at the validated desktop host/entry.
            // Resolve only those exact paths to a DSh package root; never scan
            // their generic ancestors or DSH_HOME for an unrelated catalog.
            if (instance.EffectiveDshLaunchSpec is { } launchSpec)
            {
                foreach (var launchPath in new[] { launchSpec.HostPath, launchSpec.EntryPointPath })
                {
                    if (string.IsNullOrWhiteSpace(launchPath))
                    {
                        continue;
                    }

                    var resolved = DshRuntimeDetector.TryFindPackageRoot(launchPath);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        candidates.Add(resolved);
                    }
                }
            }
        }
        catch
        {
            // Runtime probing is only a capability hint. A malformed runtime
            // must not make conversation listing itself fail.
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string normalized;
            try
            {
                // pnpm exposes DSh through a link. Node resolves dependencies
                // from the physical package directory, not the public alias.
                normalized = ResolveLinkTarget(candidate) ?? Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string> NodeModulesRoots(string root)
    {
        var current = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        for (var depth = 0; depth < 10 && !string.IsNullOrEmpty(current); depth++)
        {
            if (Path.GetFileName(current).Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                yield return current;
            }

            yield return Path.Combine(current, "node_modules");
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = parent ?? string.Empty;
        }
    }

    private static long ReadCatalogSupportedVersion(string catalogRoot)
    {
        try
        {
            var resolvedCatalogRoot = ResolveLinkTarget(catalogRoot);
            if (resolvedCatalogRoot is null || !Directory.Exists(resolvedCatalogRoot))
            {
                return 0;
            }

            var manifestPath = Path.Combine(resolvedCatalogRoot, "package.json");
            if (!File.Exists(manifestPath))
            {
                return 0;
            }

            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !string.Equals(name.GetString(), CatalogPackageName, StringComparison.Ordinal)
                || !root.TryGetProperty("main", out var main)
                || main.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(main.GetString()))
            {
                return 0;
            }

            var mainPath = Path.GetFullPath(Path.Combine(resolvedCatalogRoot, main.GetString()!));
            if (!IsInsideDirectory(mainPath, resolvedCatalogRoot)
                || !File.Exists(mainPath))
            {
                return 0;
            }

            var source = File.ReadAllText(mainPath, Encoding.UTF8);
            if (!source.Contains(CatalogMarker, StringComparison.Ordinal))
            {
                return 0;
            }

            // The generated catalog imports one codec per released generation.
            // Requiring the generated markers makes an arbitrary package named
            // like the catalog insufficient proof of runtime capability.
            var supported = source.Contains("releasedV1SessionFormatCodec", StringComparison.Ordinal)
                && source.Contains("sessionFormatV0ToV1", StringComparison.Ordinal)
                ? 1L
                : 0L;
            if (supported >= 1
                && source.Contains("releasedV2SessionFormatCodec", StringComparison.Ordinal)
                && source.Contains("sessionFormatV1ToV2", StringComparison.Ordinal))
            {
                supported = 2;
            }

            if (supported >= 2
                && source.Contains("releasedV3SessionFormatCodec", StringComparison.Ordinal)
                && source.Contains("sessionFormatV2ToV3", StringComparison.Ordinal))
            {
                supported = 3;
            }

            return supported;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            return 0;
        }
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return Path.GetFullPath(path);
            }

            var directory = new DirectoryInfo(path);
            return directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

}

public readonly record struct SessionFileFormat(long Version, bool IsCompressed);

internal sealed record SessionHeaderInfo(
    long Version,
    string SessionId,
    string? WorkingDirectory);
