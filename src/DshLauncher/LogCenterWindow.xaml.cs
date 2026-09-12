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

    private void Window_OnLoaded(object sender, RoutedEventArgs e) => RefreshData();

    private void Window_OnUnloaded(object sender, RoutedEventArgs e) => _keywordDebounce.Stop();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();

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

    private void RefreshData()
    {
        _snapshot = LogCenterService.Load();

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
