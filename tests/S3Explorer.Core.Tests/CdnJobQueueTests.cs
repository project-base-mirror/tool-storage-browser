using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CdnJobQueueTests
{
    [Fact]
    public async Task EnqueueUsesIdempotencyKeyAndExecutesOnce()
    {
        var store = new MemoryStore();
        var executor = new SequenceExecutor(
            new CdnProviderResult(CdnProviderOperationState.Completed, "done"));
        await using var queue = new PersistentCdnJobQueue(store, executor);
        await queue.InitializeAsync();
        var first = Job("same-key");

        var created = await queue.EnqueueAsync(first);
        var duplicate = await queue.EnqueueAsync(Job("same-key"));
        await queue.WaitForIdleAsync();

        Assert.Equal(created.Id, duplicate.Id);
        Assert.Single(queue.Snapshot.Jobs);
        Assert.Equal(CdnJobState.Completed, queue.Snapshot.Jobs[0].State);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task RetryableFailureIsRetriedAndCompleted()
    {
        var now = DateTimeOffset.Parse("2026-07-28T00:00:00Z");
        var store = new MemoryStore(new CdnJobStoreSnapshot { AutomationStartedAt = now });
        var executor = new SequenceExecutor(
            new CdnProviderResult(CdnProviderOperationState.Failed, "temporary", Retryable: true),
            new CdnProviderResult(CdnProviderOperationState.Completed, "done"));
        await using var queue = new PersistentCdnJobQueue(
            store,
            executor,
            clock: () => now,
            jitter: () => 0);
        await queue.InitializeAsync();
        await queue.EnqueueAsync(Job("retry") with { RetryBaseDelaySeconds = 0 });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(timeout.Token);

        var job = Assert.Single(queue.Snapshot.Jobs);
        Assert.Equal(CdnJobState.Completed, job.State);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task RunningJobIsRecoveredAfterRestart()
    {
        var now = DateTimeOffset.Parse("2026-07-28T00:00:00Z");
        var running = Job("recover") with
        {
            State = CdnJobState.Running,
            AttemptCount = 1,
            StartedAt = now.AddMinutes(-1)
        };
        var store = new MemoryStore(new CdnJobStoreSnapshot
        {
            AutomationStartedAt = now.AddDays(-1),
            Jobs = [running]
        });
        var executor = new SequenceExecutor(
            new CdnProviderResult(CdnProviderOperationState.Completed, "done"));
        await using var queue = new PersistentCdnJobQueue(
            store,
            executor,
            clock: () => now,
            jitter: () => 0);

        await queue.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.WaitForIdleAsync(timeout.Token);

        Assert.Equal(CdnJobState.Completed, Assert.Single(queue.Snapshot.Jobs).State);
        Assert.True(store.SaveCount >= 2);
    }

    [Fact]
    public async Task AcceptedProviderTaskIsQueriedUntilComplete()
    {
        var now = DateTimeOffset.Parse("2026-07-28T00:00:00Z");
        var executor = new SequenceExecutor(
            new CdnProviderResult(
                CdnProviderOperationState.Accepted,
                "accepted",
                ProviderTaskId: "provider-1"),
            new CdnProviderResult(CdnProviderOperationState.Completed, "done"));
        await using var queue = new PersistentCdnJobQueue(
            new MemoryStore(new CdnJobStoreSnapshot { AutomationStartedAt = now }),
            executor,
            clock: () => now,
            jitter: () => 0);
        await queue.InitializeAsync();
        await queue.EnqueueAsync(Job("async"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (queue.Snapshot.Jobs.FirstOrDefault()?.State != CdnJobState.WaitingProvider)
            await Task.Delay(20, timeout.Token);
        now = now.AddSeconds(6);
        await queue.WaitForIdleAsync(timeout.Token);

        var job = Assert.Single(queue.Snapshot.Jobs);
        Assert.Equal(CdnJobState.Completed, job.State);
        Assert.Equal("provider-1", job.ProviderTaskId);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task CancelledJobRemainsCancelled()
    {
        var executor = new BlockingExecutor();
        await using var queue = new PersistentCdnJobQueue(new MemoryStore(), executor);
        await queue.InitializeAsync();
        var job = await queue.EnqueueAsync(Job("cancel"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.Started.Task.WaitAsync(timeout.Token);
        await queue.CancelAsync(job.Id);
        await Task.Delay(100, timeout.Token);

        Assert.Equal(CdnJobState.Cancelled, Assert.Single(queue.Snapshot.Jobs).State);
    }

    [Fact]
    public async Task ActiveDuplicateUrlIsCoalescedEvenWithDifferentIdempotencyKeys()
    {
        var executor = new BlockingExecutor();
        await using var queue = new PersistentCdnJobQueue(new MemoryStore(), executor);
        await queue.InitializeAsync();
        var first = Job("first");
        var created = await queue.EnqueueAsync(first);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.Started.Task.WaitAsync(timeout.Token);
        var duplicate = await queue.EnqueueAsync(Job("second") with
        {
            CdnProfileId = first.CdnProfileId,
            Action = first.Action,
            Urls = first.Urls
        });

        Assert.Equal(created.Id, duplicate.Id);
        Assert.Single(queue.Snapshot.Jobs);
    }

    [Fact]
    public async Task RunsDifferentProfilesInParallelButSerializesEachProfile()
    {
        var executor = new ProfileBlockingExecutor();
        await using var queue = new PersistentCdnJobQueue(
            new MemoryStore(),
            executor,
            maxConcurrency: 4,
            maxConcurrencyPerProfile: 1);
        await queue.InitializeAsync();
        var firstProfile = Guid.NewGuid();
        var secondProfile = Guid.NewGuid();
        await queue.EnqueueAsync(Job("first") with { CdnProfileId = firstProfile });
        await queue.EnqueueAsync(Job("blocked-same-profile") with
        {
            CdnProfileId = firstProfile,
            Urls = ["https://cdn.example/second.bin"]
        });
        await queue.EnqueueAsync(Job("other-profile") with { CdnProfileId = secondProfile });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.TwoProfilesStarted.Task.WaitAsync(timeout.Token);

        Assert.Equal(2, executor.StartedProfiles.Count);
        Assert.Contains(firstProfile, executor.StartedProfiles);
        Assert.Contains(secondProfile, executor.StartedProfiles);
    }

    private static CdnJobRecord Job(string key) => new()
    {
        IdempotencyKey = key,
        CdnProfileId = Guid.NewGuid(),
        Action = CdnJobAction.Warmup,
        Urls = ["https://cdn.example/file.bin"],
        RetryBaseDelaySeconds = 0
    };

    private sealed class MemoryStore : ICdnJobStore
    {
        private CdnJobStoreSnapshot _snapshot;
        public int SaveCount { get; private set; }

        public MemoryStore(CdnJobStoreSnapshot? snapshot = null) =>
            _snapshot = snapshot ?? new CdnJobStoreSnapshot();

        public Task<CdnJobStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task SaveAsync(
            CdnJobStoreSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            snapshot.Validate();
            _snapshot = snapshot;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceExecutor(params CdnProviderResult[] results) : ICdnJobExecutor
    {
        private readonly Queue<CdnProviderResult> _results = new(results);
        public int CallCount { get; private set; }

        public Task<CdnProviderResult> ExecuteAsync(
            CdnJobRecord job,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : new CdnProviderResult(CdnProviderOperationState.Completed, "done"));
        }
    }

    private sealed class BlockingExecutor : ICdnJobExecutor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CdnProviderResult> ExecuteAsync(
            CdnJobRecord job,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ProfileBlockingExecutor : ICdnJobExecutor
    {
        private readonly object _sync = new();
        public HashSet<Guid> StartedProfiles { get; } = [];
        public TaskCompletionSource TwoProfilesStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CdnProviderResult> ExecuteAsync(
            CdnJobRecord job,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                StartedProfiles.Add(job.CdnProfileId);
                if (StartedProfiles.Count >= 2) TwoProfilesStarted.TrySetResult();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
