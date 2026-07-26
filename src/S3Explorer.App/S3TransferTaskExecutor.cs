using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class S3TransferTaskExecutor(
    IProfileStore profiles,
    IS3StorageService storage,
    TransferRuntimeConfiguration runtime) : ITransferTaskExecutor
{
    public async Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken)
    {
        var task = context.Task;
        var profile = await ResolveProfileAsync(task, cancellationToken).ConfigureAwait(false);
        var transfer = runtime.CreateContext(context);
        using var sleepLease = WindowsSleepInhibitor.Acquire();

        switch (task.Direction)
        {
            case TransferDirection.Upload:
                if (!File.Exists(task.LocalPath))
                    throw new FileNotFoundException("上传源文件不存在。", task.LocalPath);
                await storage.UploadFileAsync(
                    profile, task.Bucket, task.ObjectKey, task.LocalPath, task.StorageClass, transfer, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case TransferDirection.Download:
                var directory = Path.GetDirectoryName(task.LocalPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                await storage.DownloadFileAsync(
                    profile, task.Bucket, task.ObjectKey, task.LocalPath, transfer, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case TransferDirection.Copy:
                await storage.CopyObjectAsync(
                    profile, task.Bucket, task.ObjectKey,
                    task.DestinationBucket, task.DestinationObjectKey, cancellationToken)
                    .ConfigureAwait(false);
                context.ReportProgress(new TransferProgress(task.TotalBytes, task.TotalBytes));
                return;

            case TransferDirection.Move:
                await storage.MoveObjectAsync(
                    profile, task.Bucket, task.ObjectKey,
                    task.DestinationBucket, task.DestinationObjectKey, cancellationToken)
                    .ConfigureAwait(false);
                context.ReportProgress(new TransferProgress(task.TotalBytes, task.TotalBytes));
                return;

            case TransferDirection.DeleteRemote:
                await storage.DeleteObjectsAsync(profile, task.Bucket, [task.ObjectKey], cancellationToken)
                    .ConfigureAwait(false);
                context.ReportProgress(new TransferProgress(0, 0));
                return;

            case TransferDirection.DeleteLocal:
                if (File.Exists(task.LocalPath))
                    File.Delete(task.LocalPath);
                context.ReportProgress(new TransferProgress(0, 0));
                return;

            default:
                throw new InvalidOperationException($"不支持的传输方向：{task.Direction}");
        }
    }

    public async Task AbortMultipartAsync(
        ITransferTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        var task = context.Task;
        var checkpoint = task.MultipartCheckpoint;
        if (checkpoint is null) return;
        var profile = await ResolveProfileAsync(task, cancellationToken).ConfigureAwait(false);
        await storage.AbortMultipartUploadAsync(
            profile,
            string.IsNullOrWhiteSpace(checkpoint.Bucket) ? task.Bucket : checkpoint.Bucket,
            string.IsNullOrWhiteSpace(checkpoint.ObjectKey) ? task.ObjectKey : checkpoint.ObjectKey,
            checkpoint.UploadId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectionProfile> ResolveProfileAsync(
        TransferTaskRecord task,
        CancellationToken cancellationToken)
    {
        var available = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        return available.FirstOrDefault(item => item.Id == task.ProfileId)
            ?? throw new InvalidOperationException($"找不到传输任务引用的连接：{task.ProfileName} ({task.ProfileId})");
    }
}
