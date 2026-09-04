using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;
using DshLauncher.Services;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Button = System.Windows.Controls.Button;

namespace DshLauncher;

internal sealed class RuntimeProgressWindow : Window
{
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly Button _cancelButton;
    private readonly CancellationTokenSource _cancellation;
    private readonly LauncherTaskHandle _task;
    private readonly CancellationTokenRegistration _taskCancellationRegistration;
    private bool _isInstallPhase;
    private bool _taskFinished;

    public RuntimeProgressWindow(
        Window? owner,
        CancellationTokenSource cancellation,
        string title = "准备运行环境",
        string hintText = "准备期间请勿关闭窗口。安装 Node.js 时会弹出系统授权确认。")
    {
        _cancellation = cancellation;
        _task = LauncherTaskService.Shared.Begin(title, GetTaskCategory(title));
        _task.Report(0, title);
        _taskCancellationRegistration = _task.Token.Register(_cancellation.Cancel);
        Title = title;
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
            Text = hintText,
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
        Closing += (_, eventArgs) => eventArgs.Cancel = !IsCloseAllowed(_isInstallPhase);
        panel.Children.Add(_statusText);
        panel.Children.Add(_progressBar);
        panel.Children.Add(hint);
        panel.Children.Add(_cancelButton);
        Content = panel;
        Closed += (_, _) =>
        {
            if (!_taskFinished)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    CancelTask("任务已取消。");
                }
                else
                {
                    FailTask("任务窗口已结束，但操作没有报告完成状态。");
                }
            }

            _taskCancellationRegistration.Dispose();
            _cancellation.Cancel();
        };
    }

    public void SetStatus(string text)
    {
        _statusText.Text = text;
        _task.Report(statusMessage: text);
    }

    public void SetIndeterminate(bool indeterminate)
    {
        _progressBar.IsIndeterminate = indeterminate;
        _progressBar.Value = 0;
    }

    public void SetDownloadProgress(NodeDownloadProgress progress)
        => SetDownloadProgress(progress, "Node.js 安装程序");

    public void SetDownloadProgress(NodeDownloadProgress progress, string itemName)
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = progress.Percent ?? 0;
        _statusText.Text = $"正在下载 {itemName}… {progress.BytesText}（{progress.PercentText}）";
        _task.Report(progress.Percent, _statusText.Text);
    }

    public void SetProgress(int completed, int total, string text)
    {
        _progressBar.IsIndeterminate = total <= 0;
        _progressBar.Maximum = Math.Max(1, total);
        _progressBar.Value = Math.Clamp(completed, 0, Math.Max(1, total));
        _statusText.Text = text;
        _task.Report(total <= 0 ? null : completed * 100d / total, text);
    }

    internal static bool IsCloseAllowed(bool installPhase) => !installPhase;

    public void SetInstallPhase(bool installing, string? status = null)
    {
        _isInstallPhase = installing;
        _cancelButton.IsEnabled = !installing;
        _task.SetCancelable(!installing);
        if (installing)
        {
            _statusText.Text = status ?? "Node.js 系统安装正在进行，请等待安装完成。";
            _task.Report(statusMessage: _statusText.Text);
        }
    }

    public void CompleteTask(string message)
    {
        if (_taskFinished)
        {
            return;
        }

        _taskFinished = true;
        _task.Complete(message);
    }

    public void FailTask(string message)
    {
        if (_taskFinished)
        {
            return;
        }

        _taskFinished = true;
        _task.Fail(message);
    }

    public void CancelTask(string message)
    {
        if (_taskFinished)
        {
            return;
        }

        _taskFinished = true;
        _task.Cancel(message);
    }

    private static string GetTaskCategory(string title)
    {
        if (title.Contains("扫描", StringComparison.Ordinal)) return "扫描";
        if (title.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) return "Launcher";
        if (title.Contains("Desktop", StringComparison.OrdinalIgnoreCase)) return "下载与安装";
        return "运行环境";
    }
}
