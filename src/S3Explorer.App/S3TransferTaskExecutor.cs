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

        if (task.Direction == TransferDirection.Upload)
        {
            if (!File.Exists(task.LocalPath))
                throw new FileNotFoundException("上传源文件不存在。", task.LocalPath);
            await storage.UploadFileAsync(
                profile, task.Bucket, task.ObjectKey, task.LocalPath, task.StorageClass, transfer, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var directory = Path.GetDirectoryName(task.LocalPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await storage.DownloadFileAsync(
            profile, task.Bucket, task.ObjectKey, task.LocalPath, transfer, cancellationToken)
            .ConfigureAwait(false);
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
