using System.IO;
using System.Windows;
using System.Windows.Controls;
using DshLauncher.Services;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace DshLauncher;

internal enum FirstRunDownloadSource
{
    Official,
    ChinaMirror
}

internal sealed record FirstRunSetupChoice(
    FirstRunDownloadSource Source,
    string? DshInstallDirectory);

internal sealed class FirstRunSetupWindow : Window
{
    private readonly TextBox _installDirectory;

    private FirstRunSetupWindow(
        string nodeStatus,
        string dshStatus,
        string? initialInstallDirectory,
        bool runtimeReady)
    {
        Title = "首次运行";
        Width = 600;
        Height = 430;
        MinWidth = 540;
        MinHeight = 400;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "准备第一个 DSh 版本",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = runtimeReady
                ? "运行环境已经就绪。继续后会创建一个使用独立 DSH_HOME 的干净版本并启动。"
                : "尚未创建版本。Launcher 会先准备缺少的 Node.js 和 DeepSeek Harness，再创建一个使用独立 DSH_HOME 的干净版本并启动。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 18)
        });

        var statusCard = new Border
        {
            Background = System.Windows.Media.Brushes.WhiteSmoke,
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14)
        };
        var statusPanel = new StackPanel();
        statusPanel.Children.Add(new TextBlock { Text = $"Node.js：{nodeStatus}", FontWeight = FontWeights.SemiBold });
        statusPanel.Children.Add(new TextBlock
        {
            Text = $"DeepSeek Harness：{dshStatus}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 7, 0, 0)
        });
        statusCard.Child = statusPanel;
        panel.Children.Add(statusCard);

        panel.Children.Add(new TextBlock
        {
            Text = "DeepSeek Harness 安装位置",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 7)
        });
        var locationRow = new Grid();
        locationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _installDirectory = new TextBox
        {
            Text = initialInstallDirectory ?? string.Empty,
            Height = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "留空时使用当前 Node.js 的 npm 全局默认位置"
        };
        var browse = new Button
        {
            Content = "选择文件夹",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(8, 0, 0, 0)
        };
        browse.Click += Browse_Click;
        Grid.SetColumn(browse, 1);
        locationRow.Children.Add(_installDirectory);
        locationRow.Children.Add(browse);
        panel.Children.Add(locationRow);
        panel.Children.Add(new TextBlock
        {
            Text = "这里仅控制 DSh Runtime 的安装位置；每个版本的 Plugin、Skill、Provider、设置和对话仍保存在各自的 DSH_HOME。",
            Foreground = System.Windows.Media.Brushes.DimGray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        });

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        var cancel = new Button
        {
            Content = "稍后处理",
            IsCancel = true,
            Padding = new Thickness(13, 8, 13, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);

        if (!runtimeReady)
        {
            var mirror = new Button
            {
                Content = "使用国内镜像",
                Padding = new Thickness(13, 8, 13, 8),
                Margin = new Thickness(0, 0, 8, 0)
            };
            mirror.Click += (_, _) => Complete(FirstRunDownloadSource.ChinaMirror);
            buttons.Children.Add(mirror);
        }

        var primary = new Button
        {
            Content = runtimeReady ? "创建并启动" : "使用官方源",
            IsDefault = true,
            Padding = new Thickness(13, 8, 13, 8),
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton")
        };
        primary.Click += (_, _) => Complete(FirstRunDownloadSource.Official);
        buttons.Children.Add(primary);
        panel.Children.Add(buttons);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
    }

    public FirstRunSetupChoice? Choice { get; private set; }

    public static FirstRunSetupChoice? Show(
        Window owner,
        string nodeStatus,
        string dshStatus,
        string? initialInstallDirectory,
        bool runtimeReady)
    {
        var dialog = new FirstRunSetupWindow(
            nodeStatus,
            dshStatus,
            initialInstallDirectory,
            runtimeReady)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true ? dialog.Choice : null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择 DeepSeek Harness 安装位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_installDirectory.Text)
                ? _installDirectory.Text
                : string.Empty
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            _installDirectory.Text = dialog.SelectedPath;
        }
    }

    private void Complete(FirstRunDownloadSource source)
    {
        try
        {
            Choice = new FirstRunSetupChoice(
                source,
                DshInstallService.NormalizeInstallDirectory(_installDirectory.Text));
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "安装位置无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
