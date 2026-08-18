using System.Windows;

namespace DshLauncher;

public partial class NewVersionWindow : Window
{
    public NewVersionWindow(
        Window? owner,
        IReadOnlyList<string> versions,
        string defaultVersion)
    {
        InitializeComponent();
        Owner = owner;
        VersionBox.ItemsSource = versions;
        VersionBox.SelectedItem = versions.FirstOrDefault(version =>
            string.Equals(version, defaultVersion, StringComparison.OrdinalIgnoreCase))
            ?? versions.FirstOrDefault();
        NameBox.Text = $"DSh {VersionBox.SelectedItem ?? defaultVersion}";
        NameBox.SelectAll();
        NameBox.Focus();
        VersionBox.SelectionChanged += (_, _) =>
        {
            if (NameBox.Text.StartsWith("DSh ", StringComparison.Ordinal))
            {
                NameBox.Text = $"DSh {VersionBox.SelectedItem}";
            }
        };
    }

    public string VersionName => NameBox.Text.Trim();

    public string DshVersion => VersionBox.SelectedItem?.ToString()?.Trim() ?? string.Empty;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VersionName) || string.IsNullOrWhiteSpace(DshVersion))
        {
            System.Windows.MessageBox.Show(this, "请输入版本名称并选择 DSh 版本。", "新建版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
