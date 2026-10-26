using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferRecoveryBoundaryTests
{
    [Fact]
    public void ResumeTruncatesOnlyUnconfirmedTail()
    {
        var checkpoint = new DownloadCheckpoint("file.part", 100, 200, "etag", "version");
        var decision = DownloadResumePlanner.Decide(true, 101, checkpoint, new(200, "etag", "version"));
        Assert.True(decision.Resume);
        Assert.False(decision.ResetTemporaryFile);
        Assert.Equal(100, decision.Offset);
    }

    [Fact]
    public void ShortLocalFileAndSparseCheckpointCannotResumeAsContiguous()
    {
        var checkpoint = new DownloadCheckpoint("file.part", 100, 200, "etag");
        Assert.True(DownloadResumePlanner.Decide(true, 99, checkpoint, new(200, "etag", null)).ResetTemporaryFile);
        var segmented = checkpoint with { SegmentSizeBytes = 100, CompletedSegments = [1] };
        segmented.Validate();
        Assert.True(DownloadResumePlanner.Decide(true, 200, segmented, new(200, "etag", null)).ResetTemporaryFile);
    }

    [Theory]
    [InlineData(167772160000L, 16777216L)]
    [InlineData(167772160001L, 16777217L)]
    [InlineData(5368709120000L, 536870912L)]
    public void EffectivePartSizeKeepsLargeFilesWithinPartLimit(long length, long expected)
    {
        var size = MultipartUploadPlanner.EffectivePartSize(length, 16L * 1024 * 1024);
        Assert.Equal(expected, size);
        Assert.InRange(MultipartUploadPlanner.BuildParts(length, size).Count, 1, 10_000);
    }

    [Fact]
    public void SegmentCheckpointRejectsDuplicateOrUnaccountedSegments()
    {
        var checkpoint = new DownloadCheckpoint("file", 4, 8, "etag")
        {
            SegmentSizeBytes = 4,
            CompletedSegments = [0]
        };
        checkpoint.Validate();
        Assert.Throws<ArgumentException>(() => (checkpoint with { CompletedSegments = [0, 0] }).Validate());
        Assert.Throws<ArgumentException>(() => (checkpoint with { CompletedBytes = 5 }).Validate());
        Assert.Throws<ArgumentException>(() => (checkpoint with { CompletedSegments = [2] }).Validate());
    }

    [Fact]
    public async Task SharedBudgetBoundsRequestsAndCancelledWaitDoesNotLeak()
    {
        var budget = new SharedTransferRequestBudget(1);
        using var held = await budget.EnterAsync(TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        var waiting = budget.EnterAsync(cancelled.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        using var next = await budget.EnterAsync(TestContext.Current.CancellationToken);
    }
}
