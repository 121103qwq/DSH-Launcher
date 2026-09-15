using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher.Models;

public enum VisualMaterial
{
    Solid,
    FrostedGlass,
    LiquidGlass
}

public sealed class VisualEffectsSettings
{
    public bool Enabled { get; set; } = true;

    [JsonConverter(typeof(VisualMaterialJsonConverter))]
    public VisualMaterial Material { get; set; } = VisualMaterial.LiquidGlass;

    public bool AmbientMotion { get; set; } = true;

    public bool Particles { get; set; } = true;

    public bool PointerHalo { get; set; } = true;

    public bool PointerTrail { get; set; }

    public bool ClickRipples { get; set; } = true;

    public bool Parallax { get; set; } = true;

    public VisualEffectsSettings Clone() => new()
    {
        Enabled = Enabled,
        Material = Material,
        AmbientMotion = AmbientMotion,
        Particles = Particles,
        PointerHalo = PointerHalo,
        PointerTrail = PointerTrail,
        ClickRipples = ClickRipples,
        Parallax = Parallax
    };
}

/// <summary>
/// VisualMaterial is an optional Launcher setting. A newer Launcher may add a
/// material before this one understands it, so an unknown token only falls
/// back to the current safe material instead of invalidating the whole file.
/// </summary>
internal sealed class VisualMaterialJsonConverter : JsonConverter<VisualMaterial>
{
    public override bool HandleNull => true;

    public override VisualMaterial Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<VisualMaterial>(reader.GetString(), ignoreCase: true, out var material)
            && Enum.IsDefined(material))
        {
            return material;
        }

        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(VisualMaterial), numeric))
        {
            return (VisualMaterial)numeric;
        }

        if (reader.TokenType is not JsonTokenType.Null)
        {
            reader.Skip();
        }

        return VisualMaterial.LiquidGlass;
    }

    public override void Write(
        Utf8JsonWriter writer,
        VisualMaterial value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(Enum.IsDefined(value)
            ? value.ToString()
            : VisualMaterial.LiquidGlass.ToString());
    }
}

public enum ConversationSyncMode
{
    Independent,
    Workspace,
    All
}

public enum PluginInstallMode
{
    Fast,
    Compatibility
}

public enum VersionOpenMode
{
    Launcher,
    Desktop,
    Custom
}

public sealed class VersionSettingsData
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = LauncherConfigSchema.CurrentVersion;

    public string ActiveProfileName { get; set; } = "web";

    public bool SyncAllConfiguration { get; set; }

    public ConversationSyncMode ConversationSyncMode { get; set; } = ConversationSyncMode.Independent;

    public string? ConversationWorkspace { get; set; }

    public bool SyncModelProviders { get; set; } = true;

    public bool UseDshMarketHotReload { get; set; } = true;

    /// <summary>
    /// Stops a Launcher-managed instance after this many minutes without a
    /// conversation file update. Zero disables the behavior.
    /// </summary>
    public int IdleStopMinutes { get; set; }

    /// <summary>
    /// Restarts a Launcher-managed instance after an unexpected process exit.
    /// The Launcher applies a bounded retry count and backoff.
    /// </summary>
    public bool RestartOnCrash { get; set; }

    public string? WindowTitle { get; set; }

    public string? NodeExecutablePath { get; set; }

    /// <summary>
    /// Null keeps the legacy behavior: a detected DSH Desktop runtime opens as
    /// a desktop window, while normal DSh runtimes use Launcher web startup.
    /// </summary>
    public VersionOpenMode? OpenMode { get; set; }

    /// <summary>
    /// Local executable, script or Windows shortcut used when OpenMode is Custom.
    /// This machine-specific path is not included in shareable version packages.
    /// </summary>
    public string? CustomOpenTargetPath { get; set; }
}

/// <summary>
/// Launcher 级设置：作用域是全部版本，而不是某一个版本的 DSH_HOME。
/// </summary>
public sealed class LauncherSettingsData
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = LauncherConfigSchema.CurrentVersion;

    public bool SyncAllConfiguration { get; set; }

    public List<string> Workspaces { get; set; } = new();

    public PluginInstallMode PluginInstallMode { get; set; } = PluginInstallMode.Fast;

    /// <summary>
    /// Optional npm global prefix used only for the Launcher-managed DSh runtime.
    /// Instance data remains isolated under each instance's DSH_HOME.
    /// </summary>
    public string? DshInstallDirectory { get; set; }

    /// <summary>
    /// Optional GitHub token protected with Windows DPAPI for the current user.
    /// The plaintext token is never written to Launcher configuration.
    /// </summary>
    public string? GitHubTokenCiphertext { get; set; }

    /// <summary>
    /// Optional in JSON so older Launcher settings remain readable. A missing
    /// or null value is normalized to the enabled default profile.
    /// </summary>
    public VisualEffectsSettings VisualEffects { get; set; } = new();

    /// <summary>Records the one-time v1.2.3 enablement, so later opt-outs persist.</summary>
    [JsonPropertyName("visualEffectsDefaultApplied")]
    public bool VisualEffectsDefaultApplied { get; set; }
}

public sealed record VersionExportOptions(
    bool IncludeProviderConfiguration,
    bool IncludePluginConfiguration);
