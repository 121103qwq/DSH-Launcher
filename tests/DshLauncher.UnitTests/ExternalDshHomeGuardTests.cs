using System.Text;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;
using Xunit.Abstractions;

namespace DshLauncher.UnitTests;

[Collection("DesktopOccupancy")]
public sealed class ExternalDshHomeGuardTests
{
    private readonly ITestOutputHelper _output;

    public ExternalDshHomeGuardTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DesktopProbePerformanceSample()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var guard = new ExternalDshHomeGuard();
        for (var warmup = 0; warmup < 5; warmup++) _ = guard.GetConflict(instance);
        const int samples = 40;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var sample = 0; sample < samples; sample++) _ = guard.GetConflict(instance);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"Desktop probe: {elapsed.TotalMilliseconds / samples:F3} ms/call; {allocated / samples:N0} bytes/call ({samples} calls, local process population).");
    }

    [Fact]
    public void PluginFailureRecoveryDoesNotWriteAnOccupiedHome()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateExternalInstance(temporary.Path);
        var profileFile = Path.Combine(instance.DshHome, "profiles", "web", "package.json");
        const string original = "{\"name\":\"original\"}";
        const string changed = "{\"name\":\"changed\"}";
        File.WriteAllText(profileFile, original);
        var snapshots = new MarketplaceService(paths, homeGuard: new ExternalDshHomeGuard(_ => Array.Empty<ExternalHomeOwner>()));
        var snapshot = snapshots.CreatePluginSnapshot(instance);
        File.WriteAllText(profileFile, changed);
        var guard = ConflictingGuard(instance.DshHome);
        Assert.Throws<InvalidOperationException>(() =>
            new MarketplaceService(paths, homeGuard: guard).RestorePluginSnapshot(instance, snapshot));
        Assert.Equal(changed, File.ReadAllText(profileFile));
        Assert.Throws<InvalidOperationException>(() => new PluginFailureReportService(guard).Create(
            instance, "install", "test-plugin", new IOException("test"), false, "blocked", snapshot));
        Assert.False(Directory.Exists(Path.Combine(instance.DshHome, ".dsh-launcher", "reports")));
    }

    [Fact]
    public void ExternalDeletionDoesNotCreateSynchronizationMetadata()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var sync = new ConversationSyncService(new VersionSettingsService(
            new LauncherPaths(Path.Combine(temporary.Path, "launcher"))));
        var before = Directory.GetFiles(instance.DshHome, "*", SearchOption.AllDirectories);
        sync.PropagateDeletion(instance, "session.jsonl", new[] { instance });
        Assert.Equal(before, Directory.GetFiles(instance.DshHome, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(instance.DshHome, ".dsh-launcher")));
    }

    [Fact]
    public void EveryOperationChecksFreshOccupancy()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var occupied = false;
        var calls = 0;
        var guard = new ExternalDshHomeGuard(_ =>
        {
            calls++;
            return occupied
                ? new[] { new ExternalHomeOwner(1234, "DSH Desktop", instance.DshHome) }
                : Array.Empty<ExternalHomeOwner>();
        });

        Assert.Null(guard.GetConflict(instance));
        occupied = true;
        Assert.NotNull(guard.GetConflict(instance));
        occupied = false;
        Assert.Null(guard.GetConflict(instance));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void ProcessHomeUsesExplicitHomeThenTheTargetUsersDefault()
    {
        using var temporary = new TestDirectory();
        var home = Path.Combine(temporary.Path, "explicit-home");
        var profile = Path.Combine(temporary.Path, "target-user");
        Assert.Equal(home, ExternalDshHomeGuard.ResolveHome(new(true, home, profile, null)));
        Assert.Equal(Path.Combine(profile, ".dsh"), ExternalDshHomeGuard.ResolveHome(new(true, null, profile, null)));
        Assert.Null(ExternalDshHomeGuard.ResolveHome(new(false, home, profile, null)));
        Assert.Null(ExternalDshHomeGuard.ResolveHome(new(true, "relative-home", profile, null)));
        Assert.Null(ExternalDshHomeGuard.ResolveHome(new(true, null, null, null)));
    }

    [Fact]
    public void SameHomeIsRejected()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var guard = new ExternalDshHomeGuard(_ => new[]
        {
            new ExternalHomeOwner(1234, "DSH Desktop", instance.DshHome.ToUpperInvariant() + Path.DirectorySeparatorChar)
        });

        var error = Assert.Throws<InvalidOperationException>(() => guard.EnsureAvailable(instance));

        Assert.Contains("正在使用此 DSH_HOME", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentHomeDoesNotBlock()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path, "target");
        var otherHome = CreateHome(temporary.Path, "other");
        var guard = new ExternalDshHomeGuard(_ => new[]
        {
            new ExternalHomeOwner(1234, "DSH Desktop", otherHome)
        });

        Assert.Null(guard.GetConflict(instance));
        guard.EnsureAvailable(instance);
    }

    [Fact]
    public void UnknownHomeIsRejectedConservatively()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var guard = new ExternalDshHomeGuard(_ => new[]
        {
            new ExternalHomeOwner(1234, "DeepSeek Desktop", null)
        });

        var error = Assert.Throws<InvalidOperationException>(() => guard.EnsureAvailable(instance));

        Assert.Contains("无法确认它使用的 DSH_HOME", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnerReaderFailureIsFailClosed()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var guard = new ExternalDshHomeGuard(_ => throw new IOException("test reader failure"));

        var error = Assert.Throws<InvalidOperationException>(() => guard.EnsureAvailable(instance));

        Assert.Contains("无法完成桌面端占用检查", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherOwnedHomeIsNotAffectedByExternalOwners()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path) with
        {
            UsesExternalDshHome = false
        };
        var guard = new ExternalDshHomeGuard(_ =>
            throw new IOException("the reader must not be called for Launcher-owned data"));

        Assert.Null(guard.GetConflict(instance));
        guard.EnsureAvailable(instance);
    }

    [Fact]
    public async Task StartRejectsConflictBeforeStartingAProcess()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var guard = ConflictingGuard(instance.DshHome);
        var allocatorCalls = 0;
        await using var runner = new DshInstanceRunner(
            portAllocator: () => Interlocked.Increment(ref allocatorCalls),
            homeGuard: guard);

        var result = await runner.StartAsync(instance);

        Assert.False(result.IsSuccess);
        Assert.Contains("正在使用此 DSH_HOME", result.Error, StringComparison.Ordinal);
        Assert.Null(result.ProcessId);
        Assert.Equal(0, allocatorCalls);
        Assert.False(runner.IsRunning(instance.Id));
    }

    [Fact]
    public async Task ConfigurationAndConversationMutationsAreRejectedBeforeWriting()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var guard = ConflictingGuard(instance.DshHome);

        var settingsPath = Path.Combine(instance.DshHome, "settings.yaml");
        const string settings = "existing: settings\n";
        File.WriteAllText(settingsPath, settings, new UTF8Encoding(false));
        var modelService = new ModelService(homeGuard: guard);
        await Assert.ThrowsAsync<InvalidOperationException>(() => modelService.SaveDeepSeekAsync(
            instance,
            "DUMMY_API_KEY",
            "https://example.test",
            new[] { "model" }));
        Assert.Equal(settings, File.ReadAllText(settingsPath, Encoding.UTF8));

        var sessionPath = Path.Combine(instance.DshHome, "sessions", "session.jsonl");
        const string session = "session must remain\n";
        File.WriteAllText(sessionPath, session, new UTF8Encoding(false));
        var entry = new ConversationEntry(
            "session.jsonl",
            sessionPath,
            null,
            null,
            DateTimeOffset.UtcNow,
            session.Length,
            false,
            false,
            "session",
            instance.Name);
        var conversationService = new ConversationService(homeGuard: guard);
        Assert.Throws<InvalidOperationException>(() => conversationService.Delete(instance, entry));
        Assert.Equal(session, File.ReadAllText(sessionPath, Encoding.UTF8));
    }

    [Fact]
    public async Task SkillAndProviderMutationsAreRejectedBeforeWriting()
    {
        using var temporary = new TestDirectory();
        var instance = CreateExternalInstance(temporary.Path);
        var guard = ConflictingGuard(instance.DshHome);

        var skillDirectory = Path.Combine(instance.DshHome, "skills", "keep");
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        const string skill = "---\nname: keep\ndescription: test\n---\n";
        File.WriteAllText(skillPath, skill, new UTF8Encoding(false));
        var skillEntry = new ExtensionEntry(
            "skill:keep",
            ExtensionKind.Skill,
            "keep",
            null,
            "test",
            skillDirectory,
            true,
            true);
        var extensionService = new ExtensionService(homeGuard: guard);
        await Assert.ThrowsAsync<InvalidOperationException>(() => extensionService.RemoveSkillAsync(instance, skillEntry));
        Assert.Equal(skill, File.ReadAllText(skillPath, Encoding.UTF8));

        var providerPath = Path.Combine(instance.DshHome, ".dsh-launcher", "providers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(providerPath)!);
        const string providers = "{\n  \"provider\": false\n}\n";
        File.WriteAllText(providerPath, providers, new UTF8Encoding(false));
        var providerService = new ProviderStateService(guard);
        Assert.Throws<InvalidOperationException>(() => providerService.Replace(
            instance,
            new Dictionary<string, bool> { ["other"] = true }));
        Assert.Equal(providers, File.ReadAllText(providerPath, Encoding.UTF8));
    }

    [Fact]
    public void SnapshotRestoreAndCloneAreRejectedWithoutChangingData()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var instance = CreateExternalInstance(temporary.Path);
        var settingsPath = Path.Combine(instance.DshHome, "settings.yaml");
        const string original = "value: original\n";
        const string changed = "value: changed\n";
        File.WriteAllText(settingsPath, original, new UTF8Encoding(false));

        var noConflictGuard = new ExternalDshHomeGuard(_ => Array.Empty<ExternalHomeOwner>());
        var snapshot = new VersionSnapshotService(paths, homeGuard: noConflictGuard)
            .CreateSnapshot(instance, "test");
        File.WriteAllText(settingsPath, changed, new UTF8Encoding(false));

        var conflictGuard = ConflictingGuard(instance.DshHome);
        var guardedSnapshots = new VersionSnapshotService(paths, homeGuard: conflictGuard);
        Assert.Throws<InvalidOperationException>(() => guardedSnapshots.RestoreSnapshot(instance, snapshot.FilePath));
        Assert.Equal(changed, File.ReadAllText(settingsPath, Encoding.UTF8));

        var registry = new InstanceRegistry(paths);
        var guardedPackages = new VersionPackageService(registry, paths, conflictGuard);
        Assert.Throws<InvalidOperationException>(() => guardedPackages.CloneVersion(instance, "blocked clone"));
        Assert.Empty(registry.Load());
        Assert.Equal(changed, File.ReadAllText(settingsPath, Encoding.UTF8));
    }

    [Fact]
    public void UnlinkRemainsAllowedWhenAnExternalHomeIsBusy()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var runtime = CreateRuntime(temporary.Path, "runtime");
        var home = CreateHome(temporary.Path, "external-home");
        var registry = new InstanceRegistry(paths);
        var linked = registry.RegisterExistingHome(
            "旧桌面",
            home,
            runtime with { DshHome = Path.Combine(temporary.Path, "unused-template-home") });
        var service = new VersionPackageService(registry, paths, ConflictingGuard(home));

        service.DeleteVersion(linked);

        Assert.Empty(registry.Load());
        Assert.True(File.Exists(Path.Combine(home, "settings.yaml")));
    }

    private static ExternalDshHomeGuard ConflictingGuard(string home) =>
        new(_ => new[] { new ExternalHomeOwner(9999, "DSH Desktop", home) });

    private static ManagerInstance CreateExternalInstance(string root, string name = "external")
    {
        var runtime = CreateRuntime(root, $"{name}-runtime");
        var home = CreateHome(root, $"{name}-home");
        return runtime with
        {
            Id = name,
            Name = name,
            DshHome = home,
            UsesExternalDshHome = true
        };
    }

    private static ManagerInstance CreateRuntime(string root, string name)
    {
        var runtimeRoot = Path.Combine(root, name);
        Directory.CreateDirectory(runtimeRoot);
        var executable = Path.Combine(runtimeRoot, "dsh.cmd");
        File.WriteAllText(executable, "@echo off", Encoding.UTF8);
        return new ManagerInstance(
            $"{name}-id",
            name,
            runtimeRoot,
            InstanceKind.Installed,
            Path.Combine(root, $"{name}-template-home"),
            executable,
            "1.0.0",
            InstanceRuntimeStatus.Ready,
            "npm",
            null,
            DateTimeOffset.UtcNow,
            DshLaunchSpec: new DshRuntimeLaunchSpec(
                DshRuntimeLaunchMode.DirectCommand,
                executable));
    }

    private static string CreateHome(string root, string name)
    {
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        File.WriteAllText(Path.Combine(home, "settings.yaml"), "settings: keep\n", new UTF8Encoding(false));
        return home;
    }
}
