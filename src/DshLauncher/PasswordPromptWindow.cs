using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DshLauncher;

internal sealed class PasswordPromptWindow : Window
{
    private readonly PasswordBox _passwordBox;
    private readonly PasswordBox? _confirmPasswordBox;
    private readonly TextBlock _errorText;

    private PasswordPromptWindow(string title, string prompt, bool confirmPassword)
    {
        Title = title;
        Width = 520;
        Height = confirmPassword ? 310 : 270;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "密码",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        _passwordBox = new PasswordBox { MinHeight = 32, Padding = new Thickness(7, 4, 7, 4) };
        panel.Children.Add(_passwordBox);

        if (confirmPassword)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "再次输入密码",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 6)
            });
            _confirmPasswordBox = new PasswordBox { MinHeight = 32, Padding = new Thickness(7, 4, 7, 4) };
            panel.Children.Add(_confirmPasswordBox);
        }

        _errorText = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(_errorText);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            IsCancel = true,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 0)
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "确定",
            IsDefault = true,
            Padding = new Thickness(18, 7, 18, 7)
        };
        if (System.Windows.Application.Current?.TryFindResource("PrimaryButton") is Style primaryButtonStyle)
        {
            ok.Style = primaryButtonStyle;
        }

        cancel.Click += (_, _) => DialogResult = false;
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static string? Show(
        Window? owner,
        string title,
        string prompt,
        bool confirmPassword)
    {
        var dialog = new PasswordPromptWindow(title, prompt, confirmPassword)
        {
            Owner = owner
        };
        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var accepted = dialog.ShowDialog() == true;
        var password = accepted ? dialog._passwordBox.Password : null;
        dialog.ClearPasswordBoxes();
        return password;
    }

    private void Accept()
    {
        if (_passwordBox.SecurePassword.Length == 0)
        {
            _errorText.Text = "密码不能为空。";
            _passwordBox.Focus();
            return;
        }

        if (_confirmPasswordBox is not null
            && !string.Equals(_passwordBox.Password, _confirmPasswordBox.Password, StringComparison.Ordinal))
        {
            _errorText.Text = "两次输入的密码不一致。";
            _confirmPasswordBox.Clear();
            _confirmPasswordBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void ClearPasswordBoxes()
    {
        _passwordBox.Clear();
        _confirmPasswordBox?.Clear();
    }
}
