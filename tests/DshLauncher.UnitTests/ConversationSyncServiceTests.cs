using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ConversationSyncServiceTests
{
    [Fact]
    public void PropagateDeletionPersistsTombstonesAndPreventsResurrection()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var settings = new VersionSettingsService(paths);
        var first = CreateTestInstance("first", Path.Combine(temporary.Path, "runtime"), Path.Combine(temporary.Path, "first-home"));
        var second = CreateTestInstance("second", first.RootPath, Path.Combine(temporary.Path, "second-home"));
        ConfigureWorkspace(settings, first, second);

        var relativePath = "--C-work--/session-a/session.jsonl";
        WriteSession(first, relativePath, "first");
        WriteSession(second, relativePath, "second");

        var service = new ConversationSyncService(settings);
        var deletion = service.PropagateDeletion(first, relativePath, new[] { first, second });

        Assert.Empty(deletion.Errors);
        Assert.False(File.Exists(SessionPath(first, relativePath)));
        Assert.False(File.Exists(SessionPath(second, relativePath)));
        AssertTombstone(first, relativePath);
        AssertTombstone(second, relativePath);

        WriteSession(first, relativePath, "stale first");
        WriteSession(second, relativePath, "stale second");
        SetSessionTimestamp(first, relativePath, DateTime.UtcNow.AddMinutes(-1));
        SetSessionTimestamp(second, relativePath, DateTime.UtcNow.AddMinutes(-1));

        var synchronization = service.SynchronizeAll(new[] { first, second });

        Assert.Empty(synchronization.Errors);
        Assert.False(File.Exists(SessionPath(first, relativePath)));
        Assert.False(File.Exists(SessionPath(second, relativePath)));
    }

    [Fact]
    public void RunningVersionIsNotWrittenOrDeleted()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var settings = new VersionSettingsService(paths);
        var stopped = CreateTestInstance("stopped", Path.Combine(temporary.Path, "runtime"), Path.Combine(temporary.Path, "stopped-home"));
        var running = CreateTestInstance("running", stopped.RootPath, Path.Combine(temporary.Path, "running-home")) with
        {
            RuntimeStatus = InstanceRuntimeStatus.Running
        };
        ConfigureWorkspace(settings, stopped, running);

        const string relativePath = "--C-work--/session-a/session.jsonl";
        WriteSession(stopped, relativePath, "stopped source");
        WriteSession(running, relativePath, "running content");
        var runningContent = ReadSession(running, relativePath);
        var service = new ConversationSyncService(settings);

        var synchronization = service.Synchronize(stopped, new[] { stopped, running });
        Assert.Equal(1, synchronization.SkippedRunningVersions);
        Assert.Equal(runningContent, ReadSession(running, relativePath));

        var deletion = service.PropagateDeletion(stopped, relativePath, new[] { stopped, running });
        Assert.Empty(deletion.Errors);
        Assert.False(File.Exists(SessionPath(stopped, relativePath)));
        Assert.Equal(runningContent, ReadSession(running, relativePath));
    }

    [Fact]
    public void CorruptSessionDoesNotOverwriteValidSession()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var settings = new VersionSettingsService(paths);
        var corrupt = CreateTestInstance("corrupt", Path.Combine(temporary.Path, "runtime"), Path.Combine(temporary.Path, "corrupt-home"));
        var valid = CreateTestInstance("valid", corrupt.RootPath, Path.Combine(temporary.Path, "valid-home"));
        ConfigureWorkspace(settings, corrupt, valid);

        const string relativePath = "--C-work--/session-a/session.jsonl";
        var corruptPath = SessionPath(corrupt, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(corruptPath)!);
        File.WriteAllText(corruptPath, "this is not a session", new UTF8Encoding(false));
        WriteSession(valid, relativePath, "valid session");
        var expected = ReadSession(valid, relativePath);

        var result = new ConversationSyncService(settings).Synchronize(corrupt, new[] { corrupt, valid });

        Assert.NotEmpty(result.Errors);
        Assert.Equal(expected, ReadSession(corrupt, relativePath));
        Assert.Equal(expected, ReadSession(valid, relativePath));
    }

    [Fact]
    public void CorruptSyncStateReportsErrorWithoutDeletingSessions()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var settings = new VersionSettingsService(paths);
        var first = CreateTestInstance("first", Path.Combine(temporary.Path, "runtime"), Path.Combine(temporary.Path, "first-home"));
        var second = CreateTestInstance("second", first.RootPath, Path.Combine(temporary.Path, "second-home"));
        ConfigureWorkspace(settings, first, second);

        const string relativePath = "--C-work--/session-a/session.jsonl";
        WriteSession(first, relativePath, "first session");
        WriteSession(second, relativePath, "second session");
        var statePath = Path.Combine(first.DshHome, ".dsh-launcher", "conversation-sync.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, "{ not valid json", new UTF8Encoding(false));

        var result = new ConversationSyncService(settings).SynchronizeAll(new[] { first, second });

        Assert.NotEmpty(result.Errors);
        Assert.True(File.Exists(SessionPath(first, relativePath)));
        Assert.True(File.Exists(SessionPath(second, relativePath)));
    }

    [Fact]
    public void DeletionRejectsTraversalPath()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var settings = new VersionSettingsService(paths);
        var first = CreateTestInstance("first", Path.Combine(temporary.Path, "runtime"), Path.Combine(temporary.Path, "first-home"));
        var second = CreateTestInstance("second", first.RootPath, Path.Combine(temporary.Path, "second-home"));
        ConfigureWorkspace(settings, first, second);

        var outside = Path.Combine(temporary.Path, "outside", "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "must survive", new UTF8Encoding(false));

        var result = new ConversationSyncService(settings).PropagateDeletion(
            first,
            "../outside/session.jsonl",
            new[] { first, second });

        Assert.NotEmpty(result.Errors);
        Assert.Equal("must survive", File.ReadAllText(outside));
        Assert.False(File.Exists(Path.Combine(first.DshHome, ".dsh-launcher", "conversation-sync.json")));
    }

    private static void ConfigureWorkspace(VersionSettingsService settings, params ManagerInstance[] instances)
    {
        foreach (var instance in instances)
        {
            settings.Save(instance, new VersionSettingsData
            {
                ConversationSyncMode = ConversationSyncMode.Workspace,
                ConversationWorkspace = "shared"
            });
        }
    }

    private static void WriteSession(ManagerInstance instance, string relativePath, string text)
    {
        var path = SessionPath(instance, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BuildSessionJsonl(instance.Id, text), new UTF8Encoding(false));
    }

    private static string ReadSession(ManagerInstance instance, string relativePath) =>
        File.ReadAllText(SessionPath(instance, relativePath));

    private static void SetSessionTimestamp(ManagerInstance instance, string relativePath, DateTime timestamp) =>
        File.SetLastWriteTimeUtc(SessionPath(instance, relativePath), timestamp);

    private static void AssertTombstone(ManagerInstance instance, string relativePath)
    {
        var path = Path.Combine(instance.DshHome, ".dsh-launcher", "conversation-sync.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var deleted = document.RootElement.GetProperty("Deleted");
        Assert.True(deleted.TryGetProperty(relativePath, out var timestamp));
        Assert.True(timestamp.ValueKind == JsonValueKind.String && timestamp.GetDateTime() > DateTime.MinValue);
    }

    private static string SessionPath(ManagerInstance instance, string relativePath) =>
        Path.Combine(instance.DshHome, "sessions", relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string BuildSessionJsonl(string id, string text) =>
        JsonSerializer.Serialize(new
        {
            type = "session",
            version = 0,
            id,
            createdAt = 1,
            cwd = "C:\\work",
            delegationDepth = 0
        })
        + "\n"
        + JsonSerializer.Serialize(new { type = "message", text })
        + "\n";

    private static ManagerInstance CreateTestInstance(string id, string root, string home) => new(
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

internal sealed class TestDirectory : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("dsh-launcher-xunit-");

    public string Path => directory.FullName;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // The test must not hide its assertion failure behind best-effort cleanup.
        }
    }
}
