using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace DshLauncher;

internal sealed class PluginProgressWindow : Window
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly TextBox _detailsBox;
    private readonly Button _actionButton;
    private bool _canClose;

    public PluginProgressWindow(
        Window? owner,
        CancellationTokenSource cancellation,
        string title,
        string initialStatus)
    {
        _cancellation = cancellation;
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
            Height = 8,
            IsIndeterminate = true
        };
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
        Grid.SetRow(_progressBar, 1);
        Grid.SetRow(_detailsBox, 2);
        Grid.SetRow(_actionButton, 3);
        _actionButton.Click += (_, _) =>
        {
            if (_canClose)
            {
                Close();
                return;
            }

            _cancellation.Cancel();
            _statusText.Text = "正在取消 Plugin 操作…";
            _actionButton.IsEnabled = false;
        };

        grid.Children.Add(_statusText);
        grid.Children.Add(_progressBar);
        grid.Children.Add(_detailsBox);
        grid.Children.Add(_actionButton);
        Content = grid;
        WindowSizeHelper.FitInitialSize(this);
    }

    public void SetStatus(string message) => _statusText.Text = message;

    public void Complete(string message)
    {
        _canClose = true;
        _statusText.Text = message;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _actionButton.Content = "关闭";
        _actionButton.IsEnabled = true;
        Activate();
    }

    public void Fail(string message)
    {
        _canClose = true;
        _statusText.Text = "Plugin 操作失败";
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _detailsBox.Text = message;
        _detailsBox.Visibility = Visibility.Visible;
        _actionButton.Content = "关闭";
        _actionButton.IsEnabled = true;
        Height = Math.Min(420, SystemParameters.WorkArea.Height * 0.8);
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_canClose)
        {
            e.Cancel = true;
            _cancellation.Cancel();
            _statusText.Text = "正在取消 Plugin 操作…";
            _actionButton.IsEnabled = false;
            return;
        }

        base.OnClosing(e);
    }
}
