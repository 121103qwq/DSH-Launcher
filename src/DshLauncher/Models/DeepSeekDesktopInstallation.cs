namespace DshLauncher.Models;

public sealed record DeepSeekDesktopInstallation(
    string InstallRoot,
    string? DesktopVersion,
    string NodeExecutablePath,
    string DshExecutablePath,
    string DshPackageRoot,
    string DshVersion);
