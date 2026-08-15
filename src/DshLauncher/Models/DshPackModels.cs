namespace DshLauncher.Models;

public sealed record DshPackPreview(
    string Name,
    string Description,
    string? DshVersion,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> AgentPresets,
    IReadOnlyList<string> Providers,
    string? Workflow)
{
    public int PluginCount => Plugins.Count;

    public int SkillCount => Skills.Count;

    public int AgentPresetCount => AgentPresets.Count;

    public int ProviderCount => Providers.Count;
}
