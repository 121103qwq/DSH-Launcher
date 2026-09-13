using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;
using ZstdSharp;

namespace DshLauncher.UnitTests;

public sealed class ConversationSessionFormatTests
{
    [Theory]
    [InlineData("session.jsonl", 0, false)]
    [InlineData("session.v1.jsonl", 1, false)]
    [InlineData("session.v3.jsonl.zstd", 3, true)]
    public void CanonicalFileNamesExposeGenerationAndCompression(
        string fileName,
        long expectedVersion,
        bool expectedCompressed)
    {
        Assert.True(SessionFormatHelper.TryParseFileName(fileName, out var format));
        Assert.Equal(expectedVersion, format.Version);
        Assert.Equal(expectedCompressed, format.IsCompressed);
    }

    [Theory]
    [InlineData("session.v0.jsonl")]
    [InlineData("session.v01.jsonl")]
    [InlineData("session.V1.jsonl")]
    [InlineData("session.v1.jsonl.ZSTD")]
    [InlineData("session.jsonl.tmp")]
    public void NonCanonicalFileNamesAreNotStoredSessionFiles(string fileName)
    {
        Assert.False(SessionFormatHelper.TryParseFileName(fileName, out _));
    }

    [Fact]
    public void ListShowsOnlyHighestGenerationInOneSessionDirectory()
    {
        using var temporary = new TestDirectory();
        var instance = CreateInstance(
            "one",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "home"));
        var directory = Path.Combine(
            instance.DshHome,
            "sessions",
            "--C-work--",
            "session-a");
        Directory.CreateDirectory(directory);
        WriteSession(Path.Combine(directory, "session.jsonl"), 0, "old");
        WriteSession(Path.Combine(directory, "session.v2.jsonl"), 2, "middle");
        WriteSession(Path.Combine(directory, "session.v3.jsonl"), 3, "new");

        var entries = new ConversationService().List(instance);

