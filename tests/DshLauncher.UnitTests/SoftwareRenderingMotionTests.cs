using Xunit;

namespace DshLauncher.UnitTests;

public sealed class SoftwareRenderingMotionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void ContinuousMotionNeedsHardwareRendering(int tier, bool expected) =>
        Assert.Equal(expected, DshLauncher.VisualEffectsController.AllowsContinuousMotion(tier));
}

public sealed class RenderCpuThrottleTests
{
    [Fact]
    public void CountsOnlyConsecutiveHighSamplesWhileAnimating()
    {
        Assert.Equal(1, DshLauncher.VisualEffectsController.NextHighCpuSamples(0, true, 1.9));
        Assert.Equal(2, DshLauncher.VisualEffectsController.NextHighCpuSamples(1, true, 1.9));
        Assert.Equal(0, DshLauncher.VisualEffectsController.NextHighCpuSamples(1, true, 0.1));
        Assert.Equal(0, DshLauncher.VisualEffectsController.NextHighCpuSamples(1, false, 1.9));
    }
}
