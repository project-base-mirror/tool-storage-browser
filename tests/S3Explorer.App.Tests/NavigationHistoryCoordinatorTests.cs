using S3Explorer.App;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class NavigationHistoryCoordinatorTests
{
    [Fact]
    public void RecordTracksBackForwardAndIgnoresCurrentDuplicate()
    {
        var history = new NavigationHistoryCoordinator();
        var first = Location("bucket-a", "one/");
        var second = Location("bucket-a", "two/");

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.True(history.Record(first));
        Assert.False(history.Record(first));
        Assert.Equal(1, history.Count);

        Assert.True(history.Record(second));
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);

        Assert.True(history.TryMove(-1, out var moved));
        Assert.Equal(first, moved);
        Assert.False(history.CanGoBack);
        Assert.True(history.CanGoForward);
    }

    [Fact]
    public void RecordingAfterBackDropsForwardHistory()
    {
        var history = new NavigationHistoryCoordinator();
        var first = Location("bucket-a", "one/");
        var second = Location("bucket-a", "two/");
        var replacement = Location("bucket-b", "replacement/");

        history.Record(first);
        history.Record(second);
        Assert.True(history.TryMove(-1, out _));

        Assert.True(history.Record(replacement));

        Assert.Equal(2, history.Count);
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.False(history.TryMove(1, out _));
    }

    [Fact]
    public void OutOfRangeMoveLeavesHistoryPositionUnchanged()
    {
        var history = new NavigationHistoryCoordinator();
        var location = Location("bucket-a", string.Empty);
        history.Record(location);

        Assert.False(history.TryMove(-1, out _));
        Assert.False(history.TryMove(1, out _));
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Equal(1, history.Count);
    }

    private static S3Location Location(string bucket, string prefix) =>
        new("profile-a", bucket, prefix);
}
