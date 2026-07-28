using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CdnUploadAutomationTests
{
    [Fact]
    public async Task NewObjectUsesWarmupPolicy()
    {
        var fixture = await CreateAsync(CdnUploadAction.Warmup, CdnUploadAction.PurgeThenWarmup);
        var task = UploadTask(fixture.StorageProfileId, fixture.AutomationStartedAt.AddMinutes(1), destinationExisted: false);

        var jobs = await fixture.Coordinator.ProcessCompletedUploadAsync(task, fixture.Configuration);
        await fixture.Queue.WaitForIdleAsync();

        var job = Assert.Single(jobs);
        Assert.Equal(CdnJobAction.Warmup, job.Action);
        Assert.Equal(task.Id, job.TransferTaskId);
        Assert.Equal(fixture.Binding.Id, job.BindingId);
        Assert.Equal(CdnJobState.Completed, Assert.Single(fixture.Queue.Snapshot.Jobs).State);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task OverwriteUsesPurgeThenWarmupPolicy()
    {
        var fixture = await CreateAsync(CdnUploadAction.None, CdnUploadAction.PurgeThenWarmup);
        var task = UploadTask(fixture.StorageProfileId, fixture.AutomationStartedAt.AddMinutes(1), destinationExisted: true);

        var jobs = await fixture.Coordinator.ProcessCompletedUploadAsync(task, fixture.Configuration);

        Assert.Equal(CdnJobAction.PurgeThenWarmup, Assert.Single(jobs).Action);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task StartupBackfillIgnoresLegacyAndUnknownDestinationTasks()
    {
        var fixture = await CreateAsync(CdnUploadAction.Warmup, CdnUploadAction.Warmup);
        var old = UploadTask(fixture.StorageProfileId, fixture.AutomationStartedAt.AddMinutes(-1), destinationExisted: false);
        var unknown = UploadTask(fixture.StorageProfileId, fixture.AutomationStartedAt.AddMinutes(1), destinationExisted: null);
        var current = UploadTask(fixture.StorageProfileId, fixture.AutomationStartedAt.AddMinutes(2), destinationExisted: false);

        var jobs = await fixture.Coordinator.ProcessCompletedUploadsAsync(
            [old, unknown, current],
            fixture.Configuration);

        Assert.Single(jobs);
        Assert.Equal(current.Id, jobs[0].TransferTaskId);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task ReprocessingCompletedUploadIsIdempotent()
    {
        var fixture = await CreateAsync(CdnUploadAction.Warmup, CdnUploadAction.None);
        var task = UploadTask(fixture.StorageProfileId, fixture.AutomationStartedAt.AddMinutes(1), destinationExisted: false);

        var first = await fixture.Coordinator.ProcessCompletedUploadAsync(task, fixture.Configuration);
        var second = await fixture.Coordinator.ProcessCompletedUploadAsync(task, fixture.Configuration);

        Assert.Equal(Assert.Single(first).Id, Assert.Single(second).Id);
        Assert.Single(fixture.Queue.Snapshot.Jobs);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task SnapshotIsOnlyRequiredForBindingsWithAutomation()
    {
        var fixture = await CreateAsync(CdnUploadAction.None, CdnUploadAction.None);

        Assert.False(CdnUploadAutomationCoordinator.RequiresDestinationSnapshot(
            fixture.Configuration,
            fixture.StorageProfileId,
            "site",
            "assets/app.js"));

        var enabled = fixture.Configuration with
        {
            Bindings =
            [
                fixture.Binding with { NewObjectAction = CdnUploadAction.Warmup }
            ]
        };
        Assert.True(CdnUploadAutomationCoordinator.RequiresDestinationSnapshot(
            enabled,
            fixture.StorageProfileId,
            "site",
            "assets/app.js"));
        await fixture.DisposeAsync();
    }

    private static async Task<Fixture> CreateAsync(
        CdnUploadAction newObjectAction,
        CdnUploadAction overwriteAction)
    {
        var startedAt = DateTimeOffset.Parse("2026-07-28T00:00:00Z");
        var store = new MemoryStore(new CdnJobStoreSnapshot { AutomationStartedAt = startedAt });
        var queue = new PersistentCdnJobQueue(store, new CompletedExecutor());
        await queue.InitializeAsync();

        var storageId = Guid.NewGuid();
        var profile = new CdnProfile
        {
            Name = "site",
            BaseUrl = "https://cdn.example",
            PurgeEndpointTemplate = "https://api.example/purge?url={url}"
        };
        var binding = new CdnBinding
        {
            StorageProfileId = storageId,
            Bucket = "site",
            SourcePrefix = "assets/",
            CdnProfileId = profile.Id,
            NewObjectAction = newObjectAction,
            OverwriteAction = overwriteAction
        };
        return new Fixture(
            queue,
            new CdnUploadAutomationCoordinator(queue),
            new CdnConfiguration([profile], [binding]),
            binding,
            storageId,
            startedAt);
    }

    private static TransferTaskRecord UploadTask(
        Guid storageProfileId,
        DateTimeOffset completedAt,
        bool? destinationExisted) => new()
    {
        ProfileId = storageProfileId,
        ProfileName = "site-storage",
        Direction = TransferDirection.Upload,
        Bucket = "site",
        ObjectKey = "assets/app.js",
        LocalPath = "app.js",
        RelativePath = "app.js",
        TotalBytes = 10,
        State = TransferTaskState.Completed,
        DestinationExistedBeforeTransfer = destinationExisted,
        CreatedAt = completedAt.AddMinutes(-1),
        CompletedAt = completedAt
    };

    private sealed record Fixture(
        PersistentCdnJobQueue Queue,
        CdnUploadAutomationCoordinator Coordinator,
        CdnConfiguration Configuration,
        CdnBinding Binding,
        Guid StorageProfileId,
        DateTimeOffset AutomationStartedAt)
    {
        public ValueTask DisposeAsync() => Queue.DisposeAsync();
    }

    private sealed class MemoryStore(CdnJobStoreSnapshot snapshot) : ICdnJobStore
    {
        private CdnJobStoreSnapshot _snapshot = snapshot;

        public Task<CdnJobStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task SaveAsync(CdnJobStoreSnapshot value, CancellationToken cancellationToken = default)
        {
            value.Validate();
            _snapshot = value;
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedExecutor : ICdnJobExecutor
    {
        public Task<CdnProviderResult> ExecuteAsync(
            CdnJobRecord job,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CdnProviderResult(
                CdnProviderOperationState.Completed,
                "done"));
    }
}
