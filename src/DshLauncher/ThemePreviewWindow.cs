using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DshLauncher.Models;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace DshLauncher;

internal sealed class ThemePreviewWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ProgressBar _progress;
    private readonly TextBlock _message;
    private readonly Image _image;

    public ThemePreviewWindow(Window? owner, MarketplaceItem item)
    {
        Title = $"主题预览 · {item.Name}";
        Width = 820;
        Height = 640;
        MinWidth = 520;
        MinHeight = 400;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Owner = owner;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = item.Description,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        _progress = new ProgressBar
        {
            Height = 7,
            IsIndeterminate = true,
            Margin = new Thickness(0, 16, 0, 12)
        };
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var imageBorder = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            Padding = new Thickness(12),
            Child = _image
        };
        _message = new TextBlock
        {
            Text = "正在读取 GitHub README 中的预览图…",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 100, 0)
        };
        var close = new Button
        {
            Content = "关闭",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => Close();

        Grid.SetRow(_progress, 1);
        Grid.SetRow(imageBorder, 2);
        Grid.SetRow(_message, 3);
        Grid.SetRow(close, 3);
        root.Children.Add(heading);
        root.Children.Add(_progress);
        root.Children.Add(imageBorder);
        root.Children.Add(_message);
        root.Children.Add(close);
        Content = root;
        WindowSizeHelper.FitInitialSize(this);
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void SetPreview(ThemeReadmePreview preview)
    {
        _progress.Visibility = Visibility.Collapsed;
        _message.Text = preview.Message;
        if (!preview.HasImage)
        {
            _image.Source = null;
            return;
        }

        try
        {
            using var stream = new MemoryStream(preview.ImageBytes!, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _image.Source = bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException)
        {
            _image.Source = null;
            _message.Text = $"README 中找到了图片，但当前系统无法显示该格式：{ex.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        base.OnClosed(e);
    }
}
