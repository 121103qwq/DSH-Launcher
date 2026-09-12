namespace DshLauncher.Services;

/// <summary>
/// 启动方式（<see cref="DshLauncher.Models.VersionOpenMode"/>）的生效规则（可测，work-log/92，变更集 109）：
/// 选中的方式在当前实例 / 活动 profile 下不可用时回退为 Web 启动；**判不到不替换**。
/// </summary>
public static class LaunchModePolicy
{
    /// <param name="selected">用户在 ▼ 菜单里选中的方式（settings.OpenMode）。</param>
    /// <param name="vendorDesktopSurface">运行时自带 desktop surface（启动器的「Desktop 启动」不再适用）。</param>
    /// <param name="terminalSurfaceSupported">活动 profile 的呈现面允许在终端打开（终端面）。</param>
    /// <param name="terminalModeVisible">「在终端打开」入口未被实例设置隐藏。</param>
    public static DshLauncher.Models.VersionOpenMode Effective(
        DshLauncher.Models.VersionOpenMode selected,
        bool vendorDesktopSurface,
        bool terminalSurfaceSupported,
        bool terminalModeVisible)
    {
        if (selected == DshLauncher.Models.VersionOpenMode.Desktop && vendorDesktopSurface)
        {
            return DshLauncher.Models.VersionOpenMode.Web;
        }

        if (selected == DshLauncher.Models.VersionOpenMode.Terminal && (!terminalSurfaceSupported || !terminalModeVisible))
        {
            return DshLauncher.Models.VersionOpenMode.Web;
        }

        return selected;
    }
}
