using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class DspackPackageTests
{
    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(3, 5)]
    public void PreviewAcceptsSupportedProfileGenerationsAndUsesChineseLocale(
        int containerVersion,
        int manifestVersion)
    {
        using var temporary = new TestDirectory();
        var packagePath = CreateDspack(
            temporary.Path,
            containerVersion,
            manifestVersion,
            displayName: "整合包中文名",
            description: "中文描述");
        var packages = CreateEnvironment(temporary).Service;

        var preview = packages.PreviewPackage(packagePath);

        Assert.Equal(VersionPackageKind.Dspack, preview.PackageKind);
        Assert.Equal("整合包中文名", preview.Name);
        Assert.Equal("中文描述", preview.Description);
        Assert.Equal("0.1.0", preview.DshVersion);
        Assert.Contains(preview.Warnings, warning => warning.Contains("独立版本", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 5)]
    [InlineData(3, 3)]
    public void PreviewRejectsUnknownContainerOrManifestGeneration(
        int containerVersion,
        int manifestVersion)
    {
        using var temporary = new TestDirectory();
        var packagePath = CreateDspack(temporary.Path, containerVersion, manifestVersion);
        var packages = CreateEnvironment(temporary).Service;

        if (containerVersion is not (2 or 3))
        {
            Assert.Throws<InvalidDataException>(() => packages.PreviewPackage(packagePath));
        }
        else
        {
            Assert.Throws<NotSupportedException>(() => packages.PreviewPackage(packagePath));
        }
    }

    [Fact]
    public void ImportResolvesGithubDependencyFromSnapshotAndKeepsProfileIsolated()
    {
        using var temporary = new TestDirectory();
        var environment = CreateEnvironment(temporary);
        var packagePath = CreateDspack(
            temporary.Path,
            containerVersion: 2,
            manifestVersion: 4,
            dependencies: new Dictionary<string, string>
            {
                ["github:owner/repo"] = "abcdef1234567890"
            },
            bundles: new[] { "actual-plugin" },
            packageSnapshot: "{\"name\":\"old-snapshot\",\"version\":\"0.0.1\","
                + "\"dependencies\":{\"actual-plugin\":\"git+https://github.com/owner/repo.git#oldcommit\"}}",
            patch: "manifest patch",
            append: archive => AddText(archive, "overrides/agent/README.md", "safe resource"));

        var created = environment.Service.ImportPackage(packagePath, environment.Template);
        var profileRoot = Path.Combine(created.DshHome, "profiles", "web");
        var packageJson = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(profileRoot, "package.json"))).RootElement;
        var dependencies = packageJson.GetProperty("dependencies");

        Assert.NotEqual(environment.Template.DshHome, created.DshHome);
        Assert.Equal(
            "git+https://github.com/owner/repo.git#abcdef1234567890",
            dependencies.GetProperty("actual-plugin").GetString());
        Assert.False(dependencies.TryGetProperty("github:owner/repo", out _));
        Assert.Equal(
            "actual-plugin",
            packageJson.GetProperty("dsh").GetProperty("profile").GetProperty("bundles")[0].GetString());
        Assert.Equal("manifest patch", File.ReadAllText(Path.Combine(profileRoot, "cordis.patch.yml")));
        Assert.Equal("safe resource", File.ReadAllText(Path.Combine(profileRoot, "agent", "README.md")));
        Assert.True(File.Exists(Path.Combine(profileRoot, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(created.DshHome, "package.json")));
    }

    [Fact]
    public void OverridePatchTakesPrecedenceAndMachineConfigurationIsRejected()
    {
        using var temporary = new TestDirectory();
        var environment = CreateEnvironment(temporary);
        var packagePath = CreateDspack(
            temporary.Path,
            patch: "manifest patch",
            append: archive => AddText(archive, "overrides/cordis.patch.yml", "override patch"));

        var created = environment.Service.ImportPackage(packagePath, environment.Template);
        var profilePatch = Path.Combine(created.DshHome, "profiles", "web", "cordis.patch.yml");
        Assert.Equal("override patch", File.ReadAllText(profilePatch));

        var machineConfigPath = CreateDspack(
            temporary.Path,
            fileName: "machine-config.dspack",
            append: archive => AddText(archive, "overrides/package.json", "machine config"));
        Assert.Throws<InvalidDataException>(() => environment.Service.PreviewPackage(machineConfigPath));
    }

    [Theory]
    [InlineData("dshhome", null)]
    [InlineData("profile", "files")]
    public void PreviewRejectsWholeHomeOrExternalFiles(
        string type,
        string? nonEmptyFiles)
    {
        using var temporary = new TestDirectory();
        var files = nonEmptyFiles is null ? null : new object[] { new { path = "outside.txt" } };
        var packagePath = CreateDspack(temporary.Path, type: type, files: files);
        var packages = CreateEnvironment(temporary).Service;

        Assert.Throws<NotSupportedException>(() => packages.PreviewPackage(packagePath));
    }

    [Fact]
    public void PreviewRejectsTraversalDuplicateAndLinkEntries()
    {
        using var temporary = new TestDirectory();
        var environment = CreateEnvironment(temporary);
        var cases = new (string Name, Action<ZipArchive> Append)[]
        {
            ("traversal.dspack", archive => AddText(archive, "../outside.txt", "escape")),
            ("duplicate.dspack", archive =>
            {
                AddText(archive, "overrides/readme.md", "one");
                AddText(archive, "overrides/readme.md", "two");
            }),
            ("link.dspack", archive =>
            {
                var link = archive.CreateEntry("overrides/link.txt");
                link.ExternalAttributes = (int)FileAttributes.ReparsePoint
                    | unchecked((int)(0xA000u << 16));
            })
        };

        foreach (var (name, append) in cases)
        {
            var packagePath = CreateDspack(temporary.Path, fileName: name, append: append);
            Assert.Throws<InvalidDataException>(() => environment.Service.PreviewPackage(packagePath));
        }
    }

    [Fact]
    public void PreviewRejectsOversizeZipEntry()
    {
        using var temporary = new TestDirectory();
        var environment = CreateEnvironment(temporary);
        var packagePath = CreateDspack(
            temporary.Path,
            fileName: "oversize.dspack",
            append: archive =>
            {
                var entry = archive.CreateEntry("overrides/large.txt");
                using var stream = entry.Open();
                var block = new byte[1024 * 1024];
                for (var index = 0; index < 33; index++)
                {
                    stream.Write(block, 0, block.Length);
                }
            });

        Assert.Throws<InvalidDataException>(() => environment.Service.PreviewPackage(packagePath));
    }

    private static EnvironmentUnderTest CreateEnvironment(TestDirectory temporary)
    {
        var launcherRoot = Path.Combine(temporary.Path, "launcher");
        var runtimeRoot = Path.Combine(temporary.Path, "runtime");
        var packageRoot = Path.Combine(runtimeRoot, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.Combine(packageRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "node_modules", ".bin"));
        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.0\","
                + "\"bin\":{\"dsh\":\"bin/dsh.js\"}}",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(packageRoot, "bin", "dsh.js"), "console.log('test');", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(runtimeRoot, "node_modules", ".bin", "dsh.cmd"), "@echo off", new UTF8Encoding(false));

        var paths = new LauncherPaths(launcherRoot);
        var registry = new InstanceRegistry(paths);
        var template = registry.Register(
            "模板版本",
            runtimeRoot,
            InstanceKind.Installed,
            detectedVersion: "0.1.0",
            packageManager: "npm");
        new VersionSettingsService(paths).SaveLauncherSettings(new LauncherSettingsData
        {
            DshInstallDirectory = runtimeRoot
        });

        return new EnvironmentUnderTest(new VersionPackageService(registry, paths), template);
    }

    private static string CreateDspack(
        string root,
        int containerVersion = 2,
        int manifestVersion = 4,
        string type = "profile",
        object[]? files = null,
        string displayName = "测试整合包",
        string description = "测试描述",
        IReadOnlyDictionary<string, string>? dependencies = null,
        string[]? bundles = null,
        string? packageSnapshot = null,
        string patch = "manifest patch",
        string fileName = "profile.dspack",
        Action<ZipArchive>? append = null)
    {
        var path = Path.Combine(root, fileName);
        dependencies ??= new Dictionary<string, string> { ["demo-plugin"] = "1.2.3" };
        bundles ??= new[] { "demo-plugin" };
        var manifest = new Dictionary<string, object?>
        {
            ["manifestVersion"] = manifestVersion,
            ["type"] = type,
            ["name"] = "test-profile",
            ["displayName"] = new Dictionary<string, string>
            {
                ["zh-CN"] = displayName,
                ["en-US"] = "English profile"
            },
            ["description"] = new Dictionary<string, string>
            {
                ["zh-CN"] = description,
                ["en-US"] = "English description"
            },
            ["version"] = "1.0.0",
            ["dshVersion"] = "0.1.0",
            ["profileName"] = "web",
            ["bundles"] = bundles,
            ["dependencies"] = dependencies,
            ["patch"] = patch
        };
        if (files is not null)
        {
            manifest["files"] = files;
        }

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddJson(archive, "dspack.json", new { format = "dspack", version = containerVersion });
            AddJson(archive, "manifest.json", manifest);
            if (packageSnapshot is not null)
            {
                AddText(archive, "package.json", packageSnapshot);
            }
            append?.Invoke(archive);
        }

        return path;
    }

    private static void AddJson(ZipArchive archive, string name, object value) =>
        AddText(archive, name, JsonSerializer.Serialize(value));

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed record EnvironmentUnderTest(VersionPackageService Service, ManagerInstance Template);
}
