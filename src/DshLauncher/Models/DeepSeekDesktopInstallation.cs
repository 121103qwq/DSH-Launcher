namespace DshLauncher.Models;

public sealed record DeepSeekDesktopInstallation(
    string InstallRoot,
    string? DesktopVersion,
    string NodeExecutablePath,
    string DshExecutablePath,
    string DshPackageRoot,
    string DshVersion,
    DshRuntimeLaunchSpec? LaunchSpec = null,
    string ProductName = "DeepSeek Desktop",
    bool IsOfficial = false,
    string? UpdateChannel = null,
    string? UpdateUrl = null)
{
    public string KindText => IsOfficial ? "官方桌面端" : ProductName;

    public string DesktopVersionText => string.IsNullOrWhiteSpace(DesktopVersion)
        ? "版本未知"
        : $"版本 {DesktopVersion}";

    public string Description => $"{KindText} · 只读\n{DesktopVersionText}\n{InstallRoot}"
        + (string.IsNullOrWhiteSpace(UpdateChannel) ? string.Empty : $"\n更新通道：{UpdateChannel}")
        + (string.IsNullOrWhiteSpace(UpdateUrl) ? string.Empty : $"\n更新地址：{UpdateUrl}")
        + "\n请从启动页选择实例后打开；此安装不能用作 Launcher 管理的运行时。";
}
