using S3Explorer.Core;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class TransferExecutionTests
{
    [Fact]
    public void ResumePlannerAcceptsMatchingCheckpoint()
    {
        var checkpoint = new DownloadCheckpoint("file.part", 50, 100, "\"etag\"", "version");
        var decision = DownloadResumePlanner.Decide(
            temporaryFileExists: true,
            temporaryLength: 50,
            checkpoint,
            new RemoteObjectIdentity(100, "etag", "version"));

        Assert.True(decision.Resume);
        Assert.False(decision.ResetTemporaryFile);
        Assert.Equal(50, decision.Offset);
    }

    [Theory]
    [InlineData(101, "etag", "version")]
    [InlineData(100, "changed", "version")]
    [InlineData(100, "etag", "changed")]
    public void ResumePlannerResetsWhenRemoteIdentityChanges(long length, string etag, string version)
    {
        var checkpoint = new DownloadCheckpoint("file.part", 50, 100, "etag", "version");
        var decision = DownloadResumePlanner.Decide(true, 50, checkpoint, new RemoteObjectIdentity(length, etag, version));

        Assert.False(decision.Resume);
        Assert.True(decision.ResetTemporaryFile);
        Assert.Equal(0, decision.Offset);
    }

    [Fact]
    public async Task BandwidthLimiterIsUnlimitedAtZeroAndCancellationInterruptsWait()
    {
        var limiter = new SharedTransferBandwidthLimiter();
        limiter.Configure(0, 0);
        await limiter.WaitAsync(TransferDirection.Upload, 1024, CancellationToken.None);

        limiter.Configure(100, 100);
        await limiter.WaitAsync(TransferDirection.Download, 100, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await limiter.WaitAsync(TransferDirection.Download, 100, cancellation.Token));
    }

    [Fact]
    public void AtomicCommitRejectsIncompleteTemporaryFileAndPreservesDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "result.bin");
        var temporary = ResumableDownloadFile.TemporaryPath(destination);
        try
        {
            File.WriteAllBytes(destination, [1, 2, 3]);
            File.WriteAllBytes(temporary, [9, 9]);

            Assert.Throws<IOException>(() => ResumableDownloadFile.Commit(temporary, destination, 3));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(destination));
            Assert.True(File.Exists(temporary));

            File.WriteAllBytes(temporary, [4, 5, 6]);
            ResumableDownloadFile.Commit(temporary, destination, 3);
            Assert.Equal([4, 5, 6], File.ReadAllBytes(destination));
            Assert.False(File.Exists(temporary));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PreparePreservesValidOffsetAndCanResetInvalidPartialFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.part");
        try
        {
            File.WriteAllBytes(path, new byte[12]);
            ResumableDownloadFile.Prepare(path, reset: false, offset: 12);
            Assert.Equal(12, new FileInfo(path).Length);
            ResumableDownloadFile.Prepare(path, reset: true, offset: 0);
            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RetryBackoffIsExponentialAndCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RetryBackoff.Calculate(2, 1));
        Assert.Equal(TimeSpan.FromSeconds(4), RetryBackoff.Calculate(2, 2));
        Assert.Equal(TimeSpan.FromHours(1), RetryBackoff.Calculate(60, 20));
        Assert.Equal(TimeSpan.Zero, RetryBackoff.Calculate(0, 3));
    }

    [Fact]
    public async Task RetryableFailureIsRetriedAndCompletes()
    {
        var executor = new RetryOnceExecutor();
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var task = CreateTask() with { MaxAttempts = 2, RetryBaseDelaySeconds = 0 };

        await queue.EnqueueAsync(task, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        var result = queue.Snapshot.Tasks.Single();
        Assert.Equal(TransferTaskState.Completed, result.State);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, executor.ExecutionCount);
    }

    [Fact]
    public async Task NonRetryableFailureStopsAfterOneAttempt()
    {
        var executor = new NonRetryableExecutor();
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(CreateTask() with { MaxAttempts = 5, RetryBaseDelaySeconds = 0 }, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        var result = queue.Snapshot.Tasks.Single();
        Assert.Equal(TransferTaskState.Failed, result.State);
        Assert.Equal(1, result.AttemptCount);
        Assert.False(result.Failure!.Retryable);
    }

    [Fact]
    public async Task PausePreservesPersistedDownloadCheckpoint()
    {
        var executor = new CheckpointBlockingExecutor();
        var store = new MemoryStore();
        await using var queue = new PersistentTransferQueue(store, executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);
        var task = CreateTask() with { Direction = TransferDirection.Download, TotalBytes = 100 };

        await queue.EnqueueAsync(task, TestContext.Current.CancellationToken);
        await executor.CheckpointWritten.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await queue.PauseAsync(task.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.Snapshot.Tasks.Single().State == TransferTaskState.Paused);

        var saved = store.Snapshot.Tasks.Single();
        Assert.Equal(40, saved.TransferredBytes);
        Assert.NotNull(saved.DownloadCheckpoint);
        Assert.Equal(40, saved.DownloadCheckpoint!.CompletedBytes);
    }

    [Theory]
    [InlineData("timeout", TransferFailureCategory.Timeout, true)]
    [InlineData("http500", TransferFailureCategory.Service, true)]
    [InlineData("disconnect", TransferFailureCategory.Network, true)]
    [InlineData("disk", TransferFailureCategory.LocalIo, false)]
    public void FaultInjectionIsClassifiedForSafeRetry(
        string fault,
        TransferFailureCategory expectedCategory,
        bool expectedRetryable)
    {
        var failure = TransferFailureClassifier.Classify(CreateFault(fault));

        Assert.Equal(expectedCategory, failure.Category);
        Assert.Equal(expectedRetryable, failure.Retryable);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("http500")]
    [InlineData("disconnect")]
    public async Task TransientFaultInjectionRetriesAndCompletes(string fault)
    {
        var executor = new FaultOnceExecutor(CreateFault(fault));
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(CreateTask() with { MaxAttempts = 2, RetryBaseDelaySeconds = 0 }, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        var result = Assert.Single(queue.Snapshot.Tasks);
        Assert.Equal(TransferTaskState.Completed, result.State);
        Assert.Equal(2, result.AttemptCount);
    }

    [Fact]
    public async Task DiskWriteFailureIsNotRetried()
    {
        var executor = new FaultOnceExecutor(CreateFault("disk"));
        await using var queue = new PersistentTransferQueue(new MemoryStore(), executor, maxConcurrency: 1);
        await queue.InitializeAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(CreateTask() with { MaxAttempts = 4, RetryBaseDelaySeconds = 0 }, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => queue.ActiveCount == 0);

        var result = Assert.Single(queue.Snapshot.Tasks);
        Assert.Equal(TransferTaskState.Failed, result.State);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(TransferFailureCategory.LocalIo, result.Failure!.Category);
    }

    private static Exception CreateFault(string fault) => fault switch
    {
        "timeout" => new TaskCanceledException("simulated network timeout"),
        "http500" => new HttpRequestException(
            "simulated HTTP 500", null, HttpStatusCode.InternalServerError),
        "disconnect" => new IOException(
            "simulated connection reset", new SocketException((int)SocketError.ConnectionReset)),
        "disk" => new IOException("simulated disk write failure"),
        _ => throw new ArgumentOutOfRangeException(nameof(fault))
    };

    private static TransferTaskRecord CreateTask() => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = Guid.NewGuid(),
        ProfileName = "profile",
        Direction = TransferDirection.Upload,
        Bucket = "bucket",
        ObjectKey = "object.bin",
        LocalPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin"),
        TotalBytes = 10,
        MaxAttempts = 3
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("Queue condition was not reached.");
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

    private sealed class RetryOnceExecutor : ITransferTaskExecutor
    {
        public int ExecutionCount { get; private set; }
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            if (ExecutionCount == 1)
                throw new TransferExecutionException(new TransferFailureInfo("temporary", TransferFailureCategory.Network, Retryable: true));
            context.ReportProgress(new TransferProgress(context.Task.TotalBytes, context.Task.TotalBytes));
            return Task.CompletedTask;
        }
    }

    private sealed class NonRetryableExecutor : ITransferTaskExecutor
    {
        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) =>
            throw new TransferExecutionException(new TransferFailureInfo("denied", TransferFailureCategory.Authorization, Retryable: false));
    }

    private sealed class CheckpointBlockingExecutor : ITransferTaskExecutor
    {
        public TaskCompletionSource CheckpointWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            var checkpoint = new DownloadCheckpoint("file.part", 40, 100, "etag");
            await context.UpdateCheckpointAsync(40, checkpoint, null, cancellationToken);
            CheckpointWritten.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FaultOnceExecutor(Exception fault) : ITransferTaskExecutor
    {
        private int _count;

        public Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1)
                throw fault;
            context.ReportProgress(new TransferProgress(context.Task.TotalBytes, context.Task.TotalBytes));
            return Task.CompletedTask;
        }
    }
}
