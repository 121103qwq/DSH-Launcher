using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DshLauncher.Models;
using DshLauncher.Services;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace DshLauncher;

internal sealed class PluginProgressWindow : Window
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _progressText;
    private readonly TextBox _detailsBox;
    private readonly Button _actionButton;
    private readonly WindowState _ownerInitialState;
    private readonly LauncherTaskHandle _task;
    private readonly CancellationTokenRegistration _taskCancellationRegistration;
    private bool _canClose;

    public PluginProgressWindow(
        Window? owner,
        CancellationTokenSource cancellation,
        string title,
        string initialStatus)
    {
        _cancellation = cancellation;
        _ownerInitialState = owner?.WindowState ?? WindowState.Normal;
        _task = LauncherTaskService.Shared.Begin(
            title,
            title.Contains("Skill", StringComparison.OrdinalIgnoreCase) ? "Skill" : "Plugin");
        _task.Report(5, initialStatus);
        _taskCancellationRegistration = _task.Token.Register(_cancellation.Cancel);
        Title = title;
        Width = 520;
        Height = 250;
        MinWidth = 420;
        MinHeight = 220;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Owner = owner;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _statusText = new TextBlock
        {
            Text = initialStatus,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        _progressBar = new ProgressBar
        {
            Height = 10,
            Minimum = 0,
            Maximum = 100,
            Value = 5
        };
        _progressText = new TextBlock
        {
            Text = "5%",
            Width = 46,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var progressRow = new Grid();
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_progressText, 1);
        progressRow.Children.Add(_progressBar);
        progressRow.Children.Add(_progressText);
        _detailsBox = new TextBox
        {
            Visibility = Visibility.Collapsed,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 14, 0, 0)
        };
        _actionButton = new Button
        {
            Content = "取消",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 16, 0, 0)
        };
        Grid.SetRow(progressRow, 1);
        Grid.SetRow(_detailsBox, 2);
        Grid.SetRow(_actionButton, 3);
        _actionButton.Click += (_, _) =>
        {
            if (_canClose)
            {
                Close();
                return;
            }

            _task.Cancel("正在取消操作…");
            _cancellation.Cancel();
            _statusText.Text = "正在取消 Plugin 操作…";
            _actionButton.IsEnabled = false;
        };

        grid.Children.Add(_statusText);
        grid.Children.Add(progressRow);
        grid.Children.Add(_detailsBox);
        grid.Children.Add(_actionButton);
        Content = grid;
        WindowSizeHelper.FitInitialSize(this);
    }

    public void SetStatus(string message)
    {
        _statusText.Text = message;
        _task.Report(statusMessage: message);
    }

    public void SetProgress(double percentage, string message)
    {
        var value = ClampProgress(percentage);
        _statusText.Text = message;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = value;
        _progressText.Text = $"{value:0}%";
        _task.Report(value, message);
    }

    public void SetIndeterminate(string message, string detail = "处理中")
    {
        _statusText.Text = message;
        _progressBar.IsIndeterminate = true;
        _progressText.Text = detail;
        _task.Report(progress: null, statusMessage: message);
    }

    public void SetDownloadProgress(SkillInstallProgress progress, string itemName)
    {
        if (!string.Equals(progress.Stage, "下载", StringComparison.Ordinal))
        {
            SetIndeterminate($"{progress.Stage} {itemName}…");
            return;
        }

        var receivedText = FormatBytes(progress.BytesReceived);
        if (progress.TotalBytes is > 0)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Maximum = progress.TotalBytes.Value;
            _progressBar.Value = Math.Min(progress.BytesReceived, progress.TotalBytes.Value);
            _progressText.Text = $"{progress.Percent ?? 0}%";
            _statusText.Text = $"正在下载 {itemName}… {receivedText} / {FormatBytes(progress.TotalBytes.Value)}";
            _task.Report(progress.Percent, _statusText.Text);
            return;
        }

        SetIndeterminate($"正在下载 {itemName}… 已接收 {receivedText}", "下载中");
    }

    public void SetPackageProgress(PluginCommandProgress progress, string message)
    {
        var completed = Math.Min(
            progress.Resolved,
            Math.Max(progress.Reused + progress.Downloaded, progress.Added));
        _statusText.Text = message;
        _progressBar.IsIndeterminate = progress.Resolved <= 0;
        _progressBar.Maximum = Math.Max(1, progress.Resolved);
        _progressBar.Value = Math.Max(0, completed);
        _progressText.Text = progress.Resolved <= 0
            ? "处理中"
            : $"{completed}/{progress.Resolved}";
        _detailsBox.Text = $"实际包进度：解析 {progress.Resolved}，复用 {progress.Reused}，下载 {progress.Downloaded}，添加 {progress.Added}";
        _detailsBox.Visibility = Visibility.Visible;
        _task.Report(
            progress.Resolved <= 0 ? null : completed * 100d / progress.Resolved,
            message);
    }

    public void Complete(string message, string? details = null)
    {
        _canClose = true;
        SetProgress(100, message);
        if (!string.IsNullOrWhiteSpace(details))
        {
            _detailsBox.Text = details;
            _detailsBox.Visibility = Visibility.Visible;
        }
        _actionButton.Content = "关闭";
        _actionButton.IsEnabled = true;
        _task.Complete(message);
        PresentResult();
    }

    public void Fail(string message)
    {
        _canClose = true;
        _statusText.Text = "操作失败";
        _progressBar.IsIndeterminate = false;
        _progressText.Text = "失败";
        _detailsBox.Text = message;
        _detailsBox.Visibility = Visibility.Visible;
        _actionButton.Content = "关闭";
        _actionButton.IsEnabled = true;
        Height = Math.Min(420, SystemParameters.WorkArea.Height * 0.8);
        _task.Fail(message);
        PresentResult();
    }

    public void Canceled(string message)
    {
        _canClose = true;
        _statusText.Text = message;
        _progressBar.IsIndeterminate = false;
        _progressText.Text = "已取消";
        _actionButton.Content = "关闭";
        _actionButton.IsEnabled = true;
        _task.Cancel(message);
        PresentResult();
    }

    internal static double ClampProgress(double percentage) =>
        Math.Clamp(double.IsFinite(percentage) ? percentage : 0, 0, 100);

    internal static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        if (value >= 1024L * 1024 * 1024) return $"{value / (1024d * 1024 * 1024):0.0} GB";
        if (value >= 1024L * 1024) return $"{value / (1024d * 1024):0.0} MB";
        if (value >= 1024L) return $"{value / 1024d:0.0} KB";
        return $"{value} B";
    }

    internal static bool ShouldRestoreOwner(WindowState initialState, WindowState currentState) =>
        initialState != WindowState.Minimized && currentState == WindowState.Minimized;

    private void PresentResult()
    {
        if (Owner is { } owner
            && ShouldRestoreOwner(_ownerInitialState, owner.WindowState))
        {
            owner.WindowState = _ownerInitialState;
            owner.Activate();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
            _task.Cancel("正在取消操作…");
            _cancellation.Cancel();
            _statusText.Text = "正在取消 Plugin 操作…";
            _actionButton.IsEnabled = false;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _taskCancellationRegistration.Dispose();
        base.OnClosed(e);
    }
}