        var entry = Assert.Single(entries);
        Assert.EndsWith("session.v3.jsonl", entry.FullPath, StringComparison.Ordinal);
        Assert.True(entry.HasValidHeader);
        Assert.True(File.Exists(Path.Combine(directory, "session.jsonl")));
        Assert.True(File.Exists(Path.Combine(directory, "session.v2.jsonl")));
    }

    [Fact]
    public void FilenameAndHeaderGenerationMismatchIsNotValid()
    {
        using var temporary = new TestDirectory();
        var instance = CreateInstance(
            "one",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "home"));
        var path = Path.Combine(
            instance.DshHome,
            "sessions",
            "--C-work--",
            "session-a",
            "session.v3.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteSession(path, 2, "mismatch");

        var entry = Assert.Single(new ConversationService().List(instance));

        Assert.False(entry.HasValidHeader);
        Assert.False(new ConversationService().CanOpen(instance, entry, out _));
    }

    [Fact]
    public void ImportPreservesVersionAndRejectsDuplicateGenerationDirectory()
    {
        using var temporary = new TestDirectory();
        var runtime = Path.Combine(temporary.Path, "runtime");
        CreateCatalogRuntime(runtime);
        var instance = CreateInstance(
            "one",
            runtime,
            Path.Combine(temporary.Path, "home"));
        var source = Path.Combine(temporary.Path, "session.v3.jsonl");
        WriteSession(source, 3, "portable");

        var imported = new ConversationService().Import(instance, source);

        Assert.EndsWith("session.v3.jsonl", imported, StringComparison.Ordinal);
        Assert.True(File.Exists(imported));
        var duplicate = Assert.Throws<IOException>(() => new ConversationService().Import(instance, source));
        Assert.Contains("相同会话 ID", duplicate.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session.jsonl", false)]
    [InlineData("session.v3.jsonl.zstd", true)]
    public void ImportRejectsDifferentGenerationWhenSessionDirectoryAlreadyExists(string fileName, bool compressed)
    {
        using var temporary = new TestDirectory();
        var runtime = Path.Combine(temporary.Path, "runtime");
        CreateCatalogRuntime(runtime);
        var instance = CreateInstance(
            "one",
            runtime,
            Path.Combine(temporary.Path, "home"));
        var existing = SessionPath(instance, $"--C-work--/session-a/{fileName}");
        if (compressed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            WriteCompressedSession(existing, 3, "existing");
        }
        else WriteSession(existing, 0, "existing");
        var source = Path.Combine(temporary.Path, "session.v3.jsonl");
        WriteSession(source, 3, "new-generation");

        var error = Assert.Throws<IOException>(() => new ConversationService().Import(instance, source));

        Assert.Contains("相同会话 ID", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(SessionPath(instance, "--C-work--/session-a/session.v3.jsonl")));
    }

    [Fact]
    public void V3CompressedImportBackupAndRestoreKeepPhysicalFormat()
    {
        using var temporary = new TestDirectory();
        var runtime = Path.Combine(temporary.Path, "runtime");
        CreateCatalogRuntime(runtime);
        var instance = CreateInstance(
            "one",
            runtime,
            Path.Combine(temporary.Path, "home"));
        var source = Path.Combine(temporary.Path, "session.v3.jsonl.zstd");
        WriteCompressedSession(source, 3, "compressed-v3");
        var service = new ConversationService(
            new LauncherPaths(Path.Combine(temporary.Path, "launcher")));

        var imported = service.Import(instance, source);
        var entry = Assert.Single(service.List(instance));
        Assert.Equal(imported, entry.FullPath);
        Assert.True(entry.IsCompressed && entry.HasValidHeader);

        var backupPath = service.Backup(instance, entry);
        var backup = Assert.Single(service.ListBackups(instance));
        Assert.True(backup.IsCompressed && backup.HasValidHeader);
        service.Delete(instance, entry);
        var restored = service.RestoreBackup(instance, backup);

        Assert.EndsWith("session.v3.jsonl.zstd", restored, StringComparison.Ordinal);
        Assert.True(File.Exists(restored));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(restored));
        Assert.Equal(backupPath, backup.FullPath, StringComparer.OrdinalIgnoreCase);
        Assert.True(Assert.Single(service.List(instance)).HasValidHeader);
    }

    [Fact]
    public void ImportRejectsHistoricalGenerationWhenRuntimeHasNoCatalog()
    {
        using var temporary = new TestDirectory();
        var instance = CreateInstance(
            "one",
            Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "home"));
        var source = Path.Combine(temporary.Path, "session.v2.jsonl");
        WriteSession(source, 2, "historical");

        var error = Assert.Throws<NotSupportedException>(
            () => new ConversationService().Import(instance, source));

        Assert.Contains("format catalog", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncDoesNotWriteHistoricalGenerationToRuntimeWithoutCatalog()
    {
        using var temporary = new TestDirectory();
        var first = CreateInstance(
            "first",
            Path.Combine(temporary.Path, "runtime-first"),
            Path.Combine(temporary.Path, "first-home"));
        CreateCatalogRuntime(first.RootPath);
        var second = CreateInstance(
            "second",
            Path.Combine(temporary.Path, "runtime-second"),
            Path.Combine(temporary.Path, "second-home"));
        var settings = new VersionSettingsService(
            new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
        settings.Save(first, WorkspaceSettings());
        settings.Save(second, WorkspaceSettings());

        const string relativePath = "--C-work--/session-a/session.v3.jsonl";
        WriteSession(SessionPath(first, relativePath), 3, "historical");
        var result = new ConversationSyncService(settings).Synchronize(first, new[] { first, second });

        Assert.NotEmpty(result.Errors);
        Assert.False(File.Exists(SessionPath(second, relativePath)));
    }

    [Fact]
    public void SyncCopiesHistoricalGenerationBetweenRuntimesWithCatalog()
    {
        using var temporary = new TestDirectory();
        var firstRoot = Path.Combine(temporary.Path, "runtime-first");
        var secondRoot = Path.Combine(temporary.Path, "runtime-second");
        CreateCatalogRuntime(firstRoot);
        CreateCatalogRuntime(secondRoot);
        var first = CreateInstance(
            "first",
            firstRoot,
            Path.Combine(temporary.Path, "first-home"));
        var second = CreateInstance(
            "second",
            secondRoot,
            Path.Combine(temporary.Path, "second-home"));
        var settings = new VersionSettingsService(
            new LauncherPaths(Path.Combine(temporary.Path, "launcher")));
        settings.Save(first, WorkspaceSettings());
        settings.Save(second, WorkspaceSettings());

        const string relativePath = "--C-work--/session-a/session.v3.jsonl";
        WriteSession(SessionPath(first, relativePath), 3, "historical");
        var result = new ConversationSyncService(settings).Synchronize(first, new[] { first, second });

        Assert.Empty(result.Errors);
        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(
            ReadBytes(SessionPath(first, relativePath)),
            ReadBytes(SessionPath(second, relativePath)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"name\":7}")]
    public void MalformedCatalogDoesNotCrashCapabilityCheck(string manifest)
    {
        using var temporary = new TestDirectory();
        var runtime = Path.Combine(temporary.Path, "runtime");
        CreateCatalogRuntime(runtime);
        File.WriteAllText(Path.Combine(runtime, "node_modules", "@deepseek-ai",
            "dsh-session-format-catalog", "package.json"), manifest);
        var instance = CreateInstance("one", runtime, Path.Combine(temporary.Path, "home"));
        Assert.False(SessionFormatHelper.IsRuntimeFormatSupported(instance, 3));
    }

    [Fact]
    public void CatalogInDshHomeDoesNotProveRuntimeSupport()
    {
        using var temporary = new TestDirectory();
        var instance = CreateInstance("one", Path.Combine(temporary.Path, "runtime"),
            Path.Combine(temporary.Path, "home"));
        CreateCatalogRuntime(instance.DshHome);
        Assert.False(SessionFormatHelper.IsRuntimeFormatSupported(instance, 3));
    }

    [Fact]
    public void PnpmRuntimeAndCatalogJunctionsResolveFromPhysicalPackage()
    {
        using var temporary = new TestDirectory();
        var physical = Path.Combine(temporary.Path, "store", "dsh", "node_modules", "@deepseek-ai", "dsh");
        CreateCatalogRuntime(physical);
        var physicalCatalog = Path.Combine(physical, "node_modules", "@deepseek-ai", "dsh-session-format-catalog");
        var storedCatalog = Path.Combine(temporary.Path, "store", "catalog");
        Directory.Move(physicalCatalog, storedCatalog);
        // pnpm's dependency link is beside the physical DSh package, while
        // the public DSh alias lives in a separate node_modules directory.
        var siblingCatalog = Path.Combine(Path.GetDirectoryName(physical)!, "dsh-session-format-catalog");
        CreateJunction(siblingCatalog, storedCatalog);
        var alias = Path.Combine(temporary.Path, "app", "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        CreateJunction(alias, physical);
        var instance = CreateInstance("one", alias, Path.Combine(temporary.Path, "home"));
        Assert.True(SessionFormatHelper.IsRuntimeFormatSupported(instance, 3));
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static VersionSettingsData WorkspaceSettings() => new()
    {
        ConversationSyncMode = ConversationSyncMode.Workspace,
        ConversationWorkspace = "shared"
    };

    private static void CreateCatalogRuntime(string runtime)
    {
        var catalog = Path.Combine(
            runtime,
            "node_modules",
            "@deepseek-ai",
            "dsh-session-format-catalog");
        Directory.CreateDirectory(Path.Combine(catalog, "lib"));
        File.WriteAllText(
            Path.Combine(runtime, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"test\"}",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(catalog, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh-session-format-catalog\",\"version\":\"test\",\"main\":\"lib/index.js\"}",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(catalog, "lib", "index.js"),
            RealCatalogSource,
            new UTF8Encoding(false));
    }

    // Copied from the published @deepseek-ai/dsh-session-format-catalog
    // 0.1.5-rc.2 lib/index.js. Keeping the generated imports and chain in the
    // fixture ensures capability detection is tested against the real package
    // shape, not only a package-name or marker stub.
    private const string RealCatalogSource = """
        import { KNOWN_SESSION_EVENT_TYPES, SESSION_FORMAT_VERSION, Session, SessionId, SessionLogOffset } from "@deepseek-ai/dsh-session";
        import { SessionFormatUnsupportedMigrationError, createSessionFormatCatalog } from "@deepseek-ai/dsh-session-format";
        import { releasedV0SessionFormatCodec, releasedV1SessionFormatCodec, sessionFormatV0ToV1 } from "@deepseek-ai/dsh-session-format-v0-to-v1";
        import { releasedV2SessionFormatCodec, sessionFormatV1ToV2 } from "@deepseek-ai/dsh-session-format-v1-to-v2";
        import { assertReleasedV3Header, releasedV3SessionFormatCodec, restoreReleasedV3Artifact, sessionFormatV2ToV3 } from "@deepseek-ai/dsh-session-format-v2-to-v3";
        function validateInstalledCurrentSessionHeader(header) {
            if (header.version !== SESSION_FORMAT_VERSION) throw new Error(`installed Session format is v${SESSION_FORMAT_VERSION}, got v${header.version}`);
            Session.fromRestore(SessionId(header.id), [], header, SessionLogOffset(0), "detached");
        }
        const sessionFormatCatalog = createSessionFormatCatalog({
            currentVersion: 3,
            codecs: [
                releasedV0SessionFormatCodec,
                releasedV1SessionFormatCodec,
                releasedV2SessionFormatCodec,
                releasedV3SessionFormatCodec
            ],
            currentEncoder: releasedV3SessionFormatCodec,
            migrations: [
                sessionFormatV0ToV1,
                sessionFormatV1ToV2,
                sessionFormatV2ToV3
            ],
            restoreCurrentHeader(header) {
                assertReleasedV3Header(header);
                validateInstalledCurrentSessionHeader(header);
                return header;
            }
        });
        export { SessionFormatUnsupportedMigrationError, sessionFormatCatalog };
        """;

    private static void WriteSession(string path, int version, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                type = "session",
                version,
                id = "session-a",
                createdAt = 1,
                cwd = "C:\\work",
                delegationDepth = 0
            })
            + "\n"
            + JsonSerializer.Serialize(new { type = "message", text })
            + "\n",
            new UTF8Encoding(false));
    }

    private static void WriteCompressedSession(string path, int version, string text)
    {
        var header = JsonSerializer.Serialize(new
        {
            type = "session",
            version,
            id = "session-a",
            createdAt = 1,
            cwd = "C:\\work",
            delegationDepth = 0
        }) + "\n";
        var eventLine = JsonSerializer.Serialize(new { type = "message", text }) + "\n";
        using var compressor = new Compressor(3);
        var first = compressor.Wrap(Encoding.UTF8.GetBytes(header)).ToArray();
        var second = compressor.Wrap(Encoding.UTF8.GetBytes(eventLine)).ToArray();
        using var output = File.Create(path);
        output.Write(first);
        output.Write(second);
    }

    private static byte[] ReadBytes(string path) => File.ReadAllBytes(path);

    private static string SessionPath(ManagerInstance instance, string relativePath) =>
        Path.Combine(instance.DshHome, "sessions", relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static ManagerInstance CreateInstance(string id, string root, string home) => new(
        Id: id,
        Name: id,
        RootPath: root,
        Kind: InstanceKind.Installed,
        DshHome: home,
        DshExecutablePath: null,
        DetectedVersion: "test",
        RuntimeStatus: InstanceRuntimeStatus.Ready,
        PackageManager: "npm",
        LastError: null,
        RegisteredAt: DateTimeOffset.UtcNow);
}
