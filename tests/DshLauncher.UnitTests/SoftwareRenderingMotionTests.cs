using Xunit;

namespace DshLauncher.UnitTests;

public sealed class SoftwareRenderingMotionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ContinuousMotionNeedsHardwareRendering(int tier, bool expected) =>
        Assert.Equal(expected, DshLauncher.VisualEffectsController.AllowsContinuousMotion(tier));
}
