using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DshLauncher.Models;
using Xunit;

namespace DshLauncher.UnitTests;

[Collection("WpfRendering")]
public sealed class ControlChromeTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AgentPageFromReferenceHasStyledScrollbarsAndRoundedCategories()
    {
        OnSta(() =>
        {
            var xml = new XElement(XDocument.Load(XamlTestResources.SourceFile("ExtensionWindow.xaml")).Root!);
            xml.Attribute(Xaml + "Class")!.Remove();
            foreach (var attribute in xml.Attributes().Where(attribute => attribute.Value.StartsWith("{StaticResource", StringComparison.Ordinal)))
                attribute.Value = attribute.Value.Replace("{StaticResource", "{DynamicResource", StringComparison.Ordinal);
            foreach (var attribute in xml.DescendantsAndSelf().Attributes().Where(attribute =>
                attribute.Name.LocalName is "Loaded" or "Unloaded" or "Click" or "Checked" or "Unchecked" or "SelectionChanged" or "TextChanged" or "MouseDoubleClick" or "MouseLeftButtonUp").ToArray())
                attribute.Remove();
            xml.AddFirst(new XElement(Wpf + "UserControl.Resources", ResourceElements()));
            XamlTestResources.NormalizeAssemblyNamespaces(xml);
            var page = (UserControl)XamlReader.Parse(xml.ToString());
            foreach (var name in new[] { "MarketplacePanel", "MarketplaceCategoryList", "InstallPluginButton", "AddMcpButton",
                "DshMarketHotReloadCheckBox", "ChatThemeSyncCheckBox", "ChatThemeSyncButton", "ChatThemeCapabilityText",
                "EnableButton", "DisableButton", "UpdateButton", "ProfileSelectorPanel" })
                ((UIElement)page.FindName(name)).Visibility = Visibility.Collapsed;
            ((UIElement)page.FindName("SkillMarketPanel")).Visibility = Visibility.Visible;
            var categories = (ListBox)page.FindName("SkillMarketCategoryList");
            categories.Visibility = Visibility.Visible;
            ((TextBlock)page.FindName("CurrentInstanceNameText")).Text = "Coding · 演示实例";
            ((TextBlock)page.FindName("CurrentInstanceDetailsText")).Text = "DSh 示例版本\n独立测试数据，不读取真实实例";
            ((ListBox)page.FindName("ExtensionList")).ItemsSource = new[]
            {
                new { Kind = "Workflow", Name = "Workflow", Description = "本地演示条目", Enabled = true }
            };
            ((ListBox)page.FindName("SkillMarketList")).ItemsSource = Enumerable.Range(1, 20).Select(index => new
            {
                Name = $"skill-demo-{index}", StarsText = "开发 · ★ 128", Repository = "example / skills",
                Description = "演示卡片：支持选择、滚动和按钮悬停，不执行安装或网络请求。",
                StatusText = "SKILL.md 已校验", ActionText = "安装", CanInstall = true
            }).ToArray();
            var shell = new Grid { Resources = page.Resources, Background = new SolidColorBrush(Color.FromRgb(238, 245, 253)) };
            var background = new Canvas();
            var overlay = new Canvas();
            shell.Children.Add(background);
            shell.Children.Add(page);
            shell.Children.Add(overlay);
            Layout(shell, 1180, 650);
            using var effects = new VisualEffectsController(new Window { Resources = page.Resources }, background, overlay);
            effects.Apply(new VisualEffectsSettings { Enabled = true, Material = VisualMaterial.LiquidGlass, AmbientMotion = false, Particles = false });
            Layout(shell, 1180, 650);
            var bars = Descendants<ScrollBar>(page).Where(bar => bar.Visibility == Visibility.Visible && bar.ActualHeight > 20).ToArray();
            Assert.True(bars.Length >= 2);
            Assert.All(bars, bar =>
            {
                Assert.InRange(bar.ActualWidth, 9, 11);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                Assert.NotNull(track.Thumb.Template.FindName("ThumbChrome", track.Thumb));
            });
            var categoryItem = (ListBoxItem)categories.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Equal(new CornerRadius(8), ((Border)categoryItem.Template.FindName("SelectionChrome", categoryItem)).CornerRadius);
            var output = Environment.GetEnvironmentVariable("DSH_VISUAL_RENDER_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                var bitmap = new RenderTargetBitmap(1180, 650, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(shell);
                Directory.CreateDirectory(output);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, "AgentChrome.png"));
                encoder.Save(stream);
            }
        });
    }

    [Fact]
    public void ActualMarketCategoryUsesRoundedSelectionAndHoverChrome()
    {
        OnSta(() =>
        {
            var document = XDocument.Load(XamlTestResources.SourceFile("ExtensionWindow.xaml"));
            var category = new XElement(document.Descendants().Single(element =>
                (string?)element.Attribute(Xaml + "Name") == "SkillMarketCategoryList"));
            category.Attribute("SelectionChanged")!.Remove();
            category.SetAttributeValue(XNamespace.Xmlns + "x", Xaml.NamespaceName);
            category.AddFirst(new XElement(Wpf + "ListBox.Resources", ResourceElements()));
            XamlTestResources.NormalizeAssemblyNamespaces(category);
            var list = (ListBox)XamlReader.Parse(category.ToString());
            Layout(list, 650, 40);
            var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var chrome = (Border)selected.Template.FindName("SelectionChrome", selected);
            Assert.Equal(new CornerRadius(8), chrome.CornerRadius);
            Assert.True(UiHover.GetEnabled(chrome));
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)list.Background).Color);
            Assert.Same(list.FindResource("RoundedFocusVisual"), selected.FocusVisualStyle);
        });
    }

    [Fact]
    public void RoundedScrollBarsKeepTrackDraggingAndWheelScrolling()
    {
        OnSta(() =>
        {
            foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
            {
                var bar = new ScrollBar
                {
                    Resources = LoadResources(), Orientation = orientation,
                    Minimum = 0, Maximum = 1000, Value = 100, ViewportSize = 100
                };
                Layout(bar, orientation == Orientation.Vertical ? 10 : 240, orientation == Orientation.Vertical ? 240 : 10);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                track.Thumb.ApplyTemplate();
                var thumbChrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                Assert.Equal(new CornerRadius(4), thumbChrome.CornerRadius);
                var before = bar.Value;
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(orientation == Orientation.Horizontal ? 15 : 0,
                    orientation == Orientation.Vertical ? 15 : 0) { RoutedEvent = Thumb.DragDeltaEvent });
                Assert.True(bar.Value > before);
                Assert.Equal(orientation == Orientation.Vertical, track.IsDirectionReversed);
            }

            var scroll = new ScrollViewer
            {
                Resources = LoadResources(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 1400, Background = Brushes.White }
            };
            Layout(scroll, 260, 180);
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
            scroll.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
        });
    }

    [Fact]
    public void FocusTooltipAndPasswordBoxUseRoundedTemplatesAndButtonHoverIsSeparateFromPress()
    {
        OnSta(() =>
        {
            var resources = LoadResources();
            var panel = new StackPanel { Resources = resources, Margin = new Thickness(20), Background = Brushes.White };
            var button = new Button { Content = "安装", Width = 110, Height = 40, Margin = new Thickness(4) };
            var tooltip = new ToolTip { Content = "最小化", IsOpen = false, Style = (Style)resources[typeof(ToolTip)] };
            var password = new PasswordBox { Width = 240, Height = 38, Margin = new Thickness(4) };
            panel.Children.Add(button);
            panel.Children.Add(password);
            Layout(panel, 320, 160);
            tooltip.ApplyTemplate();
            var toolBorder = Assert.IsType<Border>(VisualTreeHelper.GetChild(tooltip, 0));
            Assert.Equal(new CornerRadius(8), toolBorder.CornerRadius);
            Assert.Equal(new CornerRadius(8), ((Border)password.Template.FindName("PasswordChrome", password)).CornerRadius);
            Assert.Same(resources["RoundedFocusVisual"], button.FocusVisualStyle);
            var hover = (Grid)button.Template.FindName("ButtonHoverHost", button);
            var press = (Border)button.Template.FindName("ButtonChrome", button);
            Assert.True(UiHover.GetEnabled(hover));
            Assert.IsType<ScaleTransform>(press.RenderTransform);
            Assert.Same(hover, VisualTreeHelper.GetParent(press));
            var hitArea = (Grid)button.Template.FindName("ButtonHitArea", button);
            Assert.Same(hitArea, VisualTreeHelper.GetParent(hover));
            Assert.NotNull(hitArea.Background);
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.IsType<TransformGroup>(hover.RenderTransform);
            Assert.True(hitArea.RenderTransform.Value.IsIdentity);
            Assert.IsType<ScaleTransform>(press.RenderTransform);
            UiHover.Reset(hover);

            var output = Environment.GetEnvironmentVariable("DSH_VISUAL_RENDER_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                var preview = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(238, 245, 253)), Resources = resources, Margin = new Thickness(16) };
                preview.Children.Add(new TextBlock { Text = "控件样式检查", FontSize = 20, Margin = new Thickness(8) });
                preview.Children.Add(new ListBoxItem { Content = "全部 · 已选分类", IsSelected = true, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8) });
                preview.Children.Add(new Button { Content = "安装 / 刷新", Margin = new Thickness(8) });
                preview.Children.Add(new TextBlock { Text = "键盘焦点保留圆角描边，滚动条保留拖动与滚轮。", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
                Layout(preview, 420, 220);
                var bitmap = new RenderTargetBitmap(420, 220, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(preview);
                Directory.CreateDirectory(output);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, "ControlChrome.png"));
                encoder.Save(stream);
            }
        });
    }

    [Fact]
    public void MinimizeGlyphIsShortButItsClickTargetRemainsFullSize()
    {
        var document = XDocument.Load(XamlTestResources.SourceFile("MainWindow.xaml"));
        var button = document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "MinimizeButton");
        var glyph = button.Element(Wpf + "Path")!;
        Assert.Equal("42", (string?)button.Attribute("Width"));
        Assert.Equal("42", (string?)button.Attribute("Height"));
        Assert.Equal("12", (string?)glyph.Attribute("Width"));
        Assert.Equal("M 0,0 L 10,0", (string?)glyph.Attribute("Data"));
    }

    private static IEnumerable<XElement> ResourceElements() => XDocument.Load(XamlTestResources.SourceFile("App.xaml"))
        .Root!.Element(Wpf + "Application.Resources")!.Elements().Select(element => new XElement(element));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static ResourceDictionary LoadResources()
    {
        var dictionary = new XElement(Wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", Xaml.NamespaceName), ResourceElements());
        XamlTestResources.NormalizeAssemblyNamespaces(dictionary);
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("Control chrome tests")
                { WindowStyle = 0, ExtendedWindowStyle = 0x08000080, PositionX = -32000, PositionY = -32000, Width = 1, Height = 1 });
                source.RootVisual = new Border();
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
