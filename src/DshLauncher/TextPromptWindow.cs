using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Application = System.Windows.Application;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace DshLauncher;

internal sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    private TextPromptWindow(string title, string prompt, string initialValue)
    {
        Title = title;
        Width = 520;
        Height = 210;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _input = new TextBox { Text = initialValue, MinHeight = 30 };
        panel.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var ok = new Button { Content = "确定", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        cancel.Click += (_, _) => DialogResult = false;
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static string? Show(Window? owner, string title, string prompt, string initialValue = "")
    {
        var dialog = new TextPromptWindow(title, prompt, initialValue) { Owner = owner };
        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog.ShowDialog() == true ? dialog._input.Text.Trim() : null;
    }
}
