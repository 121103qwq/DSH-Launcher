using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DshLauncher.Services;
using WpfBrush = System.Windows.Media.Brush;

namespace DshLauncher;

/// <summary>
/// 日志中心（work-log/86，变更集 103）：只读浏览 `launcher.log`（含轮转旧文件），
/// 按天分组 + 级别 / 实例 / 关键字过滤。内嵌页（仿任务中心），可返回设置。
///
/// 设计约束：不写入、不删除、不轮转（删除仍走「存储与清理」）；读取失败/坏行只计数不抛。
/// </summary>
public partial class LogCenterWindow : System.Windows.Controls.UserControl
{
    /// <summary>单次渲染的行数上限（防止一次塞入几千个元素；状态行会注明）。</summary>
    private const int MaxRenderedRows = 500;

    private readonly Action? _returnToSettings;
    private readonly DispatcherTimer _keywordDebounce;
    private LogCenterSnapshot _snapshot = new(Array.Empty<LogCenterEntry>(), 0, 0, false);
    private bool _suppressFilterEvents;

    public LogCenterWindow(Action? returnToSettings = null)
    {
        _returnToSettings = returnToSettings;
        InitializeComponent();
        _keywordDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _keywordDebounce.Tick += (_, _) =>
        {
            _keywordDebounce.Stop();
            Render();
        };
    }

