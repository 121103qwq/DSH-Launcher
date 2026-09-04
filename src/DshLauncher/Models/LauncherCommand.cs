namespace DshLauncher.Models;

public enum LauncherCommandAction
{
    Open,
    Start,
    Stop,
    Restart,
    Chat,
    VersionSettings,
    Plugins,
    Conversations
}

public sealed record LauncherCommand(
    LauncherCommandAction Action,
    string? InstanceId = null,
    string? SessionId = null,
    string? Path = null);
