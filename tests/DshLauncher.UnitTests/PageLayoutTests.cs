using System.Xml.Linq;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class PageLayoutTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainPageHostHasFiniteViewportInsteadOfOuterScrollingStack()
    {
        var document = ReadXaml("MainWindow.xaml");
        var host = Named(document, "EmbeddedPageHost");
        var frame = Named(document, "MainPageFrame");

        Assert.Equal("Grid", frame.Name.LocalName);
        Assert.Equal("1", (string?)host.Attribute("Grid.Row"));
        Assert.Null(host.Attribute("MinHeight"));
        Assert.DoesNotContain(host.Ancestors(), element =>
            element.Name.LocalName is "StackPanel" or "ScrollViewer");
        Assert.Null(Named(document, "InstanceWorkspacePanel").Attribute("Height"));
    }

    [Theory]
    [InlineData("ExtensionWindow.xaml", "MarketplaceList")]
    [InlineData("ExtensionWindow.xaml", "SkillMarketList")]
    [InlineData("VersionControlWindow.xaml", "VersionList")]
    public void LongListsOwnTheirScrollingViewport(string file, string name)
    {
        var list = Named(ReadXaml(file), name);
        Assert.DoesNotContain(list.Ancestors(), element =>
            element.Name.LocalName is "StackPanel" or "ScrollViewer");
        Assert.Null(list.Attribute("Height"));
        Assert.Null(list.Attribute("MaxHeight"));
    }

    [Fact]
    public void ConversationTabsDoNotForceContentBelowTheWindow()
    {
        var tabs = ReadXaml("ConversationWindow.xaml").Descendants()
            .Single(element => element.Name.LocalName == "TabControl");
        Assert.Null(tabs.Attribute("MinHeight"));
        Assert.Equal("2", (string?)tabs.Attribute("Grid.Row"));
    }

    [Fact]
    public void InstalledListHasBoundedLayoutAndActionsRemainScrollableInShortWindows()
    {
        var document = ReadXaml("ExtensionWindow.xaml");
        var viewport = Named(document, "InstalledPanelViewport");
        Assert.Contains("ViewportHeight", (string?)viewport.Attribute("Height"));
        Assert.Equal("Auto", (string?)Named(document, "InstalledPanelScroll").Attribute("VerticalScrollBarVisibility"));
        Assert.Contains(Named(document, "ExtensionList").Ancestors(), element => element == viewport);
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XDocument ReadXaml(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "DshLauncher", file);
            if (File.Exists(path))
            {
                return XDocument.Load(path);
            }
        }

        throw new FileNotFoundException($"Repository XAML not found: {file}");
    }
}
