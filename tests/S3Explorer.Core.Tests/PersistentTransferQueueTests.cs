using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class PersistentTransferQueueTests
{
    [Fact]
    public async Task JsonStoreRoundTripsCredentialFreeSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "transfers.json");
        try
        {
            var store = new JsonTransferTaskStore(path);
            var task = CreateTask() with { VersionId = "historical-version-id" };
            await store.SaveAsync(new TransferStoreSnapshot { Tasks = [task] }, TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(loaded.Tasks);
            Assert.Equal(task.Id, loaded.Tasks[0].Id);
            Assert.Equal("historical-version-id", loaded.Tasks[0].VersionId);
            Assert.DoesNotContain("secretKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sessionToken", json, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptStoreIsPreservedAndRecoveredAsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "transfers.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, "{not-json", TestContext.Current.CancellationToken);
        try
        {
            var loaded = await new JsonTransferTaskStore(path).LoadAsync(TestContext.Current.CancellationToken);

            Assert.Empty(loaded.Tasks);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.EnumerateFiles(root, "transfers.json.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializationRecoversRunningTaskAndLeavesItInterrupted()
    {
        var running = CreateTask(TransferTaskState.Running) with { AttemptCount = 1 };
        var store = new MemoryStore(new TransferStoreSnapshot { Tasks = [running] });
        await using var queue = new PersistentTransferQueue(store, new BlockingExecutor());

        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferTaskState.Interrupted, queue.Snapshot.Tasks.Single().State);
        Assert.Equal(TransferTaskState.Interrupted, store.Snapshot.Tasks.Single().State);
    }

    [Fact]
    public async Task OneFailureDoesNotBlockFollowingTask()
    {
        var executor = new SequencedExecutor();
        var store = new MemoryStore();
        await using var queue = new PersistentTransferQueue(store, executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        var failed = CreateTask();
        var completed = CreateTask();
        executor.FailTaskId = failed.Id;
        await queue.EnqueueAsync(failed, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(completed, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        Assert.Equal(TransferTaskState.Failed, queue.Snapshot.Tasks.Single(task => task.Id == failed.Id).State);
        Assert.Equal(TransferTaskState.Completed, queue.Snapshot.Tasks.Single(task => task.Id == completed.Id).State);
    }

    [Fact]
    public async Task PauseResumeCancelAndRetryAreDurable()
    {
        var executor = new BlockingExecutor();
        var store = new MemoryStore();
        await using var queue = new PersistentTransferQueue(store, executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        var task = CreateTask();
        await queue.EnqueueAsync(task, TestContext.Current.CancellationToken);
        await executor.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.PauseAsync(task.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.Snapshot.Tasks.Single().State == TransferTaskState.Paused);

        executor.Release();
        await queue.ResumeAsync(task.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.Snapshot.Tasks.Single().State == TransferTaskState.Completed);

        executor.Reset();
        var cancelled = CreateTask();
        await queue.EnqueueAsync(cancelled, TestContext.Current.CancellationToken);
        await executor.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.CancelAsync(cancelled.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.Snapshot.Tasks.Single(item => item.Id == cancelled.Id).State == TransferTaskState.Cancelled);
        Assert.Equal(TransferTaskState.Cancelled, store.Snapshot.Tasks.Single(item => item.Id == cancelled.Id).State);
    }

    [Fact]
    public async Task MultipartCancelAbortsAndClearsCheckpoint()
    {
        var executor = new MultipartBlockingExecutor();
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var task = CreateMultipartTask();

        await queue.EnqueueAsync(task, TestContext.Current.CancellationToken);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.CancelAsync(task.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.Snapshot.Tasks.Single().State == TransferTaskState.Cancelled);

        var result = queue.Snapshot.Tasks.Single();
        Assert.Equal(1, executor.AbortCount);
        Assert.Null(result.MultipartCheckpoint);
    }

    [Fact]
    public async Task MultipartAbortFailureBecomesCleanupPendingAndRetainsUploadId()
    {
        var executor = new MultipartBlockingExecutor { FailAbort = true };
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var task = CreateMultipartTask();

        await queue.EnqueueAsync(task, TestContext.Current.CancellationToken);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.CancelAsync(task.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.Snapshot.Tasks.Single().State == TransferTaskState.CleanupPending);

        var result = queue.Snapshot.Tasks.Single();
        Assert.Equal(1, executor.AbortCount);
        Assert.Equal("upload-id", result.MultipartCheckpoint!.UploadId);
        Assert.True(result.MultipartCheckpoint.CleanupPending);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task MultipartPausePreservesCheckpointWithoutAbort()
    {
        var executor = new MultipartBlockingExecutor();
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var task = CreateMultipartTask();

        await queue.EnqueueAsync(task, TestContext.Current.CancellationToken);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.PauseAsync(task.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.Snapshot.Tasks.Single().State == TransferTaskState.Paused);

        Assert.Equal(0, executor.AbortCount);
        Assert.Equal("upload-id", queue.Snapshot.Tasks.Single().MultipartCheckpoint!.UploadId);
    }

    [Fact]
    public async Task RetryAllFailedRequeuesEveryFailure()
    {
        var executor = new SequencedExecutor { FailAll = true };
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 2);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(CreateTask(), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(CreateTask(), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        executor.FailAll = false;
        await queue.RetryAllFailedAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        Assert.All(queue.Snapshot.Tasks, task => Assert.Equal(TransferTaskState.Completed, task.State));
    }

    [Fact]
    public async Task RetryRejectsNonFailedTaskWithoutChangingPersistedState()
    {
        var paused = CreateTask(TransferTaskState.Paused);
        var store = new MemoryStore(new TransferStoreSnapshot { Tasks = [paused] });
        await using var queue = new PersistentTransferQueue(store, new BlockingExecutor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.RetryAsync(paused.Id, TestContext.Current.CancellationToken));

        Assert.Equal(TransferTaskState.Paused, Assert.Single(store.Snapshot.Tasks).State);
    }

    [Fact]
    public async Task CancelAllPreservesFailureHistoryAndMultipartCleanupWork()
    {
        var paused = CreateTask(TransferTaskState.Paused);
        var interrupted = CreateTask(TransferTaskState.Interrupted);
        var failed = CreateTask(TransferTaskState.Failed) with
        {
            Failure = new TransferFailureInfo("failed", Retryable: true)
        };
        var cleanup = CreateMultipartTask() with
        {
            State = TransferTaskState.CleanupPending,
            Failure = new TransferFailureInfo("abort failed", Retryable: true),
            MultipartCheckpoint = CreateMultipartTask().MultipartCheckpoint! with { CleanupPending = true }
        };
        var completed = CreateTask(TransferTaskState.Completed) with { TransferredBytes = 10 };
        var store = new MemoryStore(new TransferStoreSnapshot
        {
            Tasks = [paused, interrupted, failed, cleanup, completed]
        });
        await using var queue = new PersistentTransferQueue(store, new BlockingExecutor());
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.CancelAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferTaskState.Cancelled, TaskState(paused.Id));
        Assert.Equal(TransferTaskState.Cancelled, TaskState(interrupted.Id));
        Assert.Equal(TransferTaskState.Failed, TaskState(failed.Id));
        Assert.Equal(TransferTaskState.CleanupPending, TaskState(cleanup.Id));
        Assert.True(queue.Snapshot.Tasks.Single(task => task.Id == cleanup.Id).MultipartCheckpoint!.CleanupPending);
        Assert.Equal(TransferTaskState.Completed, TaskState(completed.Id));

        TransferTaskState TaskState(Guid id) =>
            queue.Snapshot.Tasks.Single(task => task.Id == id).State;
    }

    [Fact]
    public async Task RecoveryOnlyInterruptsPreviouslyRunningTasks()
    {
        var running = CreateTask(TransferTaskState.Running) with { AttemptCount = 2 };
        var retry = CreateTask(TransferTaskState.RetryPending) with
        {
            AttemptCount = 1,
            NextAttemptAt = DateTimeOffset.UtcNow.AddDays(1),
            Failure = new TransferFailureInfo("retry", Retryable: true)
        };
        var failed = CreateTask(TransferTaskState.Failed) with
        {
            Failure = new TransferFailureInfo("failed", Retryable: false)
        };
        var store = new MemoryStore(new TransferStoreSnapshot { Tasks = [running, retry, failed] });
        await using var queue = new PersistentTransferQueue(store, new BlockingExecutor());

        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TransferTaskState.Interrupted, queue.Snapshot.Tasks.Single(task => task.Id == running.Id).State);
        Assert.Equal(2, queue.Snapshot.Tasks.Single(task => task.Id == running.Id).AttemptCount);
        Assert.Equal(TransferTaskState.RetryPending, queue.Snapshot.Tasks.Single(task => task.Id == retry.Id).State);
        Assert.Equal(TransferTaskState.Failed, queue.Snapshot.Tasks.Single(task => task.Id == failed.Id).State);
    }

    [Fact]
    public async Task ExecutorCanPersistDestinationSnapshotBeforeUploadCompletes()
    {
        var store = new MemoryStore();
        var executor = new DestinationSnapshotExecutor();
        await using var queue = new PersistentTransferQueue(store, executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(CreateTask(), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        var task = Assert.Single(store.Snapshot.Tasks);
        Assert.Equal(TransferTaskState.Completed, task.State);
        Assert.True(task.DestinationExistedBeforeTransfer);
        Assert.True(executor.ObservedPersistedSnapshot);
    }

    private static TransferTaskRecord CreateTask(TransferTaskState state = TransferTaskState.Queued) => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = Guid.NewGuid(),
        ProfileName = "profile",
        Direction = TransferDirection.Upload,
        State = state,
        Bucket = "bucket",
        ObjectKey = $"file-{Guid.NewGuid():N}.bin",
        LocalPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin"),
        TotalBytes = 10,
        MaxAttempts = 3
    };

    private static TransferTaskRecord CreateMultipartTask()
    {
        var modified = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        return CreateTask() with
        {
            TotalBytes = 10L * 1024 * 1024,
            MultipartCheckpoint = new MultipartUploadCheckpoint(
                "upload-id",
                5L * 1024 * 1024,
                [],
                false,
                "bucket",
                "object.bin",
                10L * 1024 * 1024,
                modified,
                modified),
            ObjectKey = "object.bin"
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("Queue condition was not reached.");
            await Task.Delay(20);
        }
    }

    private sealed class MemoryStore(TransferStoreSnapshot? snapshot = null) : ITransferTaskStore
    {
        public TransferStoreSnapshot Snapshot { get; private set; } = snapshot ?? new TransferStoreSnapshot();
        public Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
        public Task SaveAsync(TransferStoreSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class SequencedExecutor : ITransferTaskExecutor
    {
        public Guid? FailTaskId { get; set; }
        public bool FailAll { get; set; }

        public Task ExecuteAsync(
            ITransferTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            var task = context.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (FailAll || task.Id == FailTaskId)
                throw new IOException("simulated failure");
            context.ReportProgress(new TransferProgress(task.TotalBytes, task.TotalBytes));
            return Task.CompletedTask;
        }
    }

    private sealed class DestinationSnapshotExecutor : ITransferTaskExecutor
    {
        public bool ObservedPersistedSnapshot { get; private set; }

        public async Task ExecuteAsync(
            ITransferTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            await context.UpdateDestinationSnapshotAsync(true, cancellationToken);
            ObservedPersistedSnapshot = context.Task.DestinationExistedBeforeTransfer == true;
            context.ReportProgress(new TransferProgress(context.Task.TotalBytes, context.Task.TotalBytes));
        }
    }

    private sealed class MultipartBlockingExecutor : ITransferTaskExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailAbort { get; init; }
        public int AbortCount { get; private set; }

        public async Task ExecuteAsync(
            ITransferTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task AbortMultipartAsync(
            ITransferTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            AbortCount++;
            if (FailAbort)
                throw new IOException("abort failed");
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingExecutor : ITransferTaskExecutor
    {
        private TaskCompletionSource _release = NewSource();
        private TaskCompletionSource _started = NewSource();

        public Task Started => _started.Task;

        public async Task ExecuteAsync(
            ITransferTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            var task = context.Task;
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            context.ReportProgress(new TransferProgress(task.TotalBytes, task.TotalBytes));
        }

        public void Release() => _release.TrySetResult();

        public void Reset()
        {
            _release = NewSource();
            _started = NewSource();
        }

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
