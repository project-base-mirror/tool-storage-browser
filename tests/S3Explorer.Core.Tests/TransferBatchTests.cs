using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferBatchTests
{
    [Fact]
    public void ProjectionRecalculatesStableCountsAndBytes()
    {
        var batch = CreateBatch() with { DiscoveryCompleted = true, SkippedCount = 1 };
        var completed = CreateTask(batch, "done.bin", 10) with
        {
            State = TransferTaskState.Completed,
            TransferredBytes = 10,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var failed = CreateTask(batch, "failed.bin", 20) with
        {
            State = TransferTaskState.Failed,
            Failure = new TransferFailureInfo("failed", TransferFailureCategory.Network, Retryable: true)
        };
        var skipped = CreateTask(batch, "skipped.bin", 30) with
        {
            State = TransferTaskState.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var running = CreateTask(batch, "running.bin", 40) with
        {
            State = TransferTaskState.Running,
            AttemptCount = 1,
            StartedAt = DateTimeOffset.UtcNow
        };
        var progress = new Dictionary<Guid, long> { [running.Id] = 15 };

        var first = TransferBatchProjector.Project(batch, [completed, failed, skipped, running], progress);
        var second = TransferBatchProjector.Project(batch, [completed, failed, skipped, running], progress);

        Assert.Equal(first, second);
        Assert.Equal(5, first.TotalFiles);
        Assert.Equal(1, first.CompletedFiles);
        Assert.Equal(1, first.FailedFiles);
        Assert.Equal(2, first.SkippedFiles);
        Assert.Equal(1, first.ActiveFiles);
        Assert.Equal(100, first.TotalBytes);
        Assert.Equal(25, first.TransferredBytes);
        Assert.Equal(TransferBatchState.Running, first.State);
    }

    [Fact]
    public void FailureExportRemovesCredentialsAndPresignedQuery()
    {
        var batch = CreateBatch() with { DiscoveryCompleted = true };
        var task = CreateTask(batch, "folder/a,\"b.bin", 10) with
        {
            State = TransferTaskState.Failed,
            Failure = new TransferFailureInfo(
                "SecretKey=top-secret https://example.test/object?X-Amz-Signature=signature&X-Amz-Credential=credential",
                TransferFailureCategory.Authorization,
                403,
                "AccessDenied",
                Retryable: false)
        };

        var csv = TransferBatchProjector.ExportFailuresCsv(batch, [task]);

        Assert.DoesNotContain("top-secret", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", csv, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://example.test/object?[redacted]", csv, StringComparison.Ordinal);
        Assert.Contains("AccessDenied", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneChildFailureDoesNotStopBatchAndRetryOnlyUsesRetryableFailures()
    {
        var executor = new BatchExecutor();
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 2);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var batch = await queue.CreateBatchAsync(CreateBatch(), TestContext.Current.CancellationToken);
        var retryable = CreateTask(batch, "retryable.bin", 10) with { MaxAttempts = 1 };
        var denied = CreateTask(batch, "denied.bin", 20) with { MaxAttempts = 1 };
        var success = CreateTask(batch, "success.bin", 30);
        executor.Failures[retryable.Id] = new TransferFailureInfo("temporary", TransferFailureCategory.Network, Retryable: true);
        executor.Failures[denied.Id] = new TransferFailureInfo("denied", TransferFailureCategory.Authorization, Retryable: false);

        await queue.AddBatchTasksAsync(batch.Id, [retryable, denied, success], TestContext.Current.CancellationToken);
        await queue.CompleteBatchDiscoveryAsync(
            batch.Id, cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        Assert.Equal(TransferTaskState.Completed, queue.Snapshot.Tasks.Single(task => task.Id == success.Id).State);
        executor.Failures.Remove(retryable.Id);
        var retried = await queue.RetryBatchFailuresAsync(
            batch.Id, cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        Assert.Equal(1, retried);
        Assert.Equal(TransferTaskState.Completed, queue.Snapshot.Tasks.Single(task => task.Id == retryable.Id).State);
        Assert.Equal(TransferTaskState.Failed, queue.Snapshot.Tasks.Single(task => task.Id == denied.Id).State);
        var summary = TransferBatchProjector.Project(queue.Snapshot.Batches.Single(), queue.Snapshot.Tasks);
        Assert.Equal(2, summary.CompletedFiles);
        Assert.Equal(1, summary.FailedFiles);

        await queue.RemoveCompletedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, queue.Snapshot.Tasks.Count);
    }

    [Fact]
    public async Task SelectedRetryDoesNotRequeueOtherRetryableFailure()
    {
        var executor = new BatchExecutor();
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 2);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var batch = await queue.CreateBatchAsync(CreateBatch(), TestContext.Current.CancellationToken);
        var first = CreateTask(batch, "first.bin", 10) with { MaxAttempts = 1 };
        var second = CreateTask(batch, "second.bin", 10) with { MaxAttempts = 1 };
        var failure = new TransferFailureInfo("temporary", TransferFailureCategory.Service, Retryable: true);
        executor.Failures[first.Id] = failure;
        executor.Failures[second.Id] = failure;

        await queue.AddBatchTasksAsync(batch.Id, [first, second], TestContext.Current.CancellationToken);
        await queue.CompleteBatchDiscoveryAsync(
            batch.Id, cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);
        executor.Failures.Remove(first.Id);

        var retried = await queue.RetryBatchFailuresAsync(batch.Id, [first.Id], TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        Assert.Equal(1, retried);
        Assert.Equal(TransferTaskState.Completed, queue.Snapshot.Tasks.Single(task => task.Id == first.Id).State);
        Assert.Equal(TransferTaskState.Failed, queue.Snapshot.Tasks.Single(task => task.Id == second.Id).State);
    }

    [Fact]
    public async Task BatchCancelCancelsRunningAndQueuedChildren()
    {
        var executor = new BlockingBatchExecutor();
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var batch = await queue.CreateBatchAsync(CreateBatch(), TestContext.Current.CancellationToken);
        var running = CreateTask(batch, "running.bin", 10);
        var queued = CreateTask(batch, "queued.bin", 10);

        await queue.AddBatchTasksAsync(batch.Id, [running, queued], TestContext.Current.CancellationToken);
        await queue.CompleteBatchDiscoveryAsync(
            batch.Id, cancellationToken: TestContext.Current.CancellationToken);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.CancelBatchAsync(batch.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        Assert.All(queue.Snapshot.Tasks, task => Assert.Equal(TransferTaskState.Cancelled, task.State));
        Assert.True(queue.Snapshot.Batches.Single().CancellationRequested);
        Assert.Equal(TransferBatchState.Cancelled,
            TransferBatchProjector.Project(queue.Snapshot.Batches.Single(), queue.Snapshot.Tasks).State);
    }

    private static TransferBatchRecord CreateBatch() => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = Guid.NewGuid(),
        ProfileName = "profile",
        Name = "folder",
        Bucket = "bucket",
        RootPath = "folder",
        Direction = TransferDirection.Upload
    };

    private static TransferTaskRecord CreateTask(TransferBatchRecord batch, string relativePath, long size) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = batch.Id,
        ProfileId = batch.ProfileId,
        ProfileName = batch.ProfileName,
        Direction = batch.Direction,
        Kind = TransferTaskKind.FolderBatchItem,
        Bucket = batch.Bucket,
        ObjectKey = $"folder/{relativePath}",
        LocalPath = Path.Combine(Path.GetTempPath(), relativePath.Replace('/', Path.DirectorySeparatorChar)),
        RelativePath = relativePath,
        TotalBytes = size,
        MaxAttempts = 3
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("Batch queue condition was not reached.");
            await Task.Delay(20);
        }
    }

    private sealed class MemoryStore : ITransferTaskStore
    {
        public TransferStoreSnapshot Snapshot { get; private set; } = new();
        public Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task SaveAsync(TransferStoreSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class BatchExecutor : ITransferTaskExecutor
    {
        public Dictionary<Guid, TransferFailureInfo> Failures { get; } = [];

        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failures.TryGetValue(context.Task.Id, out var failure))
                throw new TransferExecutionException(failure);
            context.ReportProgress(new TransferProgress(context.Task.TotalBytes, context.Task.TotalBytes));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingBatchExecutor : ITransferTaskExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
