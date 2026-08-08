using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void SecondaryInstanceSignalsPrimary()
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        using var primary = SingleInstanceCoordinator.Acquire(key);
        using var activated = new ManualResetEventSlim();
        primary.StartListening(activated.Set);

        using var secondary = SingleInstanceCoordinator.Acquire(key);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReleasedInstanceKeyCanBeAcquiredAgain()
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        var first = SingleInstanceCoordinator.Acquire(key);
        Assert.True(first.IsPrimary);
        first.Dispose();

        using var replacement = SingleInstanceCoordinator.Acquire(key);

        Assert.True(replacement.IsPrimary);
    }

    [Fact]
    public void SecondaryCannotStartActivationListener()
    {
        var key = "test." + Guid.NewGuid().ToString("N");
        using var primary = SingleInstanceCoordinator.Acquire(key);
        using var secondary = SingleInstanceCoordinator.Acquire(key);

        Assert.Throws<InvalidOperationException>(() => secondary.StartListening(() => { }));
    }
}
