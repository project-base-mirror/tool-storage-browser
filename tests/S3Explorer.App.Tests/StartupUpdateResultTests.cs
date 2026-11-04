using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class StartupUpdateResultTests
{
    [Fact]
    public async Task WaitsForResultWrittenAfterTheNewProcessStarts()
    {
        var expected = new UpdateInstallResult("completed", new Version(1, 2, 3), 0,
            "updated", Path.GetFullPath("test-update.log"), DateTimeOffset.UtcNow);
        var reads = 0;
        var delays = 0;
        var result = await MainForm.WaitForPendingUpdateResultAsync(
            () => ++reads == 4 ? expected : null,
            (_, _) => { delays++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(4, reads);
        Assert.Equal(3, delays);
    }

    [Fact]
    public async Task MissingResultStopsAfterTheStartupWindow()
    {
        var reads = 0;
        var delays = 0;
        var result = await MainForm.WaitForPendingUpdateResultAsync(
            () => { reads++; return null; },
            (_, _) => { delays++; return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(11, reads);
        Assert.Equal(10, delays);
    }

    [Fact]
    public async Task ClosingCancelsBeforeReadingOrConsumingAnotherResult()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MainForm.WaitForPendingUpdateResultAsync(
            () => { reads++; return null; },
            (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(1, reads);
    }
}
