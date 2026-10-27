using System.Net;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferQueueRecoveryTests
{
    [Theory]
    [InlineData(TransferTaskState.Paused)]
    [InlineData(TransferTaskState.Interrupted)]
    [InlineData(TransferTaskState.Failed)]
    [InlineData(TransferTaskState.CleanupPending)]
    public async Task CancelIdleMultipartAbortsBeforeDiscardingCheckpoint(TransferTaskState state)
    {
        var task = NewTask(state) with { MultipartCheckpoint = Multipart() };
        var store = new Store(new TransferStoreSnapshot { Tasks = [task] });
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        executor.BeforeAbort = () =>
        {
            var durable = Assert.Single(store.Snapshot.Tasks);
            Assert.Equal(TransferTaskState.CleanupPending, durable.State);
            Assert.Equal("upload", durable.MultipartCheckpoint!.UploadId);
        };

        await queue.CancelAsync(task.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, executor.Aborts);
        Assert.Equal(TransferTaskState.Cancelled, Assert.Single(store.Snapshot.Tasks).State);
        Assert.Null(Assert.Single(store.Snapshot.Tasks).MultipartCheckpoint);
        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);
        Assert.Empty(store.Snapshot.Tasks);
    }

    [Theory]
    [InlineData(false, TransferTaskState.Paused)]
    [InlineData(true, TransferTaskState.Paused)]
    [InlineData(false, TransferTaskState.Failed)]
    [InlineData(true, TransferTaskState.Failed)]
    public async Task BulkCancellationCleansIdleMultipart(bool batchCancellation, TransferTaskState state)
    {
        var task = NewTask(state) with { MultipartCheckpoint = Multipart() };
        var batch = new TransferBatchRecord
        {
            ProfileId = task.ProfileId, ProfileName = task.ProfileName,
            Bucket = task.Bucket, Name = "batch", RootPath = "root", Direction = task.Direction,
            TaskIds = [task.Id]
        };
        if (batchCancellation)
            task = task with { BatchId = batch.Id, Kind = TransferTaskKind.FolderBatchItem };
        var store = new Store(new TransferStoreSnapshot
        {
            Tasks = [task], Batches = batchCancellation ? [batch] : []
        });
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        if (batchCancellation)
            await queue.CancelBatchAsync(batch.Id, TestContext.Current.CancellationToken);
        else
            await queue.CancelAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, executor.Aborts);
        Assert.Equal(TransferTaskState.Cancelled, Assert.Single(store.Snapshot.Tasks).State);
        Assert.Null(Assert.Single(store.Snapshot.Tasks).MultipartCheckpoint);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("all")]
    [InlineData("batch")]
    public async Task CleanupIsReservedBeforePublishingAndAcrossWholeBatch(string mode)
    {
        var first = NewTask(TransferTaskState.Paused) with { MultipartCheckpoint = Multipart() };
        var second = first with { Id = Guid.NewGuid() };
        var batch = new TransferBatchRecord
        {
            ProfileId = first.ProfileId, ProfileName = first.ProfileName,
            Bucket = first.Bucket, Name = "batch", RootPath = "root", Direction = first.Direction,
            TaskIds = [first.Id, second.Id]
        };
        if (mode == "batch")
        {
            first = first with { BatchId = batch.Id, Kind = TransferTaskKind.FolderBatchItem };
            second = second with { BatchId = batch.Id, Kind = TransferTaskKind.FolderBatchItem };
        }
        var store = new Store(new TransferStoreSnapshot
        {
            Tasks = [first, second], Batches = mode == "batch" ? [batch] : []
        });
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var observedPending = 0;
        var waits = new List<Task>();
        queue.Changed += (_, args) =>
        {
            if (!args.Snapshot.Tasks.Any(task => task.State == TransferTaskState.CleanupPending)) return;
            observedPending++;
            var wait = queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
            waits.Add(wait);
            Assert.False(wait.IsCompleted); // including publication before the first Abort call
        };
        if (mode == "single") await queue.CancelAsync(first.Id, TestContext.Current.CancellationToken);
        else if (mode == "all") await queue.CancelAllAsync(TestContext.Current.CancellationToken);
        else await queue.CancelBatchAsync(batch.Id, TestContext.Current.CancellationToken);

        Assert.True(observedPending >= (mode == "single" ? 1 : 2));
        Assert.Equal(mode == "single" ? 1 : 2, executor.Aborts);
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailedAbortKeepsUploadIdAndCannotBeRemoved()
    {
        var task = NewTask(TransferTaskState.Interrupted) with { MultipartCheckpoint = Multipart() };
        var store = new Store(new TransferStoreSnapshot { Tasks = [task] });
        var executor = new Executor { AbortFails = true };
        await using var queue = new PersistentTransferQueue(store, executor);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.CancelAsync(task.Id, TestContext.Current.CancellationToken);
        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);

        var pending = Assert.Single(store.Snapshot.Tasks);
        Assert.Equal(TransferTaskState.CleanupPending, pending.State);
        Assert.Equal("upload", pending.MultipartCheckpoint!.UploadId);
        Assert.True(pending.MultipartCheckpoint.CleanupPending);
        executor.AbortFails = false;
        await queue.CancelAsync(task.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, executor.Aborts);
        Assert.Null(Assert.Single(store.Snapshot.Tasks).MultipartCheckpoint);
    }

    [Fact]
    public async Task LegacyCancelledMultipartIsRecoveredAsPendingCleanup()
    {
        var task = NewTask(TransferTaskState.Cancelled) with { MultipartCheckpoint = Multipart() };
        var store = new Store(new TransferStoreSnapshot { Tasks = [task] });
        await using var queue = new PersistentTransferQueue(store, new Executor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferTaskState.CleanupPending, Assert.Single(store.Snapshot.Tasks).State);
        Assert.Equal("upload", Assert.Single(store.Snapshot.Tasks).MultipartCheckpoint!.UploadId);
    }

    [Fact]
    public async Task ProgressCheckpointsAreCoalescedButPauseFlushesTheLatest()
    {
        var clock = DateTimeOffset.UtcNow;
        var store = new Store();
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor, clock: () => clock);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var task = NewTask();
        await queue.EnqueueAsync(task, TestContext.Current.CancellationToken);
        var context = await executor.Context.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Checkpoint(context, 0);
        var saves = store.Saves;

        for (var index = 1; index <= 100; index++)
            await Checkpoint(context, index * 4096L);

        Assert.Equal(saves, store.Saves);
        Assert.Equal(0, Assert.Single(store.Snapshot.Tasks).DownloadCheckpoint!.CompletedBytes);
        Assert.Equal(409600, Assert.Single(queue.Snapshot.Tasks).DownloadCheckpoint!.CompletedBytes);
        await queue.PauseAsync(task.Id, TestContext.Current.CancellationToken);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(409600, Assert.Single(store.Snapshot.Tasks).DownloadCheckpoint!.CompletedBytes);
        Assert.Equal(TransferTaskState.Paused, Assert.Single(store.Snapshot.Tasks).State);
    }

    [Fact]
    public async Task ProgressFlushesAtByteAndTimeBoundsAndNewIdentity()
    {
        var clock = DateTimeOffset.UtcNow;
        var store = new Store();
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor, clock: () => clock);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(NewTask(), TestContext.Current.CancellationToken);
        var context = await executor.Context.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Checkpoint(context, 0);
        var saves = store.Saves;
        await Checkpoint(context, 64L * 1024 * 1024);
        Assert.Equal(saves + 1, store.Saves);
        await Checkpoint(context, 65L * 1024 * 1024);
        Assert.Equal(saves + 1, store.Saves);
        clock = clock.AddSeconds(1);
        await Checkpoint(context, 66L * 1024 * 1024);
        Assert.Equal(saves + 2, store.Saves);
        await Checkpoint(context, 0);
        Assert.Equal(saves + 3, store.Saves);
        Assert.Equal(0, Assert.Single(store.Snapshot.Tasks).DownloadCheckpoint!.CompletedBytes);
        await Checkpoint(context, 0, "new-etag");
        Assert.Equal(saves + 4, store.Saves);
        Assert.Equal("new-etag", Assert.Single(store.Snapshot.Tasks).DownloadCheckpoint!.ETag);
    }

    [Fact]
    public async Task FailedCheckpointSaveDoesNotAdvancePublishedCheckpointOrSaveWindow()
    {
        var clock = DateTimeOffset.UtcNow;
        var store = new Store();
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor, clock: () => clock);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(NewTask(), TestContext.Current.CancellationToken);
        var context = await executor.Context.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Checkpoint(context, 0);
        var saves = store.Saves;
        store.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => Checkpoint(context, 64L * 1024 * 1024));
        Assert.Equal(0, Assert.Single(queue.Snapshot.Tasks).DownloadCheckpoint!.CompletedBytes);
        Assert.Equal(0, Assert.Single(store.Snapshot.Tasks).DownloadCheckpoint!.CompletedBytes);
        await Checkpoint(context, 64L * 1024 * 1024);
        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(64L * 1024 * 1024, Assert.Single(store.Snapshot.Tasks).DownloadCheckpoint!.CompletedBytes);
    }

    [Fact]
    public async Task InitialMultipartIdIsPersistedBeforeExecutorContinues()
    {
        var store = new Store();
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(NewTask(), TestContext.Current.CancellationToken);
        var context = await executor.Context.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await context.UpdateCheckpointAsync(0, null, Multipart(), TestContext.Current.CancellationToken);

        Assert.Equal("upload", Assert.Single(store.Snapshot.Tasks).MultipartCheckpoint!.UploadId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentTerminalSaveFailureSuspendsQueueWithoutHanging(bool pause)
    {
        var store = new Store();
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var first = NewTask();
        var second = NewTask() with { LocalPath = "second-local.bin" };
        await queue.EnqueueAsync(first, TestContext.Current.CancellationToken);
        var context = await executor.Context.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await context.UpdateCheckpointAsync(0, null, Multipart(), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(second, TestContext.Current.CancellationToken);
        store.FailAlways = true;
        try
        {
            if (pause)
                await queue.PauseAsync(first.Id, TestContext.Current.CancellationToken);
            else
                executor.Finish.TrySetResult();
            await queue.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(queue.HasPersistenceFailure);
            Assert.Equal(1, executor.Executions);
            Assert.Equal(0, queue.RunningCount);
            Assert.All(queue.Snapshot.Tasks, task =>
            {
                Assert.Equal(TransferTaskState.Interrupted, task.State);
                Assert.Equal(TransferFailureCategory.LocalIo, task.Failure!.Category);
                Assert.Contains("仅在内存中", task.Failure.Message);
            });
            Assert.Equal("upload", queue.Snapshot.Tasks.Single(task => task.Id == first.Id).MultipartCheckpoint!.UploadId);
            Assert.Equal(TransferTaskState.Running, store.Snapshot.Tasks.Single(task => task.Id == first.Id).State);
            await queue.SetConcurrencyAsync(2, TestContext.Current.CancellationToken);
            Assert.Equal(1, executor.Executions);
        }
        finally { store.FailAlways = false; }

        // Explicit Resume first saves the suspended snapshot, then permits scheduling again.
        executor.Finish.TrySetResult();
        await queue.ResumeAsync(second.Id, TestContext.Current.CancellationToken);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(queue.HasPersistenceFailure);
        Assert.Equal(2, executor.Executions);
        Assert.Equal(TransferTaskState.Completed, store.Snapshot.Tasks.Single(task => task.Id == second.Id).State);
    }

    [Fact]
    public async Task PersistentStartSaveFailureDoesNotLaunchExecutorOrLeaveActiveQueue()
    {
        var store = new Store(new TransferStoreSnapshot { Tasks = [NewTask()] }) { FailAlways = true };
        var executor = new Executor();
        await using var queue = new PersistentTransferQueue(store, executor);
        try
        {
            await queue.InitializeAsync(TestContext.Current.CancellationToken);
            await queue.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(queue.HasPersistenceFailure);
            Assert.Equal(0, executor.Executions);
            Assert.Equal(TransferTaskState.Interrupted, Assert.Single(queue.Snapshot.Tasks).State);
            Assert.Equal(TransferTaskState.Queued, Assert.Single(store.Snapshot.Tasks).State);
        }
        finally { store.FailAlways = false; }
    }

    [Fact]
    public async Task PersistedRetryEpisodeExpiresBeforeStartingAndManualRetryResetsBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var task = NewTask(TransferTaskState.RetryPending) with
        {
            RetryStartedAt = now.AddMinutes(-16), NextAttemptAt = now.AddMinutes(-1), AttemptCount = 1,
            Failure = new TransferFailureInfo("network", TransferFailureCategory.Network, Retryable: true)
        };
        var store = new Store(new TransferStoreSnapshot { Tasks = [task] });
        var executor = new Executor();
        executor.Finish.TrySetResult();
        await using var queue = new PersistentTransferQueue(store, executor, clock: () => now);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(TransferTaskState.Failed, Assert.Single(store.Snapshot.Tasks).State);
        Assert.Contains("15 分钟", Assert.Single(store.Snapshot.Tasks).Failure!.Message);

        await queue.RetryAsync(task.Id, TestContext.Current.CancellationToken);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, executor.Executions);
        Assert.Null(Assert.Single(store.Snapshot.Tasks).RetryStartedAt);
        Assert.Equal(TransferTaskState.Completed, Assert.Single(store.Snapshot.Tasks).State);
    }

    [Fact]
    public async Task NextRetryDelayCannotExceedElapsedBudget()
    {
        var store = new Store();
        var executor = new Executor { ExecuteFailure = new HttpRequestException("network") };
        executor.Finish.TrySetResult();
        await using var queue = new PersistentTransferQueue(store, executor);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(NewTask() with { RetryBaseDelaySeconds = 3600 }, TestContext.Current.CancellationToken);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, executor.Executions);
        Assert.Equal(TransferTaskState.Failed, Assert.Single(store.Snapshot.Tasks).State);
        Assert.Contains("15 分钟", Assert.Single(store.Snapshot.Tasks).Failure!.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, TransferFailureCategory.Validation, false)]
    [InlineData(HttpStatusCode.Unauthorized, TransferFailureCategory.Authentication, false)]
    [InlineData(HttpStatusCode.Forbidden, TransferFailureCategory.Authorization, false)]
    [InlineData(HttpStatusCode.NotFound, TransferFailureCategory.NotFound, false)]
    [InlineData(HttpStatusCode.PreconditionFailed, TransferFailureCategory.Conflict, false)]
    [InlineData(HttpStatusCode.TooManyRequests, TransferFailureCategory.Service, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TransferFailureCategory.Service, true)]
    public void HttpFailureClassificationDoesNotRetryPermanentErrors(
        HttpStatusCode status, TransferFailureCategory category, bool retryable)
    {
        var result = TransferFailureClassifier.Classify(new HttpRequestException("HTTP failure", null, status));
        Assert.Equal(category, result.Category);
        Assert.Equal(retryable, result.Retryable);
    }

    [Fact]
    public void RetryJitterIsBoundedAndDoesNotExceedBackoffCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), RetryBackoff.Calculate(2, 3, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(8), RetryBackoff.Calculate(2, 3, 1));
        Assert.Equal(TimeSpan.FromMinutes(30), RetryBackoff.Calculate(3600, 20, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryBackoff.Calculate(2, 3, double.NaN));
    }

    private static Task Checkpoint(ITransferTaskExecutionContext context, long bytes, string etag = "etag") =>
        context.UpdateCheckpointAsync(bytes,
            new DownloadCheckpoint("file.part", bytes, context.Task.TotalBytes, etag), null,
            TestContext.Current.CancellationToken);

    private static TransferTaskRecord NewTask(TransferTaskState state = TransferTaskState.Queued) => new()
    {
        ProfileId = Guid.NewGuid(), ProfileName = "test", Bucket = "test", ObjectKey = "object",
        LocalPath = "local.bin", TotalBytes = 1024L * 1024 * 1024, State = state,
        Direction = TransferDirection.Download
    };

    private static MultipartUploadCheckpoint Multipart() => new("upload", 16L * 1024 * 1024, []);

    private sealed class Store(TransferStoreSnapshot? initial = null) : ITransferTaskStore
    {
        public TransferStoreSnapshot Snapshot { get; private set; } = initial ?? new();
        public int Saves { get; private set; }
        public bool FailNext { get; set; }
        public bool FailAlways { get; set; }
        public Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task SaveAsync(TransferStoreSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (FailNext || FailAlways)
            {
                FailNext = false;
                throw new IOException("injected disk failure");
            }
            Snapshot = snapshot;
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class Executor : ITransferTaskExecutor
    {
        public TaskCompletionSource<ITransferTaskExecutionContext> Context { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions { get; private set; }
        public Exception? ExecuteFailure { get; init; }
        public int Aborts { get; private set; }
        public bool AbortFails { get; set; }
        public Action? BeforeAbort { get; set; }
        public async Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            Executions++;
            Context.TrySetResult(context);
            await Finish.Task.WaitAsync(cancellationToken);
            if (ExecuteFailure is not null) throw ExecuteFailure;
        }
        public Task AbortMultipartAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            BeforeAbort?.Invoke();
            Aborts++;
            if (AbortFails) throw new IOException("injected abort failure");
            return Task.CompletedTask;
        }
    }
}
