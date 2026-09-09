// TrayIconService.cs —— 任务栏托盘（0 新依赖：复用 csproj 已有的 UseWindowsForms）
//
// 行为设计（与上游"关窗即退出"的差异，仅此一行为变化）：
//   · 关闭主窗口 → 隐藏到托盘（不停止 Launcher 管理的实例）
//   · 双击托盘图标 / 菜单"打开" → 恢复主窗口
//   · "运行中的实例"二级菜单 → 每个实例可"打开 / 停止"（借鉴 dsh-plugins/dsh-launcher 的托盘菜单，思路借鉴）
//   · 菜单"退出" → 走 MainWindow 现有清理链路（停 Managed 实例）后真正退出
// 外部 Attached 实例与 Chat 窗口行为不变。

using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;

namespace DshLauncher.Services;

/// <summary>托盘"运行中的实例"条目。</summary>
public sealed record TrayRunningItem(string InstanceId, string Name, string Detail);

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _runningMenu;
    private readonly Func<IReadOnlyList<TrayRunningItem>> _runningProvider;
    private readonly Action<string> _openInstance;
    private readonly Action<string> _stopInstance;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public TrayIconService(
        Icon icon,
        string tooltip,
        Func<IReadOnlyList<TrayRunningItem>> runningProvider,
        Action<string> openInstance,
        Action<string> stopInstance,
        Action showMain,
        Action quit)
    {
        _runningProvider = runningProvider;
        _openInstance = openInstance;
        _stopInstance = stopInstance;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _runningMenu = new ToolStripMenuItem("运行中的实例");
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 DSH Launcher", null, (_, _) => showMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_runningMenu);
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
        RefreshRunningItems();
    }

    /// <summary>按当前运行实例重建二级菜单（任意线程可调用，内部调度到 UI 线程）。</summary>
    public void RefreshRunningItems()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(RefreshRunningItems);
            return;
        }

        IReadOnlyList<TrayRunningItem> items;
        try
        {
            items = _runningProvider();
        }
        catch
        {
            items = Array.Empty<TrayRunningItem>();
        }

        _runningMenu.DropDownItems.Clear();
        if (items.Count == 0)
        {
            var empty = new ToolStripMenuItem("（暂无运行中的实例）") { Enabled = false };
            _runningMenu.DropDownItems.Add(empty);
            return;
        }

        foreach (var item in items)
        {
            var instanceMenu = new ToolStripMenuItem(item.Name);
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                instanceMenu.ToolTipText = item.Detail;
            }

            var id = item.InstanceId;
            instanceMenu.DropDownItems.Add("打开", null, (_, _) => _openInstance(id));
            instanceMenu.DropDownItems.Add("停止", null, (_, _) => _stopInstance(id));
            _runningMenu.DropDownItems.Add(instanceMenu);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
