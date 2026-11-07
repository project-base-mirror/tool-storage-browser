using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CrossConnectionTransferTests
{
    [Fact]
    public void DifferentConnectionsMayUseIdenticalBucketAndKey()
    {
        var task = Copy();
        task.Validate();
        Assert.True(task.IsCrossConnectionCopy);
        Assert.Equal(task.DestinationProfileId, task.EffectiveDestinationProfileId);
        Assert.Throws<InvalidOperationException>(() => (task with { DestinationProfileId = null }).Validate());
        Assert.Throws<InvalidOperationException>(() => (task with { DestinationProfileId = task.ProfileId }).Validate());
        Assert.Throws<ArgumentException>(() => (task with { DestinationProfileId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => (task with { Direction = TransferDirection.Move }).Validate());
    }

    [Fact]
    public void ReservationUsesTargetConnectionForUploadsAndOtherCopies()
    {
        var copy = Copy();
        var upload = new TransferTaskRecord
        {
            ProfileId = copy.DestinationProfileId!.Value, ProfileName = "destination",
            Direction = TransferDirection.Upload, Bucket = copy.DestinationBucket,
            ObjectKey = copy.DestinationObjectKey, LocalPath = "unused"
        };
        Assert.True(TransferDestinationReservation.Conflicts(copy, upload));
        Assert.True(TransferDestinationReservation.Conflicts(upload, copy));
        Assert.Throws<InvalidOperationException>(() => TransferDestinationReservation.Reserve(copy, [upload]));
        Assert.False(TransferDestinationReservation.CanResume(copy, [upload]));
        Assert.False(TransferDestinationReservation.Conflicts(copy, upload with { ProfileId = copy.ProfileId }));
        var secondSource = copy with { Id = Guid.NewGuid(), ProfileId = Guid.NewGuid() };
        Assert.True(TransferDestinationReservation.Conflicts(copy, secondSource));
        Assert.False(TransferDestinationReservation.Conflicts(copy,
            secondSource with { DestinationProfileId = Guid.NewGuid() }));

        var batch = Guid.NewGuid();
        var chunk = new[] { copy with { BatchId = batch, ConflictPolicy = ObjectConflictPolicy.AutoRename } };
        TransferDestinationReservation.ReserveBatch(chunk, [upload], batch);
        Assert.Equal("file (2).txt", chunk[0].DestinationObjectKey);
        Assert.Equal(copy.DestinationProfileId, chunk[0].DestinationProfileId);
        Assert.Equal("file.txt", chunk[0].ObjectKey);
    }

    [Fact]
    public async Task StoreRoundTripsBothConnectionsAndCheckpointIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "s3-cross-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var task = Copy();
            task = task with
            {
                MultipartCheckpoint = new("upload-id", 5 * 1024 * 1024, [])
                {
                    CopySourceProfileId = task.ProfileId,
                    CopyDestinationProfileId = task.DestinationProfileId,
                    CopySourceBucket = task.Bucket, CopySourceKey = task.ObjectKey,
                    CopySourceETag = "source-etag", CopySourceVersionId = "source-version"
                }
            };
            var store = new JsonTransferTaskStore(Path.Combine(root, "transfers.json"));
            await store.SaveAsync(new() { Tasks = [task] }, TestContext.Current.CancellationToken);
            var snapshot = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, snapshot.SchemaVersion);
            var restored = Assert.Single(snapshot.Tasks);
            Assert.Equal(task.ProfileId, restored.ProfileId);
            Assert.Equal(task.DestinationProfileId, restored.DestinationProfileId);
            Assert.Equal("destination", restored.DestinationProfileName);
            Assert.Equal(task.ProfileId, restored.MultipartCheckpoint!.CopySourceProfileId);
            Assert.Equal(task.DestinationProfileId, restored.MultipartCheckpoint.CopyDestinationProfileId);
            Assert.True(restored.IsCrossConnectionCopy);
            await store.SaveAsync(snapshot with { Tasks = [] }, TestContext.Current.CancellationToken);
            Assert.Equal(1, (await store.LoadAsync(TestContext.Current.CancellationToken)).SchemaVersion);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacySameConnectionRecordsDoNotRequireDestinationProfile()
    {
        var task = Copy() with { DestinationProfileId = null, DestinationProfileName = null, DestinationObjectKey = "other" };
        task.Validate();
        Assert.False(task.IsCrossConnectionCopy);
        Assert.Equal(task.ProfileId, task.EffectiveDestinationProfileId);
    }

    [Fact]
    public async Task ManualMultipartCleanupMatchesDestinationConnectionAndCheckpointPath()
    {
        var task = Copy() with
        {
            State = TransferTaskState.CleanupPending,
            DestinationBucket = "target", DestinationObjectKey = "copy.txt",
            MultipartCheckpoint = new("upload-id", 5 * 1024 * 1024, [], CleanupPending: true,
                Bucket: "target", ObjectKey: "copy.txt")
        };
        var store = new SnapshotStore(new() { Tasks = [task] });
        await using var queue = new PersistentTransferQueue(store, new NoopExecutor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.MarkMultipartCleanedAsync(task.ProfileId, "target", "copy.txt", "upload-id", TestContext.Current.CancellationToken);
        Assert.NotNull(Assert.Single(queue.Snapshot.Tasks).MultipartCheckpoint);
        await queue.MarkMultipartCleanedAsync(task.DestinationProfileId!.Value, "target", "copy.txt", "upload-id", TestContext.Current.CancellationToken);
        var cleaned = Assert.Single(queue.Snapshot.Tasks);
        Assert.Null(cleaned.MultipartCheckpoint);
        Assert.Equal(TransferTaskState.Cancelled, cleaned.State);
    }

    private sealed class SnapshotStore(TransferStoreSnapshot snapshot) : ITransferTaskStore
    {
        public Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task SaveAsync(TransferStoreSnapshot value, CancellationToken cancellationToken = default)
        { snapshot = value; return Task.CompletedTask; }
    }

    private sealed class NoopExecutor : ITransferTaskExecutor
    {
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AbortMultipartAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static TransferTaskRecord Copy() => new()
    {
        ProfileId = Guid.NewGuid(), ProfileName = "source", Direction = TransferDirection.Copy,
        Bucket = "same-bucket", ObjectKey = "file.txt", DestinationProfileId = Guid.NewGuid(),
        DestinationProfileName = "destination", DestinationBucket = "same-bucket", DestinationObjectKey = "file.txt"
    };
}
