using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class HeaderLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(880, 1.0)]
    [InlineData(880, 1.25)]
    [InlineData(880, 1.5)]
    [InlineData(880, 1.75)]
    [InlineData(880, 2.0)]
    [InlineData(1180, 1.0)]
    [InlineData(1180, 1.5)]
    [InlineData(1180, 2.0)]
    public void ActualHeaderFitsAndAlignsEveryIdentityState(double width, double scale)
    {
        OnSta(() =>
        {
            var header = LoadHeader();
            VisualTreeHelper.SetRootDpi(header, new DpiScale(scale, scale));
            var brand = (TextBlock)header.FindName("StartupBrandText");
            var selector = (Border)header.FindName("ContextInstanceSelector");
            var back = (Button)header.FindName("VersionSettingsBackButton");
            var viewport = (Grid)header.FindName("HeaderNavigationViewport");
            var navigation = (StackPanel)header.FindName("HeaderNavigation");
            var actions = (StackPanel)header.FindName("HeaderWindowActions");
            var instanceName = (TextBlock)header.FindName("ContextInstanceNameText");
            instanceName.Text = new string('测', 120);
            var buttons = navigation.Children.OfType<Button>().ToArray();
            var labels = buttons.Select(button => ((StackPanel)button.Content).Children.OfType<TextBlock>().Last()).ToArray();

            foreach (var state in new[] { 0, 1, 2 })
            {
                brand.Visibility = state == 0 ? Visibility.Visible : Visibility.Collapsed;
                selector.Visibility = state == 1 ? Visibility.Visible : Visibility.Collapsed;
                back.Visibility = state == 2 ? Visibility.Visible : Visibility.Collapsed;
                Layout(header, width);
                MainWindow.ConfigureHeaderNavigation(buttons, viewport.ActualWidth);
                Layout(header, width);
                var identityLabel = state switch
                {
                    1 => instanceName,
                    2 => (TextBlock)header.FindName("VersionSettingsBackText"),
                    _ => brand
                };
                MainWindow.AlignHeaderBaselines(header, brand, labels.Append(identityLabel));

                FrameworkElement identity = state switch { 1 => selector, 2 => back, _ => brand };
                var identityBounds = Bounds(identity, header);
                var navigationBounds = Bounds(navigation, header);
                var actionBounds = Bounds(actions, header);
                Assert.True(identityBounds.Right <= navigationBounds.Left + 1);
                Assert.True(navigationBounds.Right <= actionBounds.Left + 1);
                Assert.True(navigation.ActualWidth <= viewport.ActualWidth + 1);
                Assert.Equal(7, buttons.Length);

                var baseline = Baseline(identityLabel, header);
                foreach (var label in labels)
                {
                    Assert.InRange(Math.Abs(Baseline(label, header) - baseline), 0, 1.1 / scale);
                }
            }
        });
    }

    [Fact]
    public void CompactNavigationRetainsAllLabelsAndRestoresIcons()
    {
        OnSta(() =>
        {
            var header = LoadHeader();
            var nav = (StackPanel)header.FindName("HeaderNavigation");
            var buttons = nav.Children.OfType<Button>().ToArray();
            MainWindow.ConfigureHeaderNavigation(buttons, 400);
            foreach (var button in buttons)
            {
                var content = (StackPanel)button.Content;
                Assert.Equal(Visibility.Collapsed, content.Children[0].Visibility);
                Assert.Equal(Visibility.Visible, content.Children[1].Visibility);
            }
            MainWindow.ConfigureHeaderNavigation(buttons, 800);
            Assert.All(buttons, button => Assert.Equal(Visibility.Visible, ((StackPanel)button.Content).Children[0].Visibility));
        });
    }

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width - 10, 72));
        element.Arrange(new Rect(0, 0, width - 10, 72));
        element.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, Visual parent) =>
        element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));

    private static double Baseline(TextBlock label, Visual parent) =>
        label.TransformToAncestor(parent).Transform(new Point()).Y + label.BaselineOffset;

    private static Grid LoadHeader()
    {
        var main = XDocument.Load(SourceFile("MainWindow.xaml"));
        var header = new XElement(main.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "HeaderGrid"));
        header.SetAttributeValue(XNamespace.Xmlns + "x", Xaml.NamespaceName);
        header.SetAttributeValue("TextElement.FontFamily", "Segoe UI");
        header.SetAttributeValue("UseLayoutRounding", "True");
        foreach (var attribute in header.DescendantsAndSelf().Attributes().Where(attribute =>
            attribute.Name.LocalName is "Click" or "MouseLeftButtonDown"
            || attribute.Value.StartsWith("{Binding", StringComparison.Ordinal)).ToArray())
        {
            attribute.Remove();
        }
        var app = XDocument.Load(SourceFile("App.xaml"));
        header.AddFirst(new XElement(Wpf + "Grid.Resources", app.Root!.Element(Wpf + "Application.Resources")!.Elements().Select(element => new XElement(element))));
        return (Grid)XamlReader.Parse(header.ToString());
    }

    private static string SourceFile(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "DshLauncher", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
