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
        + "\n打开方式：启动页选中版本后点“在官方桌面端打开”（使用该版本的 DSH_HOME）；尚无版本时点启动页的“打开官方桌面端”（使用默认 DSH_HOME）。此安装不能用作 Launcher 管理的运行时。";
}
