using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class VisualEffectsSettingsTests
{
    [Fact]
    public void DefaultsMatchLightweightVisualEffectsProfile()
    {
        var settings = new VisualEffectsSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(VisualMaterial.LiquidGlass, settings.Material);
        Assert.True(settings.AmbientMotion);
        Assert.True(settings.Particles);
        Assert.True(settings.PointerHalo);
        Assert.False(settings.PointerTrail);
        Assert.True(settings.ClickRipples);
        Assert.True(settings.Parallax);
    }

    [Fact]
    public void CloneCopiesEveryValueWithoutSharingMutableState()
    {
        var original = new VisualEffectsSettings
        {
            Enabled = true,
            Material = VisualMaterial.FrostedGlass,
            AmbientMotion = false,
            Particles = false,
            PointerHalo = false,
            PointerTrail = true,
            ClickRipples = false,
            Parallax = false
        };

        var clone = original.Clone();
        clone.Enabled = false;
        clone.Material = VisualMaterial.Solid;

        Assert.True(original.Enabled);
        Assert.Equal(VisualMaterial.FrostedGlass, original.Material);
        Assert.Equal(original.AmbientMotion, clone.AmbientMotion);
        Assert.Equal(original.Particles, clone.Particles);
        Assert.Equal(original.PointerHalo, clone.PointerHalo);
        Assert.Equal(original.PointerTrail, clone.PointerTrail);
        Assert.Equal(original.ClickRipples, clone.ClickRipples);
        Assert.Equal(original.Parallax, clone.Parallax);
    }

    [Theory]
    [InlineData("\"future-material\"")]
    [InlineData("99")]
    [InlineData("null")]
    public void UnknownMaterialOnlyFallsBackToLiquidGlass(string materialJson)
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var service = new VersionSettingsService(paths);
        Directory.CreateDirectory(paths.RootDirectory);
        File.WriteAllText(
            service.LauncherSettingsPath,
            $$$"""{"schemaVersion":1,"syncAllConfiguration":true,"workspaces":["keep"],"pluginInstallMode":"Compatibility","visualEffects":{"enabled":true,"material":{{{materialJson}}},"particles":false}}""",
            new UTF8Encoding(false));

        var settings = service.ReadLauncherSettings();

        Assert.True(settings.SyncAllConfiguration);
        Assert.Equal(new[] { "keep" }, settings.Workspaces);
        Assert.Equal(PluginInstallMode.Compatibility, settings.PluginInstallMode);
        Assert.NotNull(settings.VisualEffects);
        Assert.True(settings.VisualEffects!.Enabled);
        Assert.Equal(VisualMaterial.LiquidGlass, settings.VisualEffects.Material);
        Assert.False(settings.VisualEffects.Particles);
    }

    [Fact]
    public void NullVisualEffectsFieldIsAcceptedAndOtherSettingsRemain()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var service = new VersionSettingsService(paths);
        Directory.CreateDirectory(paths.RootDirectory);
        const string json = "{\"schemaVersion\":1,\"syncAllConfiguration\":true,\"visualEffects\":null}";
        File.WriteAllText(service.LauncherSettingsPath, json, new UTF8Encoding(false));

        var settings = service.ReadLauncherSettings();

        Assert.True(settings.SyncAllConfiguration);
        Assert.NotNull(settings.VisualEffects);
        Assert.True(settings.VisualEffects.Enabled);
    }

    [Fact]
    public void MissingVisualEffectsFieldReadsAsEnabledDefaults()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var service = new VersionSettingsService(paths);
        Directory.CreateDirectory(paths.RootDirectory);
        File.WriteAllText(
            service.LauncherSettingsPath,
            "{\"schemaVersion\":1,\"syncAllConfiguration\":true}",
            new UTF8Encoding(false));

        var settings = service.ReadLauncherSettings();
        var visualEffects = settings.VisualEffects;

        Assert.True(visualEffects.Enabled);
        Assert.Equal(VisualMaterial.LiquidGlass, visualEffects.Material);
    }

    [Fact]
    public void VisualEffectsSaveRoundTripsWithOtherLauncherSettings()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var service = new VersionSettingsService(paths);
        var expected = new VisualEffectsSettings
        {
            Enabled = true,
            Material = VisualMaterial.Solid,
            AmbientMotion = false,
            Particles = true,
            PointerHalo = false,
            PointerTrail = true,
            ClickRipples = false,
            Parallax = true
        };

        service.SaveLauncherSettings(new LauncherSettingsData
        {
            SyncAllConfiguration = true,
            Workspaces = new List<string> { "keep" },
            PluginInstallMode = PluginInstallMode.Compatibility,
            VisualEffects = expected
        });

        var actual = service.ReadLauncherSettings();

        Assert.True(actual.SyncAllConfiguration);
        Assert.Equal(new[] { "keep" }, actual.Workspaces);
        Assert.Equal(PluginInstallMode.Compatibility, actual.PluginInstallMode);
        Assert.NotNull(actual.VisualEffects);
        Assert.Equal(expected.Enabled, actual.VisualEffects!.Enabled);
        Assert.Equal(expected.Material, actual.VisualEffects.Material);
        Assert.Equal(expected.AmbientMotion, actual.VisualEffects.AmbientMotion);
        Assert.Equal(expected.Particles, actual.VisualEffects.Particles);
        Assert.Equal(expected.PointerHalo, actual.VisualEffects.PointerHalo);
        Assert.Equal(expected.PointerTrail, actual.VisualEffects.PointerTrail);
        Assert.Equal(expected.ClickRipples, actual.VisualEffects.ClickRipples);
        Assert.Equal(expected.Parallax, actual.VisualEffects.Parallax);

        using var document = JsonDocument.Parse(File.ReadAllText(service.LauncherSettingsPath, Encoding.UTF8));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("visualEffects", "enabled")]
    [InlineData("VisualEffects", "Enabled")]
    public void ExistingDisabledSettingsEnableOnceWithoutResettingPreferences(string visualKey, string enabledKey)
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var service = new VersionSettingsService(paths);
        Directory.CreateDirectory(paths.RootDirectory);
        var original = $$$"""{"schemaVersion":1,"syncAllConfiguration":true,"workspaces":["keep"],"unknownSetting":{"keep":42},"{{{visualKey}}}":{"{{{enabledKey}}}":false,"material":"FrostedGlass","particles":false,"unknownEffect":7}}""";
        File.WriteAllText(service.LauncherSettingsPath, original, new UTF8Encoding(false));

        var upgraded = service.ReadLauncherSettings();
        Assert.True(upgraded.VisualEffects.Enabled);
        Assert.True(upgraded.VisualEffectsDefaultApplied);
        Assert.Equal(VisualMaterial.FrostedGlass, upgraded.VisualEffects.Material);
        Assert.False(upgraded.VisualEffects.Particles);
        Assert.True(upgraded.SyncAllConfiguration);
        Assert.Equal(new[] { "keep" }, upgraded.Workspaces);
        var backup = Assert.Single(Directory.GetFiles(paths.RootDirectory, "*.bak"));
        Assert.Equal(original, File.ReadAllText(backup));
        using (var document = JsonDocument.Parse(File.ReadAllText(service.LauncherSettingsPath)))
        {
            Assert.Equal(42, document.RootElement.GetProperty("unknownSetting").GetProperty("keep").GetInt32());
            Assert.Equal(7, document.RootElement.GetProperty(visualKey).GetProperty("unknownEffect").GetInt32());
            Assert.True(document.RootElement.GetProperty(visualKey).GetProperty(enabledKey).GetBoolean());
        }
        var migratedJson = File.ReadAllText(service.LauncherSettingsPath);
        service.ReadLauncherSettings();
        Assert.Equal(migratedJson, File.ReadAllText(service.LauncherSettingsPath));
        Assert.Single(Directory.GetFiles(paths.RootDirectory, "*.bak"));

        upgraded.VisualEffects.Enabled = false;
        service.SaveLauncherSettings(upgraded);
        Assert.False(new VersionSettingsService(paths).ReadLauncherSettings().VisualEffects.Enabled);
        Assert.Single(Directory.GetFiles(paths.RootDirectory, "*.bak"));
    }

    [Fact]
    public void NewInstallCanTurnOffDefaultWithoutBeingReenabled()
    {
        using var temporary = new TestDirectory();
        var service = new VersionSettingsService(new LauncherPaths(temporary.Path));
        var settings = service.ReadLauncherSettings();
        Assert.True(settings.VisualEffects.Enabled);
        settings.VisualEffects.Enabled = false;
        service.SaveLauncherSettings(settings);
        Assert.True(service.ReadLauncherSettings().VisualEffectsDefaultApplied);
        Assert.False(service.ReadLauncherSettings().VisualEffects.Enabled);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.bak"));
    }

    [Fact]
    public void FutureSchemaIsNotReadOrRewrittenByVisualEffectsSettings()
    {
        using var temporary = new TestDirectory();
        var paths = new LauncherPaths(Path.Combine(temporary.Path, "launcher"));
        var service = new VersionSettingsService(paths);
        Directory.CreateDirectory(paths.RootDirectory);
        const string json = "{\"schemaVersion\":99,\"syncAllConfiguration\":true,\"visualEffects\":{\"enabled\":true}}";
        File.WriteAllText(service.LauncherSettingsPath, json, new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => service.ReadLauncherSettings());
        Assert.Equal(json, File.ReadAllText(service.LauncherSettingsPath, Encoding.UTF8));
        Assert.Throws<InvalidDataException>(() => service.SaveLauncherSettings(new LauncherSettingsData()));
        Assert.Equal(json, File.ReadAllText(service.LauncherSettingsPath, Encoding.UTF8));
    }
}
