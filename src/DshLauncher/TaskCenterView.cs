using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DshLauncher.Models;
using DshLauncher.Services;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfPanel = System.Windows.Controls.Panel;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfRun = System.Windows.Documents.Run;
using WpfUserControl = System.Windows.Controls.UserControl;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace DshLauncher;

/// <summary>
/// Minimal code-built task center. The service raises Changed on the calling
/// thread; this view is the only layer that marshals refreshes to WPF.
/// </summary>
public sealed class TaskCenterView : WpfUserControl
{
    private readonly LauncherTaskService _service;
    private readonly StackPanel _runningPanel = new();
    private readonly StackPanel _historyPanel = new();
    private readonly TextBlock _summaryText = new();
    private bool _subscribed;

    public TaskCenterView(LauncherTaskService? service = null)
    {
        _service = service ?? LauncherTaskService.Shared;
        Content = BuildContent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Subscribe();
        Refresh();
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "统一任务中心",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        _summaryText.Foreground = WpfBrushes.DimGray;
        _summaryText.Margin = new Thickness(0, 5, 0, 0);
        heading.Children.Add(_summaryText);

        var clearButton = new WpfButton
        {
            Content = "清理已完成",
            Padding = new Thickness(13, 7, 13, 7),
            VerticalAlignment = VerticalAlignment.Top
        };
        clearButton.Click += (_, _) => _service.ClearCompleted();

        Grid.SetColumn(clearButton, 1);
        header.Children.Add(heading);
        header.Children.Add(clearButton);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var content = new StackPanel();
        content.Children.Add(CreateSection("运行中", _runningPanel));
        content.Children.Add(CreateSection("历史记录", _historyPanel));
        scroll.Content = content;
        root.Children.Add(scroll);
        return root;
    }

    private static UIElement CreateSection(string title, WpfPanel panel)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        section.Children.Add(panel);
        return section;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _service.Changed += Service_Changed;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _service.Changed -= Service_Changed;
        _subscribed = false;
    }

    private void Service_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(Refresh));
        }
        catch (InvalidOperationException)
        {
            // The view can be unloaded while a worker publishes its update.
        }
    }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Service_Changed(this, EventArgs.Empty);
            return;
        }

        var all = _service.GetAll();
        var running = all.Where(static task => task.IsRunning).ToArray();
        var history = all.Where(static task => task.IsCompleted).ToArray();

        _runningPanel.Children.Clear();
        foreach (var task in running)
        {
            _runningPanel.Children.Add(CreateTaskCard(task));
        }

        _historyPanel.Children.Clear();
        foreach (var task in history)
        {
            _historyPanel.Children.Add(CreateTaskCard(task));
        }

        if (running.Length == 0)
        {
            _runningPanel.Children.Add(CreateEmptyText("当前没有运行中的任务。"));
        }

        if (history.Length == 0)
        {
            _historyPanel.Children.Add(CreateEmptyText("还没有任务历史。"));
        }

        _summaryText.Text = running.Length == 0
            ? $"共 {history.Length} 条历史记录"
            : $"正在运行 {running.Length} 项 · 历史 {history.Length} 条";
    }

    private UIElement CreateTaskCard(LauncherTaskSnapshot task)
    {
        var card = new Border
        {
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(224, 228, 234)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var details = new StackPanel();
        var title = new TextBlock { FontWeight = FontWeights.SemiBold };
        title.Inlines.Add(task.Title);
        title.Inlines.Add(new WpfRun($"  ·  {task.Category}") { Foreground = WpfBrushes.DimGray });
        details.Children.Add(title);
        details.Children.Add(new TextBlock
        {
            Text = $"{GetStatusText(task.Status)}  ·  {task.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            Foreground = WpfBrushes.DimGray,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var progress = new WpfProgressBar
        {
            Height = 7,
            Maximum = 100,
            Margin = new Thickness(0, 9, 0, 5),
            IsIndeterminate = task.Progress is null && task.IsRunning,
            Value = task.Progress ?? (task.IsCompleted ? 100 : 0)
        };
        details.Children.Add(progress);
        if (!string.IsNullOrWhiteSpace(task.StatusMessage))
        {
            details.Children.Add(new TextBlock
            {
                Text = task.StatusMessage,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (!string.IsNullOrWhiteSpace(task.Error))
        {
            details.Children.Add(new TextBlock
            {
                Text = task.Error,
                Foreground = WpfBrushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 0, 0, 0)
        };
        if (task.CanCancel)
        {
            var cancel = new WpfButton { Content = "取消", Padding = new Thickness(10, 5, 10, 5) };
            cancel.Click += (_, _) => _service.Cancel(task.Id);
            actions.Children.Add(cancel);
        }

        if (task.CanRetry)
        {
            var retry = new WpfButton
            {
                Content = "重试",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(6, 0, 0, 0)
            };
            retry.Click += (_, _) => _ = _service.RetryAsync(task.Id);
            actions.Children.Add(retry);
        }

        Grid.SetColumn(actions, 1);
        layout.Children.Add(details);
        layout.Children.Add(actions);
        card.Child = layout;
        return card;
    }

    private static TextBlock CreateEmptyText(string text)
        => new()
        {
            Text = text,
            Foreground = WpfBrushes.Gray,
            Margin = new Thickness(0, 0, 0, 4)
        };

    private static string GetStatusText(LauncherTaskStatus status)
        => status switch
        {
            LauncherTaskStatus.Waiting => "等待中",
            LauncherTaskStatus.Running => "运行中",
            LauncherTaskStatus.Succeeded => "已成功",
            LauncherTaskStatus.Failed => "失败",
            LauncherTaskStatus.Canceled => "已取消",
            _ => status.ToString()
        };
}
