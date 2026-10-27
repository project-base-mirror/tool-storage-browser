using Xunit;

namespace S3Explorer.App.Tests;

public sealed class WaitForExitTests
{
    [Fact]
    public async Task WaitKeepsOriginalCompletionAndCancellationInsteadOfShutdownTimeout()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = MainForm.WaitForTransfersBeforeExitAsync(token => pending.Task.WaitAsync(token), cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(pending.Task.IsCompleted); // Returning to UI does not stop the transfer.
        pending.SetResult();
        await MainForm.WaitForTransfersBeforeExitAsync(_ => pending.Task, TestContext.Current.CancellationToken);
    }
}
