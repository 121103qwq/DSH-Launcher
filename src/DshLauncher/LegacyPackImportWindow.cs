using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

/// <summary>
/// 旧 v1 整合包（<c>.dshpack</c>）的窗口内确认面板（批次 A 收口）。
///
/// 与规范路径的 <see cref="PackImportWindow"/> 视觉同源（同一批颜色/字体令牌 + 主按钮样式），
/// 但内容按 v1 预览能提供的信息量来显示：v1 的预览对象**不含文件清单**，所以只列计数，
/// 并如实说明"旧格式不带清单"。
///
/// 代码构建（无独立 XAML）：面板结构简单，避免再加一个 XAML 文件。
/// </summary>
public sealed class LegacyPackImportWindow : Window
{
    public LegacyPackImportWindow(Window? owner, DshPackPreview preview)
    {
        if (owner is not null)
        {
            Owner = owner;
        }

        Title = "导入整合包（旧格式）";
        Width = 560;
        Height = 520;
        MinWidth = 460;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)FindResource("PageBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");

        var grid = new Grid { Margin = new Thickness(24) };
        for (var index = 0; index < 4; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = index == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });
        }

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "导入整合包（旧格式 .dshpack）",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(preview.Description)
                ? "DSH Launcher 旧格式整合包"
                : preview.Description,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var createdText = preview.CreatedAt is { } created
            ? created.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "未标记";

        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = $"将创建的新版本：{preview.Name}", TextWrapping = TextWrapping.Wrap });
        card.Children.Add(new TextBlock
        {
            Text = $"DSh：{preview.DshVersion ?? "未标记"} · 创建于 {createdText}",
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0)
        });
        card.Children.Add(new TextBlock
        {
            Text = "导入只新建独立版本，不覆盖任何现有版本。",
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var cardBorder = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("CardBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 16, 0, 0),
            Child = card
        };
        Grid.SetRow(cardBorder, 1);
        grid.Children.Add(cardBorder);

        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = "包内内容（按类别计数）", FontSize = 12, FontWeight = FontWeights.SemiBold });
        foreach (var line in new[]
                 {
                     $"Plugins：{preview.PluginCount}",
                     $"Skills：{preview.SkillCount}",
                     $"Agent Presets：{preview.AgentPresetCount}",
                     $"Providers：{preview.ProviderCount}",
                     $"Workflow：{preview.Workflow ?? "无"}",
                     "需联网下载：无（旧格式不含外部下载项）",
                     "旧格式不带文件清单，导入后可在实例目录查看实际落盘内容。"
                 })
        {
            details.Children.Add(new TextBlock
            {
                Text = "· " + line,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 16, 0, 0),
            Content = details
        };
        Grid.SetRow(scroller, 2);
        grid.Children.Add(scroller);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new System.Windows.Controls.Button { Content = "取消", MinWidth = 96, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var confirm = new System.Windows.Controls.Button
        {
            Content = "导入",
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
            Style = (Style)FindResource("PrimaryButton")
        };
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        Content = grid;
    }
}
