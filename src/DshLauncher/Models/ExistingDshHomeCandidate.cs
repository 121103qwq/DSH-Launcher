namespace DshLauncher.Models;

/// <summary>
/// A read-only DSH_HOME that can be offered for linking to a Launcher version.
/// </summary>
public sealed record ExistingDshHomeCandidate(
    string Name,
    string DshHome,
    string Source,
    DshRuntimeInfo? Runtime,
    bool IsRegistered);
