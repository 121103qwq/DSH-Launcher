using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ProcessTreeSnapshotTests
{
    [Fact]
    public void OneSnapshotCanResolveDifferentRootsWithoutCyclesOrDuplicates()
    {
        var snapshot = new ProcessTreeSnapshot(new Dictionary<int, string>(), new Dictionary<int, List<int>>
        {
            [1] = [2, 3], [2] = [4], [3] = [4], [4] = [1]
        });
        Assert.Equal(new[] { 1, 2, 3, 4 }, snapshot.FindProcessTree(1, CancellationToken.None));
        Assert.Equal(new[] { 3, 4, 1, 2 }, snapshot.FindProcessTree(3, CancellationToken.None));
        Assert.Equal(new[] { 99 }, snapshot.FindProcessTree(99, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => snapshot.FindProcessTree(1, new CancellationToken(true)));
    }

    [Fact]
    public void FreshSnapshotIncludesCurrentProcessAndItsTree()
    {
        var snapshot = InstanceResourceMonitor.CaptureProcessSnapshot(CancellationToken.None);
        Assert.True(snapshot.Names.ContainsKey(Environment.ProcessId));
        Assert.Contains(Environment.ProcessId, snapshot.FindProcessTree(Environment.ProcessId, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => InstanceResourceMonitor.CaptureProcessSnapshot(new CancellationToken(true)));
    }
}