    private readonly Services.ScrollMemory _logScroll = new(new Services.UiStateStore(), "page/logs");   // 变更集 146

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        _logScroll.Attach(LogScroll);   // 变更集 146：绑定到日志区滚动视图本身（不再靠"视觉树第一个"猜）
        await RefreshDataAsync();       // 变更集 149：首次加载也不阻塞 UI
    }

    private void Window_OnUnloaded(object sender, RoutedEventArgs e) => _keywordDebounce.Stop();


    private void Back_Click(object sender, RoutedEventArgs e) => _returnToSettings?.Invoke();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressFilterEvents)
        {
            Render();
        }
    }

    private void Keyword_Changed(object sender, TextChangedEventArgs e)
    {
        _keywordDebounce.Stop();
        _keywordDebounce.Start();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogCenterService.LogDirectory);
            Process.Start(new ProcessStartInfo(LogCenterService.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusTextStyler.Set(StatusText, "打开日志目录失败：" + ex.Message + "（请检查目录是否存在或权限）", isError: true);
        }
    }

    /// <summary>
    /// 变更集 148：一键清理日志 —— 复用既有的「存储与清理」分类（`crash` 崩溃日志、`old-logs` 轮转副本），
    /// **当前 launcher.log 不动**（它是活跃日志，清掉会失去本次会话的诊断证据；要清可在「存储与清理」里操作）。
    /// </summary>
    private async void CleanLogs_Click(object sender, RoutedEventArgs e)
    {
        var answer = AppDialog.Show(
            Window.GetWindow(this),
            "确定清理日志？\n\n会删除：崩溃日志（crash.log 及其轮转副本）与轮转旧日志（launcher.log.old / watchdog.log.1）。\n" +
            "当前 launcher.log 会保留（正在写入的活跃日志）。实例、会话与配置不受影响。",
            "确认清理日志",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        // 变更集 149：文件清理与日志解析都可能耗时（Clean 内部要扫描分类、Load 要解析上万行），
        // 全部移到后台线程；期间按钮置忙并给出进度提示，UI 不再卡住。
        var busyButton = sender as System.Windows.Controls.Button;
        if (busyButton is not null)
        {
            busyButton.IsEnabled = false;
        }

        StatusTextStyler.Set(StatusText, "正在清理日志…");
        try
        {
            var result = await Task.Run(() => new LauncherStorageService().Clean(new[] { "crash", "old-logs" }));
            var snapshot = await Task.Run(() => LogCenterService.Load());
            ApplySnapshot(snapshot);
            StatusTextStyler.Set(StatusText,
                result.RemovedCount + " 个日志文件已清理，释放 " + FormatBytes(result.FreedBytes) + "；当前 launcher.log 已保留。");
        }
        catch (Exception ex)
        {
            StatusTextStyler.Set(StatusText, "清理日志失败：" + ex.Message, isError: true);
        }
        finally
        {
            if (busyButton is not null)
            {
                busyButton.IsEnabled = true;
            }
        }
    }

    private static string FormatBytes(long bytes) => bytes < 1024
        ? bytes + " B"
        : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.0") + " KB" : (bytes / 1048576.0).ToString("0.0") + " MB";

    /// <summary>变更集 149：加载与渲染拆开——解析（可能上万行）放后台，UI 线程只做渲染。</summary>
    private void RefreshData() => ApplySnapshot(LogCenterService.Load());

    /// <summary>变更集 149：刷新也走后台加载，避免点一下卡一下。</summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDataAsync();

    private async Task RefreshDataAsync()
    {
        var snapshot = await Task.Run(() => LogCenterService.Load());
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(LogCenterSnapshot snapshot)
    {
        _snapshot = snapshot;

        _suppressFilterEvents = true;
        try
        {
            var previousLevel = LevelBox.SelectedItem as string;
            var levels = new List<string> { "全部", "INFO", "WARN", "ERROR" };
            LevelBox.ItemsSource = levels;
            LevelBox.SelectedItem = previousLevel is not null && levels.Contains(previousLevel) ? previousLevel : "全部";

            var previousInstance = InstanceBox.SelectedItem as string;
            var instances = new List<string> { "全部" };
            instances.AddRange(LogCenterService.CollectInstances(_snapshot.Entries));
            InstanceBox.ItemsSource = instances;
            InstanceBox.SelectedItem = previousInstance is not null && instances.Contains(previousInstance)
                ? previousInstance
                : "全部";
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        Render();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => _logScroll.Restore(LogScroll)));   // 变更集 146
    }

    private void Render()
    {
        var level = LevelBox.SelectedItem as string;
        var instance = InstanceBox.SelectedItem as string;
        var keyword = KeywordBox.Text;

        var filtered = LogCenterService.Filter(
            _snapshot.Entries,
            string.Equals(level, "全部", StringComparison.Ordinal) ? null : level,
            string.IsNullOrWhiteSpace(keyword) ? null : keyword,
            string.Equals(instance, "全部", StringComparison.Ordinal) ? null : instance);

        LogPanel.Children.Clear();
        var rendered = 0;
        foreach (var group in LogCenterService.GroupByDay(filtered))
        {
            LogPanel.Children.Add(BuildDayHeader(group));
            foreach (var entry in group.Entries)
            {
                if (rendered >= MaxRenderedRows)
                {
                    break;
                }

                LogPanel.Children.Add(BuildEntryRow(entry));
                rendered++;
            }

            if (rendered >= MaxRenderedRows)
            {
                break;
            }
        }

        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var status = $"匹配 {filtered.Count} 条 · 共 {_snapshot.Entries.Count} 条";
        if (_snapshot.SkippedLines > 0)
        {
            status += $" · 跳过 {_snapshot.SkippedLines} 行无法解析";
        }

        if (_snapshot.Truncated)
        {
            status += $" · 仅读取最近 {LogCenterService.DefaultMaxEntries} 条";
        }

        if (filtered.Count > rendered)
        {
            status += $" · 仅显示前 {rendered} 条";
        }

        StatusTextStyler.Set(StatusText, status);
    }

    private UIElement BuildDayHeader(LogDayGroup group)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 5) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{group.Day:yyyy-MM-dd}（{group.Entries.Count} 条）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = (WpfBrush)FindResource("LineBrush")
        });
        return panel;
    }

    private UIElement BuildEntryRow(LogCenterEntry entry)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var muted = (WpfBrush)FindResource("MutedBrush");
        var time = new TextBlock
        {
            Text = entry.LocalTimeText,
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 0);
        grid.Children.Add(time);

        var level = new TextBlock
        {
            Text = entry.Level,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = LevelBrush(entry.Level),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(level, 1);
        grid.Children.Add(level);

        var code = new TextBlock
        {
            Text = entry.Code ?? string.Empty,
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(code, 2);
        grid.Children.Add(code);

        var message = new TextBlock
        {
            Text = entry.Message,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Message,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(message, 3);
        grid.Children.Add(message);

        var instance = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Instance) ? string.Empty : "· " + entry.Instance,
            FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(instance, 4);
        grid.Children.Add(instance);

        return grid;
    }

    private WpfBrush LevelBrush(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" => (WpfBrush)FindResource("DangerBrush"),
        "WARN" => (WpfBrush)FindResource("WarningBrush"),
        _ => (WpfBrush)FindResource("MutedBrush")
    };
}
