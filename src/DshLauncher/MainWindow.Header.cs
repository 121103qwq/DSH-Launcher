using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace DshLauncher;

public partial class MainWindow
{
    private bool _headerLayoutPending;

    private void InitializeHeaderLayout()
    {
        HeaderGrid.Loaded += (_, _) => QueueHeaderLayout();
        HeaderNavigationViewport.SizeChanged += (_, _) => QueueHeaderLayout();
        StartupBrandText.IsVisibleChanged += (_, _) => QueueHeaderLayout();
        ContextInstanceSelector.IsVisibleChanged += (_, _) => QueueHeaderLayout();
        VersionSettingsBackButton.IsVisibleChanged += (_, _) => QueueHeaderLayout();
    }

    private void QueueHeaderLayout()
    {
        if (_headerLayoutPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _headerLayoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _headerLayoutPending = false;
            if (!HeaderGrid.IsLoaded)
            {
                return;
            }

            var buttons = HeaderNavigation.Children.OfType<Button>().ToArray();
            ConfigureHeaderNavigation(buttons, HeaderNavigationViewport.ActualWidth);
            HeaderGrid.UpdateLayout();
            var labels = buttons.Select(button => ((StackPanel)button.Content).Children.OfType<TextBlock>().Last())
                .Concat(new[] { StartupBrandText, ContextInstanceNameText, VersionSettingsBackText, TaskCenterButtonText });
            AlignHeaderBaselines(HeaderGrid, StartupBrandText, labels);
        }));
    }

    internal static void ConfigureHeaderNavigation(IEnumerable<Button> buttons, double availableWidth)
    {
        var compact = availableWidth < 600;
        foreach (var button in buttons)
        {
            button.Padding = new Thickness(compact ? 8 : 15, 8, compact ? 8 : 15, 8);
            button.VerticalContentAlignment = VerticalAlignment.Center;
            if (button.Content is StackPanel content && content.Children.Count == 2)
            {
                content.VerticalAlignment = VerticalAlignment.Center;
                ((FrameworkElement)content.Children[0]).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                ((FrameworkElement)content.Children[0]).VerticalAlignment = VerticalAlignment.Center;
                ((FrameworkElement)content.Children[1]).VerticalAlignment = VerticalAlignment.Center;
            }
        }
    }

    internal static void AlignHeaderBaselines(FrameworkElement header, TextBlock brand, IEnumerable<TextBlock> labels)
    {
        // Measure a reference even when the brand is replaced by an instance selector.
        // Centering different font line boxes alone does not align their baselines.
        var reference = new TextBlock
        {
            Text = brand.Text,
            FontFamily = brand.FontFamily,
            FontSize = brand.FontSize,
            FontWeight = brand.FontWeight,
            FontStyle = brand.FontStyle,
            FontStretch = brand.FontStretch
        };
        reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var baseline = header.ActualHeight / 2 - reference.DesiredSize.Height / 2 + reference.BaselineOffset;
        var dpi = VisualTreeHelper.GetDpi(header).DpiScaleY;
        foreach (var label in labels)
        {
            if (label.Visibility != Visibility.Visible || label.ActualHeight <= 0)
            {
                continue;
            }

            if (label.RenderTransform is not TranslateTransform translation)
            {
                translation = new TranslateTransform();
                label.RenderTransform = translation;
            }

            var position = label.TransformToAncestor(header).Transform(new Point());
            var correction = translation.Y + baseline - position.Y - label.BaselineOffset;
            translation.Y = Math.Round(correction * dpi) / dpi;
        }

        // A newly assigned RenderTransform enters the visual transform during
        // arrange. Complete that pass before the first frame is presented.
        header.UpdateLayout();
    }
}
