using System.IO;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Services;

public sealed record DshHomeImportResult(
    bool Imported,
    int FileCount,
    long TotalBytes,
    string? SourceHome)
{
    public static DshHomeImportResult NoData(string? sourceHome = null) =>
        new(false, 0, 0, sourceHome);
}

public sealed class DshHomeImportService
{
    private const string LauncherMetadataDirectory = ".dsh-launcher";

    public static string? ResolveCurrentDshHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_HOME");
        string candidate;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return null;
            }

            candidate = Path.Combine(userProfile, ".dsh");
        }
        else
        {
            candidate = ExpandHome(configured.Trim());
        }

        try
        {
            var normalized = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(normalized) && !IsReparsePoint(normalized)
                ? normalized
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task<DshHomeImportResult> ImportAsync(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(sourceHome, destinationHome, cancellationToken), cancellationToken);

    public Task<DshHomeImportResult> BackfillLegacyImportAsync(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => BackfillLegacyImport(sourceHome, destinationHome, cancellationToken), cancellationToken);

    public Task<DshHomeImportResult> RefreshImportAsync(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RefreshImport(sourceHome, destinationHome, cancellationToken), cancellationToken);

    public Task<int> RestoreProfilePackagesAsync(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RestoreProfilePackages(sourceHome, destinationHome, cancellationToken), cancellationToken);

    private static DshHomeImportResult Import(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceHome) || !Directory.Exists(sourceHome))
        {
            return DshHomeImportResult.NoData(sourceHome);
        }

        var source = Path.GetFullPath(sourceHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(destinationHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            || IsPathInside(source, destination)
            || IsPathInside(destination, source))
        {
            throw new InvalidOperationException("导入源和新实例的 DSH_HOME 不能互相包含。 ");
        }

        if (IsReparsePoint(source))
        {
            throw new IOException("现有 DSH_HOME 不能是符号链接或重解析点。 ");
        }

        Directory.CreateDirectory(destination);
        if (IsReparsePoint(destination))
        {
            throw new IOException("新实例的 DSH_HOME 不能是符号链接或重解析点。 ");
        }

        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException("新实例的 DSH_HOME 不是空目录，已停止导入以避免覆盖数据。 ");
        }

        var state = new CopyState();
        try
        {
            CopyDirectory(source, destination, relativePath: string.Empty, state, cancellationToken);
            NormalizeCredentialStore(Path.Combine(destination, ".credentials.yaml"));
            RestoreProfilePackages(source, destination, cancellationToken);
            return state.FileCount == 0
                ? DshHomeImportResult.NoData(source)
                : new DshHomeImportResult(true, state.FileCount, state.TotalBytes, source);
        }
        catch
        {
            try
            {
                ClearGeneratedDirectory(destination);
            }
            catch
            {
                // Preserve the original import failure.
            }

            throw;
        }
    }

    private static DshHomeImportResult BackfillLegacyImport(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceHome)
            || !Directory.Exists(sourceHome)
            || !Directory.Exists(destinationHome))
        {
            return DshHomeImportResult.NoData(sourceHome);
        }

        var source = Path.GetFullPath(sourceHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(destinationHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            || IsPathInside(source, destination)
            || IsPathInside(destination, source)
            || IsReparsePoint(source)
            || IsReparsePoint(destination))
        {
            return DshHomeImportResult.NoData(source);
        }

        var sourceSessions = Path.Combine(source, "sessions");
        var destinationSessions = Path.Combine(destination, "sessions");
        var sourceWorkspace = Path.Combine(source, "storages", "workspace.json");
        var destinationWorkspace = Path.Combine(destination, "storages", "workspace.json");
        var sourceCredentials = Path.Combine(source, ".credentials.yaml");
        var destinationCredentials = Path.Combine(destination, ".credentials.yaml");
        var sourceHasUserData = ContainsRegularFile(sourceSessions)
            || !IsWorkspaceStoreEmpty(sourceWorkspace)
            || ReadTopLevelYamlKeys(sourceCredentials).Count > 0;
        var destinationIsUninitialized = !ContainsRegularFile(destinationSessions)
            && IsWorkspaceStoreEmpty(destinationWorkspace);
        if (!sourceHasUserData || !destinationIsUninitialized)
        {
            return DshHomeImportResult.NoData(source);
        }

        var state = new CopyState();
        CopyMissingDirectory(source, destination, string.Empty, state, cancellationToken);
        RestoreProfilePackages(source, destination, cancellationToken);
        if (File.Exists(sourceWorkspace)
            && !IsWorkspaceStoreEmpty(sourceWorkspace)
            && IsWorkspaceStoreEmpty(destinationWorkspace))
        {
            CopyFileAtomically(sourceWorkspace, destinationWorkspace, overwrite: true, cancellationToken);
            state.FileCount++;
            state.TotalBytes += new FileInfo(sourceWorkspace).Length;
        }

        MergeMissingCredentials(sourceCredentials, destinationCredentials, state, cancellationToken);
        return state.FileCount == 0
            ? DshHomeImportResult.NoData(source)
            : new DshHomeImportResult(true, state.FileCount, state.TotalBytes, source);
    }

    private static DshHomeImportResult RefreshImport(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceHome) || !Directory.Exists(sourceHome))
        {
            return DshHomeImportResult.NoData(sourceHome);
        }

        var source = Path.GetFullPath(sourceHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(destinationHome)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            || IsPathInside(source, destination)
            || IsPathInside(destination, source))
        {
            throw new InvalidOperationException("导入源和实例的 DSH_HOME 不能互相包含。 ");
        }

        if (IsReparsePoint(source))
        {
            throw new IOException("现有 DSH_HOME 不能是符号链接或重解析点。 ");
        }

        Directory.CreateDirectory(destination);
        if (IsReparsePoint(destination))
        {
            throw new IOException("实例 DSH_HOME 不能是符号链接或重解析点。 ");
        }

        var state = new CopyState();
        CopyDirectoryOverwrite(source, destination, relativePath: string.Empty, state, cancellationToken);
        NormalizeCredentialStore(Path.Combine(destination, ".credentials.yaml"));
        var restoredPackages = RestoreProfilePackages(
            source,
            destination,
            cancellationToken,
            overwriteExisting: true);
        return state.FileCount == 0 && restoredPackages == 0
            ? DshHomeImportResult.NoData(source)
            : new DshHomeImportResult(true, state.FileCount, state.TotalBytes, source);
    }

    private static void CopyDirectory(
        string sourceRoot,
        string destinationRoot,
        string relativePath,
        CopyState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceDirectory = string.IsNullOrEmpty(relativePath)
            ? sourceRoot
            : Path.Combine(sourceRoot, relativePath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry))
            {
                continue;
            }

            var name = Path.GetFileName(entry);
            var childRelative = string.IsNullOrEmpty(relativePath)
                ? name
                : Path.Combine(relativePath, name);
            if (string.IsNullOrEmpty(relativePath)
                && (string.Equals(name, LauncherMetadataDirectory, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "webview2", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (ContainsNodeModulesSegment(childRelative))
            {
                continue;
            }

            var destination = Path.Combine(destinationRoot, childRelative);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(destination);
                CopyDirectory(sourceRoot, destinationRoot, childRelative, state, cancellationToken);
                continue;
            }

            CopyFile(entry, destination, cancellationToken);
            var file = new FileInfo(entry);
            state.FileCount++;
            state.TotalBytes += file.Length;
        }
    }

    private static void CopyFile(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("导入文件没有父目录。 "));
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
        }

        try
        {
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Timestamps are optional; the imported bytes are already complete.
        }
    }

    private static void CopyMissingDirectory(
        string sourceRoot,
        string destinationRoot,
        string relativePath,
        CopyState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceDirectory = string.IsNullOrEmpty(relativePath)
            ? sourceRoot
            : Path.Combine(sourceRoot, relativePath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry))
            {
                continue;
            }

            var name = Path.GetFileName(entry);
            var childRelative = string.IsNullOrEmpty(relativePath)
                ? name
                : Path.Combine(relativePath, name);
            if (string.IsNullOrEmpty(relativePath)
                && (string.Equals(name, LauncherMetadataDirectory, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ".credentials.yaml", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "webview2", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (ContainsNodeModulesSegment(childRelative)
                || string.Equals(
                    childRelative,
                    Path.Combine("storages", "workspace.json"),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(destinationRoot, childRelative);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(destination);
                CopyMissingDirectory(sourceRoot, destinationRoot, childRelative, state, cancellationToken);
                continue;
            }

            if (File.Exists(destination))
            {
                continue;
            }

            CopyFile(entry, destination, cancellationToken);
            var file = new FileInfo(entry);
            state.FileCount++;
            state.TotalBytes += file.Length;
        }
    }

    private static void CopyDirectoryOverwrite(
        string sourceRoot,
        string destinationRoot,
        string relativePath,
        CopyState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceDirectory = string.IsNullOrEmpty(relativePath)
            ? sourceRoot
            : Path.Combine(sourceRoot, relativePath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry))
            {
                continue;
            }

            var name = Path.GetFileName(entry);
            var childRelative = string.IsNullOrEmpty(relativePath)
                ? name
                : Path.Combine(relativePath, name);
            if (string.IsNullOrEmpty(relativePath)
                && (string.Equals(name, LauncherMetadataDirectory, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "webview2", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (ContainsNodeModulesSegment(childRelative))
            {
                continue;
            }

            var destination = Path.Combine(destinationRoot, childRelative);
            if (Directory.Exists(entry))
            {
                if (File.Exists(destination) || IsReparsePoint(destination))
                {
                    throw new IOException($"实例中的目标路径不能安全覆盖：{childRelative}");
                }

                Directory.CreateDirectory(destination);
                CopyDirectoryOverwrite(sourceRoot, destinationRoot, childRelative, state, cancellationToken);
                continue;
            }

            if (Directory.Exists(destination) || IsReparsePoint(destination))
            {
                throw new IOException($"实例中的目标路径不能安全覆盖：{childRelative}");
            }

            CopyFileAtomically(entry, destination, overwrite: true, cancellationToken);
            var file = new FileInfo(entry);
            state.FileCount++;
            state.TotalBytes += file.Length;
        }
    }

    private static int RestoreProfilePackages(
        string? sourceHome,
        string destinationHome,
        CancellationToken cancellationToken,
        bool overwriteExisting = false)
    {
        if (string.IsNullOrWhiteSpace(sourceHome)
            || !Directory.Exists(sourceHome)
            || !Directory.Exists(destinationHome))
        {
            return 0;
        }

        string source;
        string destination;
        try
        {
            source = Path.GetFullPath(sourceHome)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            destination = Path.GetFullPath(destinationHome)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return 0;
        }

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            || IsReparsePoint(source)
            || IsReparsePoint(destination))
        {
            return 0;
        }

        var destinationProfilesRoot = Path.Combine(destination, "profiles");
        if (!Directory.Exists(destinationProfilesRoot) || IsReparsePoint(destinationProfilesRoot))
        {
            return 0;
        }

        var restored = 0;
        foreach (var destinationProfile in Directory.EnumerateDirectories(
                     destinationProfilesRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(destinationProfile)
                || !DshProfileService.TryNormalizeName(Path.GetFileName(destinationProfile), out var profileName))
            {
                continue;
            }

            var manifest = Path.Combine(destinationProfile, "package.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            var packageNames = ReadProfilePackageNames(manifest);
            ReadCordisPackageNames(Path.Combine(destinationProfile, "cordis.patch.yml"), packageNames);
            foreach (var packageName in packageNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceProfilePackage = BuildPackagePath(
                    Path.Combine(source, "profiles", profileName, "node_modules"),
                    packageName);
                var sourceSharedPackage = BuildPackagePath(
                    Path.Combine(source, "profiles", "node_modules"),
                    packageName);
                var sourcePackage = Directory.Exists(sourceProfilePackage) && !IsReparsePoint(sourceProfilePackage)
                    ? sourceProfilePackage
                    : Directory.Exists(sourceSharedPackage) && !IsReparsePoint(sourceSharedPackage)
                        ? sourceSharedPackage
                        : null;
                if (sourcePackage is null)
                {
                    continue;
                }

                var destinationModules = string.Equals(
                    sourcePackage,
                    sourceProfilePackage,
                    StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(destinationProfile, "node_modules")
                        : Path.Combine(destination, "profiles", "node_modules");
                if (Directory.Exists(destinationModules) && IsReparsePoint(destinationModules))
                {
                    continue;
                }

                var destinationPackage = BuildPackagePath(destinationModules, packageName);
                if (Directory.Exists(destinationPackage) && !overwriteExisting)
                {
                    continue;
                }

                CopyPackageDirectoryAtomically(
                    sourcePackage,
                    destinationPackage,
                    cancellationToken,
                    overwriteExisting);
                restored++;
            }
        }

        return restored;
    }

    private static HashSet<string> ReadProfilePackageNames(string manifestPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (document.RootElement.TryGetProperty("dependencies", out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    AddSafePackageName(result, dependency.Name);
                }
            }

            if (document.RootElement.TryGetProperty("dsh", out var dsh)
                && dsh.ValueKind == JsonValueKind.Object
                && dsh.TryGetProperty("profile", out var profile)
                && profile.ValueKind == JsonValueKind.Object
                && profile.TryGetProperty("bundles", out var bundles)
                && bundles.ValueKind == JsonValueKind.Array)
            {
                foreach (var bundle in bundles.EnumerateArray())
                {
                    if (bundle.ValueKind == JsonValueKind.String)
                    {
                        AddSafePackageName(result, bundle.GetString());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return result;
    }

    private static void ReadCordisPackageNames(string patchPath, ISet<string> packageNames)
    {
        if (!File.Exists(patchPath) || IsReparsePoint(patchPath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(patchPath, Encoding.UTF8))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("name:", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = trimmed["name:".Length..].Trim().Trim('\'', '"');
                AddSafePackageName(packageNames, value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AddSafePackageName(ISet<string> packageNames, string? packageName)
    {
        if (IsSafePackageName(packageName)
            && !packageName!.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase))
        {
            packageNames.Add(packageName);
        }
    }

    private static bool IsSafePackageName(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) || packageName.Length > 214)
        {
            return false;
        }

        var segments = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if ((packageName[0] == '@' && segments.Length != 2)
            || (packageName[0] != '@' && segments.Length != 1))
        {
            return false;
        }

        return segments.All(segment => segment.Length > 0
            && segment is not "." and not ".."
            && segment.All(character => char.IsLetterOrDigit(character)
                || character is '@' or '-' or '_' or '.'));
    }

    private static string BuildPackagePath(string modulesDirectory, string packageName)
    {
        var result = modulesDirectory;
        foreach (var segment in packageName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            result = Path.Combine(result, segment);
        }

        return result;
    }

    private static void CopyPackageDirectoryAtomically(
        string source,
        string destination,
        CancellationToken cancellationToken,
        bool overwriteExisting = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Plugin 包没有父目录。 "));
        var temporary = $"{destination}.{Guid.NewGuid():N}.import";
        var backup = $"{destination}.{Guid.NewGuid():N}.backup";
        var movedExisting = false;
        try
        {
            Directory.CreateDirectory(temporary);
            CopyPackageDirectory(source, temporary, cancellationToken);
            if (Directory.Exists(destination))
            {
                if (!overwriteExisting)
                {
                    return;
                }

                if (IsReparsePoint(destination))
                {
                    throw new IOException("Plugin 目标目录不能是符号链接或重解析点。 ");
                }

                Directory.Move(destination, backup);
                movedExisting = true;
            }

            Directory.Move(temporary, destination);
        }
        catch
        {
            if (movedExisting && !Directory.Exists(destination) && Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
                movedExisting = false;
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                ClearGeneratedDirectory(temporary);
                Directory.Delete(temporary, recursive: false);
            }

            if (movedExisting && Directory.Exists(backup))
            {
                ClearGeneratedDirectory(backup);
                Directory.Delete(backup, recursive: false);
            }
        }
    }

    private static void CopyPackageDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry))
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyPackageDirectory(entry, target, cancellationToken);
            }
            else
            {
                CopyFile(entry, target, cancellationToken);
            }
        }
    }

    private static bool ContainsNodeModulesSegment(string relativePath) =>
        relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase));

    private static void MergeMissingCredentials(
        string source,
        string destination,
        CopyState state,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourceText = File.ReadAllText(source, Encoding.UTF8);
        var normalizedSource = DshCredentialStoreNormalizer.NormalizeForCurrentDsh(sourceText);

        if (!File.Exists(destination))
        {
            WriteTextAtomically(destination, normalizedSource);
            state.FileCount++;
            state.TotalBytes += Encoding.UTF8.GetByteCount(normalizedSource);
            return;
        }

        var normalizedDestination = NormalizeCredentialStore(destination);
        if (normalizedDestination)
        {
            state.FileCount++;
            state.TotalBytes += new FileInfo(destination).Length;
        }

        var existingKeys = ReadTopLevelYamlKeys(destination);
        var additions = normalizedSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => TryReadTopLevelYamlKey(line, out var key) && !existingKeys.Contains(key))
            .ToArray();
        if (additions.Length == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var current = File.ReadAllText(destination, Encoding.UTF8);
        var separator = current.Length == 0 || current.EndsWith('\n') ? string.Empty : Environment.NewLine;
        var merged = current + separator + string.Join(Environment.NewLine, additions) + Environment.NewLine;
        WriteTextAtomically(destination, merged);
        if (!normalizedDestination)
        {
            state.FileCount++;
        }

        state.TotalBytes += Encoding.UTF8.GetByteCount(string.Join(Environment.NewLine, additions));
    }

    private static bool NormalizeCredentialStore(string path)
    {
        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return false;
        }

        var current = File.ReadAllText(path, Encoding.UTF8);
        var normalized = DshCredentialStoreNormalizer.NormalizeForCurrentDsh(current);
        if (ReferenceEquals(current, normalized))
        {
            return false;
        }

        WriteTextAtomically(path, normalized);
        return true;
    }

    private static HashSet<string> ReadTopLevelYamlKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path) || IsReparsePoint(path))
        {
            return keys;
        }

        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (TryReadTopLevelYamlKey(line, out var key))
                {
                    keys.Add(key);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return keys;
    }

    private static bool TryReadTopLevelYamlKey(string line, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(line)
            || char.IsWhiteSpace(line[0])
            || line[0] == '#')
        {
            return false;
        }

        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        key = line[..separator].Trim();
        return key.Length > 0 && key.All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.');
    }

    private static bool IsWorkspaceStoreEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        if (IsReparsePoint(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            var hasWorkspaceIds = root.TryGetProperty("global", out var global)
                && global.ValueKind == JsonValueKind.Object
                && global.TryGetProperty("workspaceIds", out var workspaceIds)
                && workspaceIds.ValueKind == JsonValueKind.Array
                && workspaceIds.GetArrayLength() > 0;
            var hasWorkspaces = root.TryGetProperty("tables", out var tables)
                && tables.ValueKind == JsonValueKind.Object
                && tables.TryGetProperty("workspaces", out var workspaces)
                && workspaces.ValueKind == JsonValueKind.Object
                && workspaces.EnumerateObject().Any();
            return !hasWorkspaceIds && !hasWorkspaces;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool ContainsRegularFile(string directory)
    {
        if (!Directory.Exists(directory) || IsReparsePoint(directory))
        {
            return false;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsReparsePoint(entry))
                {
                    continue;
                }

                if (File.Exists(entry) || (Directory.Exists(entry) && ContainsRegularFile(entry)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static void CopyFileAtomically(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("导入文件没有父目录。 "));
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            CopyFile(source, temporary, cancellationToken);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteTextAtomically(string destination, string content)
    {
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ClearGeneratedDirectory(string directory)
    {
        if (!Directory.Exists(directory) || IsReparsePoint(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).ToArray())
        {
            if (IsReparsePoint(entry))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if (Directory.Exists(entry))
            {
                ClearGeneratedDirectory(entry);
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return path;
    }

    private static bool IsPathInside(string path, string parent)
    {
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class CopyState
    {
        public int FileCount { get; set; }

        public long TotalBytes { get; set; }
    }
}
