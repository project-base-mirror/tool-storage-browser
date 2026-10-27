using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferDestinationReservationTests
{
    [Theory]
    [InlineData("a:b.txt", "a_x003A_b.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("", "_empty_")]
    [InlineData("trailing.", "trailing_x002E_")]
    public void EscapedSegmentsCannotAliasLiteralEscapeNames(string first, string second) =>
        Assert.NotEqual(LocalObjectPath.SanitizeSegment(first), LocalObjectPath.SanitizeSegment(second));

    [Theory]
    [InlineData("A.txt", "a.txt")]
    [InlineData("file", "file.s3explorer.download")]
    [InlineData("folder", "folder/child.txt")]
    public void LocalReservationIncludesWindowsCaseTemporaryAndFileDirectoryAliases(string first, string second)
    {
        var root = Path.Combine(Path.GetTempPath(), "destination-tests");
        Assert.True(TransferDestinationReservation.Conflicts(Download(Path.Combine(root, first)), Download(Path.Combine(root, second))));
    }

    [Fact]
    public void ReservationProtectsActualCheckpointTemporaryPath()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "unique-reserved-" + Guid.NewGuid().ToString("N"));
        var owner = Download("target") with { DownloadCheckpoint = new(temporary, 0, 1) };
        var collision = Download(temporary);
        Assert.True(TransferDestinationReservation.Conflicts(owner, collision));
        Assert.Throws<InvalidOperationException>(() => TransferDestinationReservation.Reserve(collision, [owner]));
        var batch = Guid.NewGuid();
        var chunk = new[] { collision with { BatchId = batch } };
        Assert.Throws<InvalidOperationException>(() => TransferDestinationReservation.ReserveBatch(chunk, [owner], batch));
    }

    [Fact]
    public void AutoRenamePreservesEveryBatchObjectIncludingCompletedEarlierChunks()
    {
        var batch = Guid.NewGuid();
        var first = Download("a.txt") with { BatchId = batch, State = TransferTaskState.Completed };
        var second = Download("A.txt") with { BatchId = batch, ConflictPolicy = ObjectConflictPolicy.AutoRename };
        var reserved = TransferDestinationReservation.Reserve(second, [first]);
        Assert.Equal("A (2).txt", Path.GetFileName(reserved.LocalPath));
        Assert.Equal(second.ObjectKey, reserved.ObjectKey);
        Assert.Equal(second.Id, reserved.Id);
    }

    [Fact]
    public void ExplicitOverwriteDoesNotOverrideAnotherQueuedTransfer()
    {
        var first = Download("target.txt");
        var second = Download("target.txt") with { ConflictPolicy = ObjectConflictPolicy.Overwrite };
        Assert.Throws<InvalidOperationException>(() => TransferDestinationReservation.Reserve(second, [first]));
        Assert.Same(second, TransferDestinationReservation.Reserve(second, [first with { State = TransferTaskState.Completed }]));
    }

    [Fact]
    public void UploadReservationsShareNamespaceWithServerSideCopy()
    {
        var profile = Guid.NewGuid();
        var upload = Download("local") with { ProfileId = profile, Direction = TransferDirection.Upload, Bucket = "bucket", ObjectKey = "key" };
        var copy = Download("other") with { ProfileId = profile, Direction = TransferDirection.Copy, DestinationBucket = "bucket", DestinationObjectKey = "key" };
        Assert.True(TransferDestinationReservation.Conflicts(upload, copy));
        Assert.False(TransferDestinationReservation.Conflicts(upload, copy with { ProfileId = Guid.NewGuid() }));
        Assert.False(TransferDestinationReservation.Conflicts(upload, copy with { DestinationObjectKey = "KEY" }));
    }

    [Fact]
    public async Task ConcurrentEnqueuesCannotAcquireTheSameDownloadDestination()
    {
        var store = new Store();
        await using var queue = new PersistentTransferQueue(store, new BlockingExecutor(), maxConcurrency: 2);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var path = Path.Combine(Path.GetTempPath(), $"reservation-{Guid.NewGuid():N}");
        var attempts = new[] { Download(path), Download(path.ToUpperInvariant()) }
            .Select(async task =>
            {
                try { await queue.EnqueueAsync(task, TestContext.Current.CancellationToken); return true; }
                catch (InvalidOperationException) { return false; }
            });
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, success => success);
        Assert.Single(queue.Snapshot.Tasks);
    }

    [Fact]
    public async Task BatchCollisionIsAtomicAndDoesNotPartiallyEnqueue()
    {
        await using var queue = new PersistentTransferQueue(new Store(), new BlockingExecutor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var first = Download("same");
        var batch = await queue.CreateBatchAsync(new TransferBatchRecord
        {
            ProfileId = first.ProfileId, ProfileName = first.ProfileName, Bucket = first.Bucket,
            Name = "download", RootPath = "root", Direction = TransferDirection.Download
        }, TestContext.Current.CancellationToken);
        var second = first with { Id = Guid.NewGuid(), ObjectKey = "different" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.AddBatchTasksAsync(batch.Id, [first, second], TestContext.Current.CancellationToken));
        Assert.Empty(queue.Snapshot.Tasks);
        Assert.Empty(Assert.Single(queue.Snapshot.Batches).TaskIds);
    }

    private static TransferTaskRecord Download(string path) => new()
    {
        ProfileId = Guid.NewGuid(), ProfileName = "test", Bucket = "bucket", ObjectKey = Guid.NewGuid().ToString("N"),
        LocalPath = path, Direction = TransferDirection.Download, TotalBytes = 1
    };

    private sealed class Store : ITransferTaskStore
    {
        private TransferStoreSnapshot _snapshot = new();
        public Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
        public Task SaveAsync(TransferStoreSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingExecutor : ITransferTaskExecutor
    {
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public Task AbortMultipartAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
