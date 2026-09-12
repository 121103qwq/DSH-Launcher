namespace DshLauncher.Services;

/// <summary>
/// 启动方式（<see cref="DshLauncher.Models.VersionOpenMode"/>）的生效规则（可测）：
/// 运行时自带 desktop surface 时，启动器的「Desktop 启动」不再适用，按 Web 处理；**判不到不替换**。
/// 变更集 112 收敛为 Web / Desktop / 隔离启动（卡片 ▼ 菜单只切换不启动）。
/// </summary>
public static class LaunchModePolicy
{
    public static DshLauncher.Models.VersionOpenMode Effective(
        DshLauncher.Models.VersionOpenMode selected,
        bool vendorDesktopSurface) =>
        selected == DshLauncher.Models.VersionOpenMode.Desktop && vendorDesktopSurface
            ? DshLauncher.Models.VersionOpenMode.Web
            : selected;
}
