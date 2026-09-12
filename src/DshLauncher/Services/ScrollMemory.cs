using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DshLauncher.Services;

/// <summary>
/// 滚动位置记忆（变更集 146）：把某个滚动区/列表的垂直偏移记进 <c>ui-state.json</c>，切页与重启后回到原处。
/// 参考实现原本散在 <c>ExtensionWindow</c>（市场/技能市场各一套字典）；这里做成共享组件：
/// <see cref="Attach"/> 挂滚动监听（600ms 防抖写盘），<see cref="Restore"/> 在数据填充后恢复。
/// <c>ScrollChanged</c> 是冒泡事件，所以把页根传进来也能收到内部列表的滚动通知。
/// </summary>
internal sealed class ScrollMemory
{
    private readonly UiStateStore _store;
    private readonly string _key;
    private readonly DispatcherTimer _saveTimer;
    private double _pending;
    private bool _restoring;

    public ScrollMemory(UiStateStore store, string key)
    {
        _store = store;
        _key = key;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            if (!_restoring)
            {
                _store.SaveScrollOffset(_key, _pending);
            }
        };
    }

    public void Attach(UIElement host)
        => host.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));

    /// <summary>恢复上次记住的偏移；应在数据填充之后再调用（否则可滚动范围还是 0）。</summary>
    public void Restore(DependencyObject host)
    {
        var offset = _store.GetScrollOffset(_key);
        if (offset <= 0)
        {
            return;
        }

        _restoring = true;
        try
        {
            if (FindScrollViewer(host) is { } viewer)
            {
                viewer.ScrollToVerticalOffset(offset);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoring || (e.VerticalChange == 0 && e.ExtentHeightChange == 0))
        {
            return;
        }

        if (sender is not DependencyObject host || FindScrollViewer(host) is not { } viewer)
        {
            return;
        }

        _pending = viewer.VerticalOffset;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    internal static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
