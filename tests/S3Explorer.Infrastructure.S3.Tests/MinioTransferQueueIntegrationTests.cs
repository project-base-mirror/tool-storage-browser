using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioTransferQueueIntegrationTests
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task Durable_queue_pauses_cancels_restarts_and_resets_changed_remote_download()
    {
        var configuration = ProviderMatrixCase.Selected().Resolve();
        var profile = configuration.CreateProfile();
        var service = new S3StorageService(new S3ClientFactory());
        var bucket = $"s3explorer-queue-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-minio-queue", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var upload = Path.Combine(root, "upload.bin");
        var replacement = Path.Combine(root, "replacement.bin");
        var download = Path.Combine(root, "download.bin");
        var queuePath = Path.Combine(root, "transfers.json");
        var remoteKey = "queue/resumable.bin";
        var cancelKey = "queue/cancelled.bin";
        var created = false;

        try
        {
            await File.WriteAllBytesAsync(upload, RepeatedBytes(2 * 1024 * 1024, 0x31));
            await File.WriteAllBytesAsync(replacement, RepeatedBytes(2 * 1024 * 1024, 0x7A));
            await service.CreateBucketAsync(profile, bucket, profile.Region, CancellationToken.None);
            created = true;

            var store = new JsonTransferTaskStore(queuePath);
            var executor = new MinioQueueExecutor(profile, service, 512 * 1024);
            var uploadTask = CreateTask(profile, bucket, remoteKey, upload, TransferDirection.Upload);

            await using (var firstQueue = new PersistentTransferQueue(store, executor, maxConcurrency: 1))
            {
                await firstQueue.InitializeAsync();
                var progress = WaitForProgress(firstQueue, uploadTask.Id);
                await firstQueue.EnqueueAsync(uploadTask);
                await progress.WaitAsync(TimeSpan.FromSeconds(10));
                await firstQueue.PauseAsync(uploadTask.Id);
                await WaitUntilAsync(() =>
                    firstQueue.Snapshot.Tasks.Single(task => task.Id == uploadTask.Id).State == TransferTaskState.Paused);
            }

            await using (var restartedQueue = new PersistentTransferQueue(store, executor, maxConcurrency: 1))
            {
                await restartedQueue.InitializeAsync();
                Assert.Equal(
                    TransferTaskState.Paused,
                    restartedQueue.Snapshot.Tasks.Single(task => task.Id == uploadTask.Id).State);
                await restartedQueue.ResumeAsync(uploadTask.Id);
                await WaitUntilAsync(() =>
                    restartedQueue.Snapshot.Tasks.Single(task => task.Id == uploadTask.Id).State == TransferTaskState.Completed,
                    20);
                Assert.True(await service.ObjectExistsAsync(profile, bucket, remoteKey, CancellationToken.None));

                var cancelTask = CreateTask(profile, bucket, cancelKey, upload, TransferDirection.Upload);
                var progress = WaitForProgress(restartedQueue, cancelTask.Id);
                await restartedQueue.EnqueueAsync(cancelTask);
                await progress.WaitAsync(TimeSpan.FromSeconds(10));
                await restartedQueue.CancelAsync(cancelTask.Id);
                await WaitUntilAsync(() =>
                    restartedQueue.Snapshot.Tasks.Single(task => task.Id == cancelTask.Id).State == TransferTaskState.Cancelled);
                Assert.False(await service.ObjectExistsAsync(profile, bucket, cancelKey, CancellationToken.None));
            }

            var oldProperties = await service.GetObjectPropertiesAsync(
                profile, bucket, remoteKey, CancellationToken.None);
            var temporary = ResumableDownloadFile.TemporaryPath(download);
            var partialLength = 128 * 1024;
            await File.WriteAllBytesAsync(temporary, RepeatedBytes(partialLength, 0x31));
            var downloadTask = CreateTask(
                profile, bucket, remoteKey, download, TransferDirection.Download, oldProperties.Size) with
            {
                State = TransferTaskState.Paused,
                TransferredBytes = partialLength,
                DownloadCheckpoint = new DownloadCheckpoint(
                    temporary,
                    partialLength,
                    oldProperties.Size,
                    oldProperties.ETag)
            };
            var downloadStore = new JsonTransferTaskStore(Path.Combine(root, "download-transfers.json"));
            await downloadStore.SaveAsync(new TransferStoreSnapshot { Tasks = [downloadTask] });

            await service.UploadFileAsync(
                profile,
                bucket,
                remoteKey,
                replacement,
                "STANDARD",
                CreateContext(),
                CancellationToken.None);

            await using (var downloadQueue = new PersistentTransferQueue(
                             downloadStore,
                             new MinioQueueExecutor(profile, service, 0),
                             maxConcurrency: 1))
            {
                await downloadQueue.InitializeAsync();
                await downloadQueue.ResumeAsync(downloadTask.Id);
                await WaitUntilAsync(() =>
                    downloadQueue.Snapshot.Tasks.Single().State == TransferTaskState.Completed,
                    20);
            }

            Assert.Equal(await File.ReadAllBytesAsync(replacement), await File.ReadAllBytesAsync(download));
            Assert.False(File.Exists(temporary));
        }
        finally
        {
            if (created)
            {
                try { await service.DeleteObjectsAsync(profile, bucket, [remoteKey, cancelKey], CancellationToken.None); }
                catch { }
                try { await service.EmptyBucketAsync(profile, bucket, CancellationToken.None); }
                catch { }
                try { await service.DeleteEmptyBucketAsync(profile, bucket, CancellationToken.None); }
                catch { }
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static TransferTaskRecord CreateTask(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        TransferDirection direction,
        long? totalBytes = null) => new()
    {
        ProfileId = profile.Id,
        ProfileName = profile.Name,
        Direction = direction,
        Bucket = bucket,
        ObjectKey = key,
        LocalPath = localPath,
        TotalBytes = totalBytes ?? new FileInfo(localPath).Length,
        MaxAttempts = 2,
        RetryBaseDelaySeconds = 0
    };

    private static Task WaitForProgress(PersistentTransferQueue queue, Guid taskId)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.ProgressChanged += (_, args) =>
        {
            if (args.TaskId == taskId && args.Progress.TransferredBytes > 0)
                source.TrySetResult();
        };
        return source.Task;
    }

    private static byte[] RepeatedBytes(int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static TransferOperationContext CreateContext()
    {
        var limiter = new SharedTransferBandwidthLimiter();
        limiter.Configure(0, 0);
        return new TransferOperationContext(
            new TransferExecutionOptions(),
            limiter,
            null,
            null,
            _ => { },
            (_, _, _, _) => Task.CompletedTask);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("MinIO queue condition was not reached.");
            await Task.Delay(50);
        }
    }

    private sealed class MinioQueueExecutor(
        ConnectionProfile profile,
        S3StorageService service,
        long bytesPerSecond) : ITransferTaskExecutor
    {
        public async Task ExecuteAsync(
            ITransferTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            var limiter = new SharedTransferBandwidthLimiter();
            limiter.Configure(bytesPerSecond, bytesPerSecond);
            var transfer = new TransferOperationContext(
                new TransferExecutionOptions(),
                limiter,
                context.Task.DownloadCheckpoint,
                context.Task.MultipartCheckpoint,
                context.ReportProgress,
                context.UpdateCheckpointAsync);
            if (context.Task.Direction == TransferDirection.Upload)
            {
                await service.UploadFileAsync(
                    profile,
                    context.Task.Bucket,
                    context.Task.ObjectKey,
                    context.Task.LocalPath,
                    context.Task.StorageClass,
                    transfer,
                    cancellationToken);
            }
            else
            {
                await service.DownloadFileAsync(
                    profile,
                    context.Task.Bucket,
                    context.Task.ObjectKey,
                    context.Task.LocalPath,
                    transfer,
                    cancellationToken);
            }
        }

        public Task AbortMultipartAsync(
            ITransferTaskExecutionContext context,
            CancellationToken cancellationToken)
        {
            var checkpoint = context.Task.MultipartCheckpoint;
            return checkpoint is null
                ? Task.CompletedTask
                : service.AbortMultipartUploadAsync(
                    profile,
                    context.Task.Bucket,
                    context.Task.ObjectKey,
                    checkpoint.UploadId,
                    cancellationToken);
        }
    }
}
