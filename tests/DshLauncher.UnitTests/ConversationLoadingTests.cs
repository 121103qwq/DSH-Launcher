using DshLauncher;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ConversationLoadingTests
{
    [Fact]
    public void StaleGenerationCannotCommitAfterNewerRefresh()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.False(ConversationWindow.IsCurrentLoad(
            requestedGeneration: 4,
            currentGeneration: 5,
            pageActive: true,
            cancellation.Token));
    }

    [Fact]
    public void UnloadedPageCannotCommitCompletedBackgroundSnapshot()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.False(ConversationWindow.IsCurrentLoad(
            requestedGeneration: 2,
            currentGeneration: 2,
            pageActive: false,
            cancellation.Token));
    }

    [Fact]
    public void CancelledSnapshotCannotCommitEvenWhenGenerationMatches()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(ConversationWindow.IsCurrentLoad(
            requestedGeneration: 2,
            currentGeneration: 2,
            pageActive: true,
            cancellation.Token));
    }

    [Fact]
    public void SelectionIsRetainedCaseInsensitivelyWhenPathStillExists()
    {
        var selected = ConversationWindow.FindSelectionPath(
            new[] { @"C:\DSH\sessions\B\session.jsonl", @"C:\DSH\sessions\A\session.jsonl" },
            @"c:\dsh\SESSIONS\a\SESSION.JSONL");

        Assert.Equal(@"C:\DSH\sessions\A\session.jsonl", selected);
    }

    [Fact]
    public void SelectionIsClearedWhenSnapshotNoLongerContainsPath()
    {
        var selected = ConversationWindow.FindSelectionPath(
            new[] { @"C:\DSH\sessions\B\session.jsonl" },
            @"C:\DSH\sessions\A\session.jsonl");

        Assert.Null(selected);
    }
}
