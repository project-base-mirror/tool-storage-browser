using System.Reflection;
using System.Runtime.InteropServices;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class WindowsSleepInhibitorTests
{
    [Fact]
    public void PowerRequestFunctionsComeFromKernel32()
    {
        foreach (var name in new[] { "PowerCreateRequest", "PowerSetRequest", "PowerClearRequest" })
        {
            var method = typeof(WindowsSleepInhibitor).GetMethod(
                name,
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var import = Assert.IsType<DllImportAttribute>(
                method.GetCustomAttribute<DllImportAttribute>());
            Assert.Equal(WindowsSleepInhibitor.NativeLibraryName, import.Value, ignoreCase: true);
        }
    }

    [Fact]
    public void AcquireSupportsNestedTransferLeases()
    {
        using var first = WindowsSleepInhibitor.Acquire();
        using var second = WindowsSleepInhibitor.Acquire();
    }

    [Fact]
    public async Task TransferExecutorReachesOperationAfterAcquiringPowerLease()
    {
        var profile = new ConnectionProfile { Id = Guid.NewGuid(), Name = "power-test" };
        var task = new TransferTaskRecord
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Direction = TransferDirection.DeleteLocal,
            Bucket = "test-bucket",
            ObjectKey = "nonexistent.bin",
            LocalPath = Path.Combine(Path.GetTempPath(), $"s3explorer-power-{Guid.NewGuid():N}.bin")
        };
        var context = new RecordingExecutionContext(task);
        var executor = new S3TransferTaskExecutor(
            new StaticProfileStore(profile),
            null!,
            new TransferRuntimeConfiguration(),
            null!,
            new SimpleFileLogger(Path.Combine(Path.GetTempPath(), "S3Explorer.App.Tests")));

        await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(new TransferProgress(0, 0), context.LastProgress);
    }

    private sealed class StaticProfileStore(ConnectionProfile profile) : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([profile]);

        public Task SaveAsync(
            IReadOnlyCollection<ConnectionProfile> profiles,
            CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class RecordingExecutionContext(TransferTaskRecord task) : ITransferTaskExecutionContext
    {
        public TransferTaskRecord Task { get; } = task;
        public TransferProgress? LastProgress { get; private set; }

        public void ReportProgress(TransferProgress progress) => LastProgress = progress;

        public Task UpdateCheckpointAsync(
            long transferredBytes,
            DownloadCheckpoint? downloadCheckpoint,
            MultipartUploadCheckpoint? multipartCheckpoint,
            CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;

        public Task UpdateDestinationSnapshotAsync(
            bool destinationExistedBeforeTransfer,
            CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
    }
}
