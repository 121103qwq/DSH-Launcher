using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfApplication = System.Windows.Application;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;

namespace DshLauncher;

/// <summary>
/// 应用自绘的确认 / 提示对话框（变更集 122，work-log/105）：替换系统灰色 MessageBox。
/// API 与 <see cref="System.Windows.MessageBox"/> 同形（同样的 <see cref="MessageBoxButton"/> /
/// <see cref="MessageBoxImage"/> 入参与 <see cref="MessageBoxResult"/> 返回），因此调用点只需换类名。
/// 外观用应用资源：卡片底 + 圆角 + 边框 + 阴影，主按钮用 PrimaryButton 样式。
/// </summary>
public static class AppDialog
{
    public static MessageBoxResult Show(string messageBoxText) =>
        Show(null, messageBoxText, string.Empty, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        Show(null, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Show(null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption) =>
        Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button) =>
        Show(owner, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        Show(owner, messageBoxText, caption, button, icon, DefaultResult(button));

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        var window = new DialogWindow(messageBoxText ?? string.Empty, caption ?? string.Empty, button, icon, defaultResult);
        var target = owner ?? WpfApplication.Current?.MainWindow;
        if (target is not null && !ReferenceEquals(target, window) && target.IsVisible)
        {
            window.Owner = target;
        }

        window.ShowDialog();
        return window.Result;
    }

    private static MessageBoxResult DefaultResult(MessageBoxButton button) => button switch
    {
        MessageBoxButton.OKCancel => MessageBoxResult.OK,
        MessageBoxButton.YesNo => MessageBoxResult.Yes,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Yes,
        _ => MessageBoxResult.OK
    };

    private sealed class DialogWindow : Window
    {
        private readonly MessageBoxButton _button;
        private readonly MessageBoxResult _defaultResult;
        private readonly (string Text, MessageBoxResult Result, bool Primary)[] _options;

        public MessageBoxResult Result { get; private set; }

        public DialogWindow(
            string message,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            _button = button;
            _defaultResult = defaultResult;
            _options = button switch
            {
                MessageBoxButton.OKCancel => new[] { ("确定", MessageBoxResult.OK, true), ("取消", MessageBoxResult.Cancel, false) },
                MessageBoxButton.YesNo => new[] { ("是", MessageBoxResult.Yes, true), ("否", MessageBoxResult.No, false) },
                MessageBoxButton.YesNoCancel => new[]
                {
                    ("是", MessageBoxResult.Yes, true), ("否", MessageBoxResult.No, false), ("取消", MessageBoxResult.Cancel, false)
                },
                _ => new[] { ("确定", MessageBoxResult.OK, true) }
            };

            Title = string.IsNullOrWhiteSpace(caption) ? "DSH Launcher" : caption;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = WpfBrushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MaxWidth = 560;

            var stack = new StackPanel { Margin = new Thickness(0) };
            stack.Children.Add(BuildHeader(caption, icon));
            stack.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                MaxWidth = 460
            });
            stack.Children.Add(BuildButtons());

            Content = new Border
            {
                Background = Res("CardBrush", WpfBrushes.White),
                BorderBrush = Res("LineBrush", WpfBrushes.Gainsboro),
                BorderThickness = new Thickness(1),
                CornerRadius = Res("CardCornerRadius", new CornerRadius(12)),
                Padding = new Thickness(20),
                Effect = WpfApplication.Current?.TryFindResource("CardShadow") as Effect,
                Child = stack
            };

            ((Border)Content).MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    DragMove();
                }
            };
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    CloseWith(CancelResult());
                }
                else if (e.Key == Key.Enter)
                {
                    CloseWith(_defaultResult);
                }
            };
        }

        private UIElement BuildHeader(string caption, MessageBoxImage icon)
        {
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var (glyph, color) = icon switch
            {
                MessageBoxImage.Warning => ("⚠", "#B45309"),
                MessageBoxImage.Error => ("⛔", "#CE2111"),
                MessageBoxImage.Question => ("❓", "#1D4ED8"),
                MessageBoxImage.Information => ("ℹ", "#1D4ED8"),
                _ => (string.Empty, "#1D4ED8")
            };
            if (glyph.Length > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontSize = 15,
                    Margin = new Thickness(0, 0, 8, 0),
                    Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color))
                });
            }

            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(caption) ? "提示" : caption,
                FontSize = 15,
                FontWeight = System.Windows.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            return row;
        }

        private UIElement BuildButtons()
        {
            var panel = new WrapPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            foreach (var option in _options)
            {
                var button = new WpfButton
                {
                    Content = option.Text,
                    MinWidth = 84,
                    Padding = new Thickness(14, 7, 14, 7),
                    Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = option.Result == _defaultResult,
                    IsCancel = option.Result == CancelResult()
                };
                if (option.Primary)
                {
                    if (WpfApplication.Current?.TryFindResource("PrimaryButton") is Style primary)
                    {
                        button.Style = primary;
                    }
                }

                button.Click += (_, _) => CloseWith(option.Result);
                panel.Children.Add(button);
            }

            return panel;
        }

        private MessageBoxResult CancelResult() => _button == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        private void CloseWith(MessageBoxResult result)
        {
            Result = result;
            DialogResult = result != CancelResult() || _button == MessageBoxButton.OK;
            Close();
        }

        private static T Res<T>(string key, T fallback)
        {
            var value = WpfApplication.Current?.TryFindResource(key);
            return value is T typed ? typed : fallback;
        }
    }
}
