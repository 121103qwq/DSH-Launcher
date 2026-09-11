using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;

namespace DshLauncher;

/// <summary>
/// 整合包版本不一致时的选择面板（work-log/78）：**先提示，再让用户选**——
/// ① 改用其它已安装实例作模板 ② 下载并安装所需版本（可用默认位置或自选）③ 取消。
///
/// 代码构建（无 XAML），与批次 A 的面板同源视觉。本类只做"选择"，不下载、不导入：
/// 下载与导入由调用方执行（保持 UI 与逻辑分离，便于自测分支）。
/// </summary>
public sealed class PackVersionMismatchWindow : Window
{
    private readonly System.Windows.Controls.RadioButton _useInstanceOption;
    private readonly System.Windows.Controls.RadioButton _downloadOption;
    private readonly System.Windows.Controls.ListBox _instanceList;
    private readonly System.Windows.Controls.Button _confirmButton;
    private readonly TextBlock _installDirectoryText;

    public PackVersionMismatchWindow(
        Window? owner,
        string requiredVersion,
        string? currentVersion,
        IReadOnlyList<(ManagerInstance Instance, bool Matches)> candidates,
        string installDirectory,
        Action<string> onPickDirectory)
    {
        if (owner is not null)
        {
            Owner = owner;
        }

        InstallDirectory = installDirectory;

        Title = "整合包版本与当前模板不一致";
        Width = 620;
        Height = 560;
        MinWidth = 520;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)FindResource("PageBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");

        var root = new StackPanel { Margin = new Thickness(24) };

        root.Children.Add(new TextBlock
        {
            Text = "整合包要求另一个 DSh 版本",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = $"整合包要求 DSh {requiredVersion}，当前模板实例是 {currentVersion ?? "未知"}。"
                + "导入需要一个版本匹配的运行目录：可以先改用其它已安装实例，或现在下载并安装所需版本。",
            Foreground = (System.Windows.Media.Brush)FindResource("WarningTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });

        _useInstanceOption = new System.Windows.Controls.RadioButton
        {
            Content = "改用其它已安装实例作模板",
            IsChecked = true,
            Margin = new Thickness(0, 16, 0, 0)
        };
        root.Children.Add(_useInstanceOption);

        _instanceList = new System.Windows.Controls.ListBox
        {
            Height = 140,
            Margin = new Thickness(24, 8, 0, 0)
        };
        var firstMatch = -1;
        for (var index = 0; index < candidates.Count; index++)
        {
            var (instance, matches) = candidates[index];
            _instanceList.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = $"{instance.Name} · {instance.DetectedVersion ?? "未知版本"} · {(matches ? "匹配" : "不匹配")}",
                IsEnabled = matches
            });
            if (matches && firstMatch < 0)
            {
                firstMatch = index;
            }
        }

        if (firstMatch >= 0)
        {
            _instanceList.SelectedIndex = firstMatch;
        }

        root.Children.Add(_instanceList);
        root.Children.Add(new TextBlock
        {
            Text = candidates.Any(item => item.Matches)
                ? "列表里只有版本匹配的实例可选。"
                : "本机没有版本匹配的已安装实例——请选择下面的下载方式。",
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(24, 6, 0, 0)
        });

        _downloadOption = new System.Windows.Controls.RadioButton
        {
            Content = $"下载并安装 DSh {requiredVersion}（走 npm 官方源或国内镜像）",
            Margin = new Thickness(0, 16, 0, 0),
            IsEnabled = firstMatch < 0 ? true : true
        };
        _downloadOption.Checked += (_, _) => RefreshConfirmState();
        _downloadOption.IsChecked = firstMatch < 0;
        root.Children.Add(_downloadOption);

        var directoryRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(24, 8, 0, 0)
        };
        _installDirectoryText = new TextBlock
        {
            Text = "安装位置：" + installDirectory,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
        };
        var pickButton = new System.Windows.Controls.Button
        {
            Content = "选择…",
            MinWidth = 72,
            Margin = new Thickness(8, 0, 0, 0)
        };
        pickButton.Click += (_, _) =>
        {
            onPickDirectory(InstallDirectory);
            _installDirectoryText.Text = "安装位置：" + InstallDirectory;
        };
        directoryRow.Children.Add(_installDirectoryText);
        directoryRow.Children.Add(pickButton);
        root.Children.Add(directoryRow);

        _useInstanceOption.Checked += (_, _) => RefreshConfirmState();
        _instanceList.SelectionChanged += (_, _) => RefreshConfirmState();

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        var cancel = new System.Windows.Controls.Button { Content = "取消", MinWidth = 96, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        _confirmButton = new System.Windows.Controls.Button
        {
            Content = "继续导入",
            MinWidth = 104,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
            Style = (Style)FindResource("PrimaryButton")
        };
        _confirmButton.Click += (_, _) =>
        {
            if (_downloadOption.IsChecked == true)
            {
                DownloadRequested = true;
                SelectedInstance = null;
            }
            else
            {
                DownloadRequested = false;
                SelectedInstance = (_instanceList.SelectedItem as System.Windows.Controls.ListBoxItem) is { IsEnabled: true }
                    && _instanceList.SelectedIndex >= 0
                    ? candidates[_instanceList.SelectedIndex].Instance
                    : null;
                if (SelectedInstance is null)
                {
                    return;
                }
            }

            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_confirmButton);
        root.Children.Add(buttons);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };

        RefreshConfirmState();
    }

    /// <summary>用户选中的候选实例（下载方式时为空）。</summary>
    public ManagerInstance? SelectedInstance { get; private set; }

    /// <summary>用户选择了"下载并安装"。</summary>
    public bool DownloadRequested { get; private set; }

    /// <summary>安装位置（默认取自设置，用户可用「选择…」改）。</summary>
    public string InstallDirectory { get; set; }

    private void RefreshConfirmState()
    {
        if (_confirmButton is null)
        {
            return;
        }

        var useInstance = _useInstanceOption.IsChecked == true;
        _confirmButton.IsEnabled = !useInstance
            || (_instanceList.SelectedItem as System.Windows.Controls.ListBoxItem) is { IsEnabled: true };
    }
}
