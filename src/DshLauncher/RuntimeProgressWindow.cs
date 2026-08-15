using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Button = System.Windows.Controls.Button;

namespace DshLauncher;

internal sealed class RuntimeProgressWindow : Window
{
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly Button _cancelButton;
    private readonly CancellationTokenSource _cancellation;

    public RuntimeProgressWindow(Window? owner, CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
        Title = "准备运行环境";
        Width = 480;
        Height = 210;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Owner = owner;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22) };
        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        _progressBar = new ProgressBar
        {
            Height = 8,
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };
        var hint = new TextBlock
        {
            Text = "准备期间请勿关闭窗口。安装 Node.js 时会弹出系统授权确认。",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _cancelButton = new Button
        {
            Content = "取消",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _cancelButton.Click += (_, _) => _cancellation.Cancel();
        panel.Children.Add(_statusText);
        panel.Children.Add(_progressBar);
        panel.Children.Add(hint);
        panel.Children.Add(_cancelButton);
        Content = panel;
        Closed += (_, _) => _cancellation.Cancel();
    }

    public void SetStatus(string text) => _statusText.Text = text;

    public void SetIndeterminate(bool indeterminate)
    {
        _progressBar.IsIndeterminate = indeterminate;
        _progressBar.Value = 0;
    }

    public void SetDownloadProgress(NodeDownloadProgress progress)
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = progress.Percent ?? 0;
        _statusText.Text = $"正在下载 Node.js 安装程序… {progress.BytesText}（{progress.PercentText}）";
    }

    public void SetInstallPhase(bool installing)
    {
        _cancelButton.IsEnabled = !installing;
        if (installing)
        {
            _statusText.Text = "Node.js 安装已开始，安装阶段不可安全取消，请等待完成…";
        }
    }
}
