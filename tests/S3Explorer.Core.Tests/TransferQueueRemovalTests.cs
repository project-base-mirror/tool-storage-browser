using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferQueueRemovalTests
{
    [Fact]
    public async Task RemoveCompletedRemovesWholeTerminalBatchesAndAllSkippedEmptyBatch()
    {
        var done = Batch();
        var cancelled = Batch() with { CancellationRequested = true };
        var skipped = Batch() with { SkippedCount = 8 };
        var doneTask = Item(done, TransferTaskState.Completed);
        var cancelledTask = Item(cancelled, TransferTaskState.Cancelled);
        var standalone = Item(null, TransferTaskState.Completed);
        var store = new MemoryStore(new TransferStoreSnapshot
        {
            Batches = [done with { TaskIds = [doneTask.Id] }, cancelled with { TaskIds = [cancelledTask.Id] }, skipped],
            Tasks = [doneTask, cancelledTask, standalone]
        });
        await using var queue = new PersistentTransferQueue(store, new Executor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);

        Assert.Empty(queue.Snapshot.Tasks);
        Assert.Empty(queue.Snapshot.Batches);
        Assert.Empty(store.Snapshot.Batches);
        store.Snapshot.Validate();
    }

    [Fact]
    public async Task RemoveCompletedKeepsFailedPausedAndDiscoveringBatchesIntact()
    {
        var failed = Batch();
        var paused = Batch();
        var discovering = Batch() with { DiscoveryCompleted = false };
        var tasks = new[] { Item(failed, TransferTaskState.Completed), Item(failed, TransferTaskState.Failed),
            Item(paused, TransferTaskState.Completed), Item(paused, TransferTaskState.Paused),
            Item(discovering, TransferTaskState.Completed) };
        var batches = new[] { failed, paused, discovering }.Select(batch => batch with
        {
            TaskIds = tasks.Where(task => task.BatchId == batch.Id).Select(task => task.Id).ToArray()
        }).ToArray();
        var store = new MemoryStore(new TransferStoreSnapshot { Batches = batches, Tasks = tasks });
        await using var queue = new PersistentTransferQueue(store, new Executor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(tasks, queue.Snapshot.Tasks);
        Assert.Equal(batches, queue.Snapshot.Batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveAllWaitsForExecutorAndDoesNotRestartCancelledWork(bool failsInsteadOfCancelling)
    {
        var executor = new DelayedCancellationExecutor(failsInsteadOfCancelling);
        var store = new MemoryStore(new TransferStoreSnapshot());
        await using var queue = new PersistentTransferQueue(store, executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var batch = await queue.CreateBatchAsync(Batch(), TestContext.Current.CancellationToken);
        var running = Item(batch, TransferTaskState.Queued);
        await queue.AddBatchTasksAsync(batch.Id, [running, Item(batch, TransferTaskState.Queued)], TestContext.Current.CancellationToken);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var clearing = queue.RemoveAllAsync(TestContext.Current.CancellationToken);
        try
        {
            await executor.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(clearing.IsCompleted);
            Assert.True(queue.Snapshot.Batches.Single().CancellationRequested);
            Assert.True(queue.Snapshot.Batches.Single().DiscoveryCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.AddBatchTasksAsync(batch.Id,
                [Item(batch, TransferTaskState.Queued)], TestContext.Current.CancellationToken));
            // Work submitted after clear starts belongs to the next queue and must survive.
            await queue.EnqueueAsync(Item(null, TransferTaskState.Queued), TestContext.Current.CancellationToken);
        }
        finally { executor.Release.TrySetResult(); }
        await clearing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

        var remaining = Assert.Single(queue.Snapshot.Tasks);
        Assert.Null(remaining.BatchId);
        Assert.Equal(TransferTaskState.Completed, remaining.State);
        Assert.Empty(queue.Snapshot.Batches);
        Assert.Equal(2, executor.Executions);
        store.Snapshot.Validate();
    }

    [Fact]
    public async Task RemoveAllRetainsFailedMultipartCleanupAndOnlyItsBatchChildReference()
    {
        var batch = Batch();
        var checkpoint = new MultipartUploadCheckpoint("upload-to-clean", 5 * 1024 * 1024, []);
        var pending = Item(batch, TransferTaskState.Paused) with { MultipartCheckpoint = checkpoint };
        var done = Item(batch, TransferTaskState.Completed);
        var failed = Item(null, TransferTaskState.Failed);
        var executor = new Executor { FailAbort = true };
        var store = new MemoryStore(new TransferStoreSnapshot
        {
            Batches = [batch with { TaskIds = [pending.Id, done.Id] }, Batch() with { SkippedCount = 8 }],
            Tasks = [pending, done, failed]
        });
        await using var queue = new PersistentTransferQueue(store, executor);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.RemoveAllAsync(TestContext.Current.CancellationToken);

        var retained = Assert.Single(queue.Snapshot.Tasks);
        Assert.Equal(pending.Id, retained.Id);
        Assert.Equal(TransferTaskState.CleanupPending, retained.State);
        Assert.Equal(checkpoint.UploadId, retained.MultipartCheckpoint?.UploadId);
        Assert.Equal(new[] { pending.Id }, Assert.Single(queue.Snapshot.Batches).TaskIds);
        store.Snapshot.Validate();
        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);
        Assert.Single(queue.Snapshot.Tasks);

        executor.FailAbort = false;
        await queue.RemoveAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(queue.Snapshot.Tasks);
        Assert.Empty(queue.Snapshot.Batches);
        Assert.Equal(2, executor.Aborts);
    }

    [Fact]
    public async Task CancelAllClosesEmptyDiscoveryBeforeCompletedRemoval()
    {
        var history = Batch() with { SkippedCount = 8 };
        await using var queue = new PersistentTransferQueue(
            new MemoryStore(new TransferStoreSnapshot { Batches = [history] }), new Executor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var batch = await queue.CreateBatchAsync(Batch(), TestContext.Current.CancellationToken);
        await queue.CancelAllAsync(TestContext.Current.CancellationToken);
        Assert.True(queue.Snapshot.Batches.Single(item => item.Id == batch.Id).CancellationRequested);
        Assert.Equal(history, queue.Snapshot.Batches.Single(item => item.Id == history.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.AddBatchTasksAsync(batch.Id,
            [Item(batch, TransferTaskState.Queued)], TestContext.Current.CancellationToken));
        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);
        Assert.Empty(queue.Snapshot.Batches);
    }

    private static TransferBatchRecord Batch() => new()
    {
        Id = Guid.NewGuid(), ProfileId = Guid.NewGuid(), ProfileName = "profile", Name = "batch",
        Bucket = "bucket", RootPath = "folder", Direction = TransferDirection.Upload, DiscoveryCompleted = true
    };

    private static TransferTaskRecord Item(TransferBatchRecord? batch, TransferTaskState state) => new()
    {
        Id = Guid.NewGuid(), BatchId = batch?.Id, ProfileId = batch?.ProfileId ?? Guid.NewGuid(), ProfileName = "profile",
        Kind = batch is null ? TransferTaskKind.File : TransferTaskKind.FolderBatchItem,
        State = state, Bucket = "bucket", ObjectKey = Guid.NewGuid().ToString(), LocalPath = "/tmp/file",
        Direction = TransferDirection.Upload, TotalBytes = 10, RetryBaseDelaySeconds = 0
    };

    private sealed class MemoryStore(TransferStoreSnapshot snapshot) : ITransferTaskStore
    {
        public TransferStoreSnapshot Snapshot { get; private set; } = snapshot;
        public Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task SaveAsync(TransferStoreSnapshot value, CancellationToken cancellationToken = default)
        {
            value.Validate();
            Snapshot = value;
            return Task.CompletedTask;
        }
    }

    private sealed class Executor : ITransferTaskExecutor
    {
        public bool FailAbort { get; set; }
        public int Aborts { get; private set; }
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AbortMultipartAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            Aborts++;
            if (FailAbort) throw new IOException("offline");
            return Task.CompletedTask;
        }
    }

    private sealed class DelayedCancellationExecutor(bool failInsteadOfCancel) : ITransferTaskExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions;
        public async Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Executions) > 1) return;
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                await Release.Task;
                // This context must remain available until the executor has returned.
                Assert.NotNull(context.Task);
                if (failInsteadOfCancel) throw new IOException("network error while cancelling");
                throw;
            }
        }
    }
}
