using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DshLauncher.Models;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class VisualAppearanceRenderingTests
{
    [Fact]
    public void RealMainWindowMarkupRendersDistinctMaterialsWithoutRunningLauncherServices()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = LoadMainContent();
                var window = new Window { Resources = root.Resources };
                using var source = new HwndSource(new HwndSourceParameters("Visual material render test")
                {
                    WindowStyle = 0, ExtendedWindowStyle = 0x08000080,
                    PositionX = -32000, PositionY = -32000, Width = 1180, Height = 720
                });
                source.RootVisual = root;
                Layout(root);
                var header = (Grid)root.FindName("HeaderGrid");
                var navigation = (StackPanel)root.FindName("HeaderNavigation");
                var brand = (TextBlock)root.FindName("StartupBrandText");
                var buttons = navigation.Children.OfType<Button>().ToArray();
                MainWindow.ConfigureHeaderNavigation(buttons, ((Grid)root.FindName("HeaderNavigationViewport")).ActualWidth);
                Layout(root);
                MainWindow.AlignHeaderBaselines(header, brand,
                    buttons.Select(button => ((StackPanel)button.Content).Children.OfType<TextBlock>().Last()).Append(brand));
                using var effects = new VisualEffectsController(window,
                    (Canvas)root.FindName("VisualBackdropLayer"), (Canvas)root.FindName("VisualInteractionLayer"));

                var fingerprints = new HashSet<string>();
                foreach (var material in Enum.GetValues<VisualMaterial>())
                {
                    effects.Apply(new VisualEffectsSettings { Enabled = true, Material = material });
                    effects.Resume();
                    for (var frame = 0; frame < 240; frame++) effects.AdvanceForTest(1d / 60d);
                    Layout(root);
                    var bitmap = new RenderTargetBitmap(1180, 720, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    if (material == VisualMaterial.LiquidGlass)
                    {
                        var highlight = Assert.IsType<LinearGradientBrush>(window.Resources["SurfaceHighlightBrush"]);
                        Assert.False(highlight.IsFrozen);
                        var before = highlight.StartPoint;
                        effects.AdvanceForTest(0.1);
                        Assert.NotEqual(before, highlight.StartPoint);
                    }
                    var pixels = new byte[1180 * 720 * 4];
                    bitmap.CopyPixels(pixels, 1180 * 4, 0);
                    Assert.Contains(pixels, value => value != 0);
                    fingerprints.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                    var output = Environment.GetEnvironmentVariable("DSH_VISUAL_RENDER_DIR");
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Directory.CreateDirectory(output);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(output, material + ".png"));
                        encoder.Save(stream);
                    }
                }
                Assert.Equal(3, fingerprints.Count);
                Assert.Null(((Grid)root.FindName("MainPageFrame")).Effect);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(1180, 720));
        root.Arrange(new Rect(0, 0, 1180, 720));
        root.UpdateLayout();
    }

    private static Border LoadMainContent()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load(SourceFile("MainWindow.xaml"));
        var root = new XElement(main.Root!.Elements(wpf + "Border").Single());
        root.SetAttributeValue(XNamespace.Xmlns + "x", xaml.NamespaceName);
        root.SetAttributeValue(XNamespace.Xmlns + "controls", "clr-namespace:DshLauncher.Controls;assembly=DSH Launcher");
        foreach (var element in root.DescendantsAndSelf().Where(element => element.Name.NamespaceName == "clr-namespace:DshLauncher.Controls"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:DshLauncher.Controls;assembly=DSH Launcher");
        root.SetAttributeValue("TextElement.FontFamily", "Segoe UI");
        root.SetAttributeValue("UseLayoutRounding", "True");
        var eventNames = new HashSet<string> { "Click", "SelectionChanged", "MouseDoubleClick", "MouseLeftButtonDown", "DragDelta", "TextChanged", "Loaded", "Unloaded" };
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(attribute => eventNames.Contains(attribute.Name.LocalName)).ToArray())
            attribute.Remove();
        var app = XDocument.Load(SourceFile("App.xaml"));
        root.AddFirst(new XElement(wpf + "Border.Resources",
            app.Root!.Element(wpf + "Application.Resources")!.Elements().Select(element => new XElement(element)),
            main.Root.Element(wpf + "Window.Resources")!.Elements().Select(element => new XElement(element))));
        var result = (Border)XamlReader.Parse(root.ToString());
        result.DataContext = new
        {
            SelectedInstanceName = "演示实例",
            SelectedInstanceDescription = "独立工作区 · 示例内容",
            SelectedInstanceStatus = "已停止",
            SelectedInstanceStatusBrush = Brushes.SlateGray,
            NoInstancesVisibility = Visibility.Collapsed,
            InstancesVisibility = Visibility.Visible,
            DesktopShellVisibility = Visibility.Collapsed,
            LauncherStartVisibility = Visibility.Visible,
            PageNoticeVisibility = Visibility.Collapsed,
            PageNoticeDetailVisibility = Visibility.Collapsed,
            Instances = Array.Empty<object>()
        };
        return result;
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
}
