using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferDestinationReservationTests
{
    [Theory]
    [InlineData(TransferTaskState.Queued, true)]
    [InlineData(TransferTaskState.Running, true)]
    [InlineData(TransferTaskState.Paused, true)]
    [InlineData(TransferTaskState.RetryPending, true)]
    [InlineData(TransferTaskState.Interrupted, true)]
    [InlineData(TransferTaskState.CleanupPending, true)]
    [InlineData(TransferTaskState.Completed, false)]
    [InlineData(TransferTaskState.Failed, false)]
    [InlineData(TransferTaskState.Cancelled, false)]
    public void TerminalFailuresDoNotReserveDestinations(TransferTaskState state, bool reserved) =>
        Assert.Equal(reserved, TransferDestinationReservation.IsReserved(Download("target") with { State = state }));

    [Theory]
    [InlineData(TransferTaskState.Failed)]
    [InlineData(TransferTaskState.Cancelled)]
    public void FailedOrCancelledBatchItemsReleaseTheirNames(TransferTaskState state)
    {
        var batch = Guid.NewGuid();
        var first = Download("target.txt") with { BatchId = batch, State = state };
        var second = Download("target.txt") with { BatchId = batch, ConflictPolicy = ObjectConflictPolicy.AutoRename };
        Assert.Same(second, TransferDestinationReservation.Reserve(second, [first]));
        var chunk = new[] { second };
        TransferDestinationReservation.ReserveBatch(chunk, [first], batch);
        Assert.Same(second, chunk[0]);
    }

    [Fact]
    public async Task FailedUploadReleasesTargetButRetryCannotStealNewUploadsReservation()
    {
        var failed = Upload("lp/index.html") with
        {
            State = TransferTaskState.Failed,
            Failure = new("unsupported provider", TransferFailureCategory.Validation),
            MultipartCheckpoint = new("retained-upload", 5 * 1024 * 1024, [])
        };
        var store = new Store(new() { Tasks = [failed] });
        await using var queue = new PersistentTransferQueue(store, new BlockingExecutor(), maxConcurrency: 2);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var replacement = failed with { Id = Guid.NewGuid(), MultipartCheckpoint = null };
        await queue.EnqueueAsync(replacement, TestContext.Current.CancellationToken);
        Assert.Equal(TransferTaskState.Running, queue.Snapshot.Tasks.Single(task => task.Id == replacement.Id).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RetryAsync(failed.Id, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ResumeAsync(failed.Id, TestContext.Current.CancellationToken));
        var retained = queue.Snapshot.Tasks.Single(task => task.Id == failed.Id);
        Assert.Equal(TransferTaskState.Failed, retained.State);
        Assert.Equal(failed.Failure, retained.Failure);
        Assert.Equal(failed.MultipartCheckpoint, retained.MultipartCheckpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedUploadCanReacquireUnoccupiedTarget(bool resume)
    {
        var failed = Upload("lp/index.html") with { State = TransferTaskState.Failed };
        await using var queue = new PersistentTransferQueue(new Store(new() { Tasks = [failed] }), new BlockingExecutor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        if (resume) await queue.ResumeAsync(failed.Id, TestContext.Current.CancellationToken);
        else await queue.RetryAsync(failed.Id, TestContext.Current.CancellationToken);
        Assert.Equal(TransferTaskState.Running, Assert.Single(queue.Snapshot.Tasks).State);
    }

    [Fact]
    public async Task RetryAllSkipsOccupiedTargetsAndReservesEachReleasedTargetOnlyOnce()
    {
        var first = Upload("same") with { State = TransferTaskState.Failed };
        var duplicate = first with { Id = Guid.NewGuid() };
        var occupied = first with { Id = Guid.NewGuid(), ObjectKey = "busy" };
        var owner = occupied with { Id = Guid.NewGuid(), State = TransferTaskState.Paused };
        var independent = first with { Id = Guid.NewGuid(), ObjectKey = "independent" };
        await using var queue = new PersistentTransferQueue(
            new Store(new() { Tasks = [first, duplicate, occupied, owner, independent] }), new BlockingExecutor(), maxConcurrency: 4);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.RetryAllFailedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TransferTaskState.Running, queue.Snapshot.Tasks.Single(task => task.Id == first.Id).State);
        Assert.Equal(TransferTaskState.Running, queue.Snapshot.Tasks.Single(task => task.Id == independent.Id).State);
        Assert.Equal(TransferTaskState.Failed, queue.Snapshot.Tasks.Single(task => task.Id == duplicate.Id).State);
        Assert.Equal(TransferTaskState.Failed, queue.Snapshot.Tasks.Single(task => task.Id == occupied.Id).State);
        Assert.Equal(TransferTaskState.Paused, queue.Snapshot.Tasks.Single(task => task.Id == owner.Id).State);
    }

    [Fact]
    public async Task BatchRetrySkipsTargetsClaimedSinceFailureAndCountsActualRetries()
    {
        var first = Upload("lp/index.html") with
        {
            State = TransferTaskState.Failed, BatchId = Guid.NewGuid(), Kind = TransferTaskKind.FolderBatchItem,
            Failure = new("transient", TransferFailureCategory.Network, Retryable: true)
        };
        var second = first with { Id = Guid.NewGuid(), ObjectKey = "other" };
        var replacement = first with { Id = Guid.NewGuid(), BatchId = null, State = TransferTaskState.Paused };
        var batch = new TransferBatchRecord
        {
            Id = first.BatchId.Value, ProfileId = first.ProfileId, ProfileName = first.ProfileName,
            Direction = TransferDirection.Upload, Bucket = first.Bucket, RootPath = "local", Name = "upload",
            TaskIds = [first.Id, second.Id], DiscoveryCompleted = true
        };
        await using var queue = new PersistentTransferQueue(new Store(new()
        {
            Tasks = [first, second, replacement], Batches = [batch]
        }), new BlockingExecutor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await queue.RetryBatchFailuresAsync(batch.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TransferTaskState.Failed, queue.Snapshot.Tasks.Single(task => task.Id == first.Id).State);
        Assert.Equal(TransferTaskState.Running, queue.Snapshot.Tasks.Single(task => task.Id == second.Id).State);
    }

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

    private static TransferTaskRecord Upload(string key) => Download("local") with
    {
        Direction = TransferDirection.Upload, ObjectKey = key
    };

    private sealed class Store(TransferStoreSnapshot? initial = null) : ITransferTaskStore
    {
        private TransferStoreSnapshot _snapshot = initial ?? new();
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
