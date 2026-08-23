namespace DshLauncher.Models;

public enum VersionPackageKind
{
    DshPack,
    ModPack
}

public sealed record DshPackPreview(
    string Name,
    string Description,
    string? DshVersion,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> AgentPresets,
    IReadOnlyList<string> Providers,
    string? Workflow,
    VersionPackageKind PackageKind,
    IReadOnlyList<string> Warnings)
{
    public int PluginCount => Plugins.Count;

    public int SkillCount => Skills.Count;

    public int AgentPresetCount => AgentPresets.Count;

    public int ProviderCount => Providers.Count;

    public string PackageKindText => PackageKind == VersionPackageKind.ModPack
        ? "DSH ModPack (.tgz)"
        : "DSH Launcher (.dshpack)";
}

public sealed record VersionPackageConversionResult(
    string OutputPath,
    VersionPackageKind SourceKind,
    VersionPackageKind DestinationKind,
    IReadOnlyList<string> Warnings);
