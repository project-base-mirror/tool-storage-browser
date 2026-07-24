using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class S3TransferTaskExecutor(IProfileStore profiles, IS3StorageService storage) : ITransferTaskExecutor
{
    public async Task ExecuteAsync(TransferTaskRecord task, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var available = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = available.FirstOrDefault(item => item.Id == task.ProfileId)
            ?? throw new InvalidOperationException($"找不到传输任务引用的连接：{task.ProfileName} ({task.ProfileId})");

        if (task.Direction == TransferDirection.Upload)
        {
            if (!File.Exists(task.LocalPath))
                throw new FileNotFoundException("上传源文件不存在。", task.LocalPath);
            await storage.UploadFileAsync(profile, task.Bucket, task.ObjectKey, task.LocalPath, task.StorageClass, progress, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var directory = Path.GetDirectoryName(task.LocalPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await storage.DownloadFileAsync(profile, task.Bucket, task.ObjectKey, task.LocalPath, progress, cancellationToken)
            .ConfigureAwait(false);
    }
}
