using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DshLauncher.Services;

namespace DshLauncher;

public sealed class LogCenterWindow : Window
{
    private readonly LauncherLogService _service;
    private readonly System.Windows.Controls.TextBox _content;

    public LogCenterWindow(Window owner, LauncherLogService service)
    {
        _service = service;
        Owner = owner;
        Title = "DSH Launcher 日志";
        Width = 900;
        Height = 620;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(18) };
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var refresh = new System.Windows.Controls.Button { Content = "刷新", Padding = new Thickness(14, 7, 14, 7) };
        refresh.Click += (_, _) => Refresh();
        var open = new System.Windows.Controls.Button
        {
            Content = "打开日志目录",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(8, 0, 0, 0)
        };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(_service.LogDirectory);
            Process.Start(new ProcessStartInfo(_service.LogDirectory) { UseShellExecute = true });
        };
        actions.Children.Add(refresh);
        actions.Children.Add(open);
        DockPanel.SetDock(actions, Dock.Top);
        root.Children.Add(actions);

        _content = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12
        };
        root.Children.Add(_content);
        Content = root;
        Refresh();
    }

    private void Refresh()
    {
        var lines = _service.ReadRecent();
        _content.Text = lines.Count == 0
            ? "暂时没有 Launcher 日志。"
            : string.Join(Environment.NewLine, lines);
        _content.ScrollToEnd();
    }
}
