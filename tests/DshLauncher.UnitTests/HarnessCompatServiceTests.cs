using System.IO;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class HarnessCompatServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dsh-compat-" + Guid.NewGuid().ToString("N"));
    private readonly HarnessCompatService _service = new(() => new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ParsesOfficialUpdateFeed()
    {
        var feed = HarnessCompatService.ParseUpdateFeed(
            "provider: generic\r\nurl: https://download.deepseek.com/dsh-desk/feeds/win-x64/\r\nchannel: nightly\r\npublisherName:\r\n  - CN=x\r\n");
        Assert.Equal("generic", feed.Provider);
        Assert.Equal("nightly", feed.Channel);
        Assert.Equal("https://download.deepseek.com/dsh-desk/feeds/win-x64/", feed.Url);
        Assert.Contains("nightly", HarnessCompatService.DescribeFeed(feed));
    }

    [Fact]
    public void MissingFeedFileIsReportedNotThrown()
    {
        Assert.Null(HarnessCompatService.ReadUpdateFeed(Path.Combine(_root, "nope")));
        Assert.Contains("app-update.yml", HarnessCompatService.DescribeFeed(null));
    }

    [Fact]
    public void CopiesProviderFilesAndBacksUpTarget()
    {
        var source = Dir("official");
        var target = Dir("managed");
        File.WriteAllText(Path.Combine(source, ".credentials.yaml"), "new-creds");
        File.WriteAllText(Path.Combine(source, "settings.yaml"), "llm: {}");
        File.WriteAllText(Path.Combine(source, "sessions.txt"), "not copied");
        File.WriteAllText(Path.Combine(target, ".credentials.yaml"), "old-creds");

        var result = _service.CopyProviderConfiguration(source, target, false, false);

        Assert.Equal(2, result.CopiedFiles.Count);
        Assert.Equal("new-creds", File.ReadAllText(Path.Combine(target, ".credentials.yaml")));
        Assert.Equal("llm: {}", File.ReadAllText(Path.Combine(target, "settings.yaml")));
        Assert.False(File.Exists(Path.Combine(target, "sessions.txt")));
        var backup = Assert.Single(result.BackupFiles);
        Assert.Equal("old-creds", File.ReadAllText(backup));
        Assert.EndsWith(".bak-20261105-140000", backup);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RefusesWhenEitherHomeIsInUse(bool sourceInUse, bool targetInUse)
    {
        var source = Dir("a");
        File.WriteAllText(Path.Combine(source, "settings.yaml"), "x");
        var target = Dir("b");
        Assert.Throws<InvalidOperationException>(() =>
            _service.CopyProviderConfiguration(source, target, sourceInUse, targetInUse));
        Assert.False(File.Exists(Path.Combine(target, "settings.yaml")));
    }

    [Fact]
    public void RefusesSameHomeAndReportsEmptySource()
    {
        var home = Dir("same");
        Assert.Throws<InvalidOperationException>(() =>
            _service.CopyProviderConfiguration(home, home + Path.DirectorySeparatorChar, false, false));
        Assert.False(_service.CopyProviderConfiguration(home, Dir("other"), false, false).CopiedAnything);
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name, "数据 目录");
        Directory.CreateDirectory(path);
        return path;
    }
}
