using System.Text;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ExternalDshHomeRegistrationTests
{
    [Fact]
    public void RegisterExistingHomeDoesNotModifySourceFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new LauncherPaths(Path.Combine(root, "launcher"));
            var registry = new InstanceRegistry(paths);
            var runtime = CreateRuntime(root, "runtime");
            var home = CreateDshHome(root, "existing", "original");
            var sourceBytes = File.ReadAllBytes(Path.Combine(home, "settings.yaml"));

            var linked = registry.RegisterExistingHome("现有 DSh", home, runtime);

            Assert.True(linked.UsesExternalDshHome);
            Assert.Null(linked.ImportedFromDshHome);
            Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(home, "settings.yaml")));
            Assert.Single(registry.Load());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void SameHomeCannotBeRegisteredForDifferentRuntimes()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new LauncherPaths(Path.Combine(root, "launcher"));
            var registry = new InstanceRegistry(paths);
            var home = CreateDshHome(root, "existing", "original");
            registry.RegisterExistingHome("第一个", home, CreateRuntime(root, "runtime-1"));

            Assert.Throws<InvalidOperationException>(() =>
                registry.RegisterExistingHome("第二个", home, CreateRuntime(root, "runtime-2")));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void DeleteExternalVersionKeepsHomeFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new LauncherPaths(Path.Combine(root, "launcher"));
            var registry = new InstanceRegistry(paths);
            var service = new VersionPackageService(registry, paths);
            var home = CreateDshHome(root, "existing", "original");
            var nested = Path.Combine(home, "sessions", "session.jsonl");
            File.WriteAllText(nested, "session", Encoding.UTF8);
            var linked = service.LinkExistingHome(CreateRuntime(root, "runtime"), "现有 DSh", home);

            var backupDirectory = paths.GetInstanceBackupDirectory(linked.Id);
            Directory.CreateDirectory(backupDirectory);
            File.WriteAllText(Path.Combine(backupDirectory, "keep.backup"), "backup");

            // The stored ownership wins even if a stale caller supplies the wrong flag.
            service.DeleteVersion(linked with { UsesExternalDshHome = false });

            Assert.True(File.Exists(Path.Combine(home, "settings.yaml")));
            Assert.True(File.Exists(nested));
            Assert.True(File.Exists(Path.Combine(backupDirectory, "keep.backup")));
            Assert.Empty(registry.Load());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void MissingExternalHomeIsRetainedOnReload()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new LauncherPaths(Path.Combine(root, "launcher"));
            var registry = new InstanceRegistry(paths);
            var home = CreateDshHome(root, "existing", "original");
            registry.RegisterExistingHome("现有 DSh", home, CreateRuntime(root, "runtime"));
            Directory.Delete(home, recursive: true);

            var loaded = Assert.Single(registry.Load());

            Assert.True(loaded.UsesExternalDshHome);
            Assert.Equal(InstanceRuntimeStatus.Missing, loaded.RuntimeStatus);
            Assert.Contains("不存在", loaded.LastError);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void CloneOfExternalVersionUsesIndependentLauncherHome()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new LauncherPaths(Path.Combine(root, "launcher"));
            var registry = new InstanceRegistry(paths);
            var service = new VersionPackageService(registry, paths);
            var home = CreateDshHome(root, "existing", "original");
            var linked = service.LinkExistingHome(CreateRuntime(root, "runtime"), "现有 DSh", home);
            var settings = new VersionSettingsService(paths);
            settings.Save(linked, new VersionSettingsData { ActiveProfileName = "desktop" });
            Directory.CreateDirectory(Path.Combine(home, ".dsh-launcher"));
            File.WriteAllText(
                Path.Combine(home, ".dsh-launcher", "version-settings.json"),
                "{\"ActiveProfileName\":\"stale\"}",
                Encoding.UTF8);

            var clone = service.CloneVersion(linked, "副本");

            Assert.False(clone.UsesExternalDshHome);
            Assert.False(string.Equals(
                Path.GetFullPath(home),
                Path.GetFullPath(clone.DshHome),
                StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith(Path.GetFullPath(paths.InstancesDirectory),
                Path.GetFullPath(clone.DshHome), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("original", File.ReadAllText(Path.Combine(clone.DshHome, "settings.yaml")));
            Assert.Equal("desktop", settings.Read(clone).ActiveProfileName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRuntimeImportDoesNotOverwriteExternalHome(bool refresh)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new LauncherPaths(Path.Combine(root, "launcher"));
            var registry = new InstanceRegistry(paths);
            var home = CreateDshHome(root, "existing", "target");
            var sourceHome = CreateDshHome(root, "detected-home", "source");
            var runtime = CreateRuntime(root, "runtime");
            var linked = registry.RegisterExistingHome("现有 DSh", home, runtime);
            var detected = new DshRuntimeInfo(
                true,
                runtime.DshExecutablePath,
                "1.0.0",
                runtime.RootPath,
                null,
                ExistingDshHome: sourceHome,
                LaunchSpec: runtime.DshLaunchSpec);

            var result = await new DetectedRuntimeRegistrationService(registry).ImportAsync(
                new[] { linked },
                new[] { detected },
                refreshRegisteredRuntimeRoots: refresh);

            Assert.Equal("target", File.ReadAllText(Path.Combine(home, "settings.yaml")));
            Assert.Empty(result.AddedInstances);
            Assert.Empty(result.UpdatedInstances);
            Assert.Empty(result.BackfilledInstances);
            Assert.Empty(result.Errors);
            Assert.Null(Assert.Single(registry.Load()).ImportedFromDshHome);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void LinkRejectsInvalidRelativeAndOverlappingHomes()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var registry = new InstanceRegistry(paths);
        var runtime = CreateRuntime(temporary.Path, "runtime");
        Assert.Throws<ArgumentException>(() => registry.RegisterExistingHome("relative", "relative-home", runtime));
        var empty = Directory.CreateDirectory(Path.Combine(temporary.Path, "empty")).FullName;
        Assert.Throws<IOException>(() => registry.RegisterExistingHome("empty", empty, runtime));
        var owned = registry.Register("owned", runtime.RootPath, InstanceKind.Installed);
        Assert.Throws<IOException>(() => registry.RegisterExistingHome("owned link", owned.DshHome, runtime));
        var home = CreateDshHome(temporary.Path, "existing", "keep");
        registry.RegisterExistingHome("linked", home, runtime);
        var nestedHome = CreateDshHome(home, "nested", "keep");
        Assert.Throws<InvalidOperationException>(() => registry.RegisterExistingHome("nested", nestedHome, runtime));
    }

    private static ManagerInstance CreateRuntime(string root, string name)
    {
        var runtimeRoot = Path.Combine(root, name);
        Directory.CreateDirectory(runtimeRoot);
        var executable = Path.Combine(runtimeRoot, "dsh.cmd");
        File.WriteAllText(executable, "@echo off", Encoding.UTF8);
        return new ManagerInstance(
            $"template-{name}",
            name,
            runtimeRoot,
            InstanceKind.Installed,
            Path.Combine(root, $"template-home-{name}"),
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

    private static string CreateDshHome(string root, string name, string settings)
    {
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(home, "profiles", "web"));
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        Directory.CreateDirectory(Path.Combine(home, "storages"));
        File.WriteAllText(Path.Combine(home, "settings.yaml"), settings, Encoding.UTF8);
        return home;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsh-external-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
