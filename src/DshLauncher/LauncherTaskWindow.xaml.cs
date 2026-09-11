using System.Windows;
using System.Windows.Controls;
using DshLauncher.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace DshLauncher;

/// <summary>
/// 任务中心（内嵌页）：运行中的长任务 + 最近 50 条历史。
/// 只读台账 + 取消，不做重试、不后台接管任务；任务本身仍由各自的窗口/流程驱动。
/// </summary>
public partial class LauncherTaskWindow : UserControl
{
    private readonly LauncherTaskService _tasks;
    private bool _clearHistoryArmed;

    public LauncherTaskWindow(LauncherTaskService tasks)
    {
        _tasks = tasks;
        InitializeComponent();
        Refresh();
        _tasks.Changed += OnTasksChanged;
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void Window_OnUnloaded(object sender, RoutedEventArgs e) => _tasks.Changed -= OnTasksChanged;

    private void OnTasksChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Refresh);
            return;
        }

        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearHistoryArmed)
        {
            // 自绘确认（不用 MessageBox）：第一次点变成确认态，再点一次才真的清空。
            _clearHistoryArmed = true;
            ClearHistoryButton.Content = "确认清空历史？";
            return;
        }

        _clearHistoryArmed = false;
        ClearHistoryButton.Content = "清空历史";
        _tasks.ClearHistory();
        Refresh();
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            _tasks.TryCancel(id);
        }
    }

    private void Refresh()
    {
        var tasks = _tasks.Snapshot();
        TaskList.ItemsSource = tasks;

        var running = tasks.Count(item => item.IsRunning);
        RunningCountText.Text = running == 0 ? "当前没有运行中的任务" : $"运行中 {running} 个";
        var history = tasks.Count - running;
        StatusText.Text = $"历史 {history} 条（上限 {LauncherTaskService.MaximumRetainedTasks}）· 台账文件 launcher-tasks.json";
        EmptyText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
