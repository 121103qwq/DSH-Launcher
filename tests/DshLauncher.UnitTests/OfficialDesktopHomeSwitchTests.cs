using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class OfficialDesktopHomeSwitchTests
{
    [Fact]
    public void NotRunningOrSameHomeIsNoConflict()
    {
        Assert.Null(ExternalDshHomeGuard.DescribeHomeSwitchConflict(Array.Empty<string>(), @"C:\h\a"));
        Assert.Null(ExternalDshHomeGuard.DescribeHomeSwitchConflict(new[] { @"C:\H\A\" }, @"c:\h\a"));
    }

    [Fact]
    public void DifferentHomeExplainsSingleInstance()
    {
        var message = ExternalDshHomeGuard.DescribeHomeSwitchConflict(new[] { @"C:\Users\u\.dsh" }, @"D:\versions\v1\home");
        Assert.NotNull(message);
        Assert.Contains(@"C:\Users\u\.dsh", message);
        Assert.Contains("单实例", message);
    }
}
