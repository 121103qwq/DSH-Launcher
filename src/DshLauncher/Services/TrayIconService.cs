// TrayIconService.cs —— 任务栏托盘（0 新依赖：复用 csproj 已有的 UseWindowsForms）
//
// 行为设计（与上游"关窗即退出"的差异，仅此一行为变化）：
//   · 关闭主窗口 → 隐藏到托盘（不停止 Launcher 管理的实例）
//   · 双击托盘图标 / 菜单"打开" → 恢复主窗口
//   · 菜单"退出" → 走 MainWindow 现有清理链路（停 Managed 实例）后真正退出
// 外部 Attached 实例与 Chat 窗口行为不变。

using System.Drawing;
using System.Windows.Forms;

namespace DshLauncher.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconService(
        Icon icon,
        string tooltip,
        Action showMain,
        Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 DSH Launcher", null, (_, _) => showMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => quit());

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = tooltip,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => showMain();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
