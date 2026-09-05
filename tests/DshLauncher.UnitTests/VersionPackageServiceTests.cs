using System.IO.Compression;
using System.Text;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class VersionPackageServiceTests
{
    [Fact]
    public void ExportSanitizationPreservesHotkeysAndYamlBlockScalars()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var runtime = Path.Combine(temporary.Path, "runtime");
        Directory.CreateDirectory(runtime);
        var registry = new InstanceRegistry(paths);
        var instance = registry.Register(
            "shareable",
            runtime,
            InstanceKind.Installed,
            detectedVersion: "0.1.0",
            packageManager: "npm");
        var settingsPath = Path.Combine(instance.DshHome, "settings.yaml");
        File.WriteAllText(
            settingsPath,
            "llm-deepseek:\n"
            + "  apiKey: super-secret\n"
            + "  privateKey: |-\n"
            + "    -----BEGIN PRIVATE KEY-----\n"
            + "    block-secret\n"
            + "    -----END PRIVATE KEY-----\n"
            + "  hotkey: Ctrl+Shift+P\n"
            + "notes: |\n"
            + "  hotkey: Ctrl+Alt+H\n"
            + "  colon: remains: unchanged\n"
            + "after: keep\n",
            new UTF8Encoding(false));

        var packagePath = Path.Combine(temporary.Path, "share.dshpack");
        new VersionPackageService(registry, paths).ExportPackage(
            instance,
            packagePath,
            new VersionExportOptions(
                IncludeProviderConfiguration: true,
                IncludePluginConfiguration: false));

        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("dsh-home/settings.yaml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sanitized = reader.ReadToEnd();

        Assert.DoesNotContain("super-secret", sanitized);
        Assert.DoesNotContain("block-secret", sanitized);
        Assert.Contains("hotkey: Ctrl+Shift+P", sanitized);
        Assert.Contains("  hotkey: Ctrl+Alt+H", sanitized);
        Assert.Contains("  colon: remains: unchanged", sanitized);
        Assert.Contains("after: keep", sanitized);
        Assert.Contains("privateKey: \"<redacted>\"", sanitized);
    }

    [Fact]
    public void PreviewRejectsUnsupportedPackageFormat()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var registry = new InstanceRegistry(paths);
        var packagePath = Path.Combine(temporary.Path, "unsupported.dshpack");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("{\"formatVersion\":999}");
        }

        var exception = Assert.Throws<InvalidDataException>(() =>
            new VersionPackageService(registry, paths).PreviewPackage(packagePath));

        Assert.Contains("格式版本", exception.Message);
    }
}
