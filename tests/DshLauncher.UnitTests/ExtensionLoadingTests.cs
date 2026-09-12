using DshLauncher.Models;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ExtensionLoadingTests
{
    [Fact]
    public void SkillFilterTrimsQueryAndAppliesCategoryWithoutChangingSnapshotOrder()
    {
        var snapshot = new[]
        {
            new SkillMarketItem("org/first", "First", "Documentation helper", 10, "main", null, true, 0, "SKILL.md", "文档"),
            new SkillMarketItem("org/second", "Second", "Build helper", 9, "main", null, true, 0, "SKILL.md", "文档"),
            new SkillMarketItem("org/third", "Third", "Build helper", 8, "main", null, true, 0, "SKILL.md", "其他")
        };

        var result = ExtensionWindow.FilterSkillMarketItems(snapshot, "  documentation ", "文档");

        var item = Assert.Single(result);
        Assert.Equal("First", item.Name);
    }

    [Fact]
    public void MarketplaceProjectionMatchesInstalledPluginThroughIndexedIdentity()
    {
        var snapshot = new[]
        {
            new MarketplaceItem(
                "community:demo",
                "Demo Plugin",
                "demo-plugin",
                "2.0.0",
                "A demo plugin",
                "demo-plugin",
                null,
                "开发",
                MarketplaceSourceKind.CommunityCatalog,
                "test",
                MarketplaceVerificationStatus.Unverified,
                "test")
        };
        var installed = new[]
        {
            new ExtensionEntry(
                "plugin:demo-plugin",
                ExtensionKind.Plugin,
                "demo-plugin",
                "1.0.0",
                "A demo plugin",
                "C:\\dsh-home\\profiles\\web\\package.json",
                true,
                true)
        };

        var rendered = ExtensionWindow.BuildMarketplaceItems(
            snapshot,
            query: null,
            sourceKind: null,
            sortOrder: MarketplaceSortOrder.Relevance,
            category: null,
            featuredOnly: false,
            installedPlugins: installed,
            canMutate: true,
            themeState: DshMarketThemeState.Unavailable("test"),
            instanceRunning: false,
            instanceAttached: false,
            mutating: false);

        var item = Assert.Single(rendered);
        Assert.True(item.IsInstalled);
        Assert.True(item.IsManaged);
        Assert.Equal("1.0.0", item.InstalledVersion);
        Assert.Equal(MarketplaceUpdateStatus.Available, item.UpdateStatus);
    }

    [Fact]
    public void MarketplaceProjectionMatchesGitHubInstallByDeclaredPackageName()
    {
        var snapshot = new[]
        {
            new MarketplaceItem(
                "github:omdsh-dev/dsh-at-file",
                "dsh-at-file",
                null,
                null,
                "A GitHub plugin",
                "github:omdsh-dev/dsh-at-file",
                "https://github.com/omdsh-dev/dsh-at-file",
                "开发",
                MarketplaceSourceKind.GitHubTopic,
                "test",
                MarketplaceVerificationStatus.Unverified,
                "test")
        };
        var installed = new[]
        {
            new ExtensionEntry(
                "plugin:dsh-at-file",
                ExtensionKind.Plugin,
                "dsh-at-file",
                "1.0.0",
                "A GitHub plugin",
                "C:\\dsh-home\\profiles\\web\\package.json",
                true,
                true)
        };

        var rendered = ExtensionWindow.BuildMarketplaceItems(
            snapshot,
            query: null,
            sourceKind: null,
            sortOrder: MarketplaceSortOrder.Relevance,
            category: null,
            featuredOnly: false,
            installedPlugins: installed,
            canMutate: true,
            themeState: DshMarketThemeState.Unavailable("test"),
            instanceRunning: false,
            instanceAttached: false,
            mutating: false);

        var item = Assert.Single(rendered);
        Assert.True(item.IsInstalled);
        Assert.True(item.IsManaged);
        Assert.Equal("1.0.0", item.InstalledVersion);
    }
}
