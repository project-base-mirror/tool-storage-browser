using System.Security.Cryptography;
using System.Text;

namespace S3Explorer.Core;

public enum CdnUploadAction
{
    None,
    Warmup,
    Purge,
    PurgeThenWarmup
}

public sealed class CdnUploadAutomationCoordinator(PersistentCdnJobQueue queue)
{
    private readonly PersistentCdnJobQueue _queue = queue;

    public static bool RequiresDestinationSnapshot(
        CdnConfiguration configuration,
        Guid storageProfileId,
        string bucket,
        string objectKey)
    {
        return CdnUrlMapper.ResolveAll(configuration, storageProfileId, bucket, objectKey)
            .Any(target =>
                target.Binding.NewObjectAction != CdnUploadAction.None ||
                target.Binding.OverwriteAction != CdnUploadAction.None);
    }

    public async Task<IReadOnlyList<CdnJobRecord>> ProcessCompletedUploadsAsync(
        IEnumerable<TransferTaskRecord> tasks,
        CdnConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var jobs = new List<CdnJobRecord>();
        foreach (var task in tasks.OrderBy(value => value.CompletedAt ?? value.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            jobs.AddRange(await ProcessCompletedUploadAsync(task, configuration, cancellationToken)
                .ConfigureAwait(false));
        }
        return jobs;
    }

    public async Task<IReadOnlyList<CdnJobRecord>> ProcessCompletedUploadAsync(
        TransferTaskRecord task,
        CdnConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(configuration);

        if (task.Direction != TransferDirection.Upload ||
            task.State != TransferTaskState.Completed ||
            task.CompletedAt is not DateTimeOffset completedAt ||
            completedAt < _queue.Snapshot.AutomationStartedAt ||
            task.DestinationExistedBeforeTransfer is not bool destinationExisted ||
            task.ProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(task.Bucket) ||
            string.IsNullOrWhiteSpace(task.ObjectKey))
        {
            return [];
        }

        var jobs = new List<CdnJobRecord>();
        foreach (var target in CdnUrlMapper.ResolveAll(
                     configuration,
                     task.ProfileId,
                     task.Bucket,
                     task.ObjectKey))
        {
            var uploadAction = destinationExisted
                ? target.Binding.OverwriteAction
                : target.Binding.NewObjectAction;
            if (!TryMapAction(uploadAction, out var jobAction))
                continue;
            if (jobAction is CdnJobAction.PurgeUrl or CdnJobAction.PurgeThenWarmup &&
                !target.Profile.Capabilities.HasFlag(CdnCapabilities.Purge))
                continue;

            var job = await _queue.EnqueueAsync(new CdnJobRecord
            {
                IdempotencyKey = BuildIdempotencyKey(task, target, jobAction),
                CdnProfileId = target.Profile.Id,
                BindingId = target.Binding.Id,
                TransferTaskId = task.Id,
                Action = jobAction,
                Urls = [target.Url.AbsoluteUri],
                CreatedAt = completedAt
            }, cancellationToken).ConfigureAwait(false);
            jobs.Add(job);
        }
        return jobs;
    }

    private static bool TryMapAction(CdnUploadAction action, out CdnJobAction jobAction)
    {
        jobAction = action switch
        {
            CdnUploadAction.Warmup => CdnJobAction.Warmup,
            CdnUploadAction.Purge => CdnJobAction.PurgeUrl,
            CdnUploadAction.PurgeThenWarmup => CdnJobAction.PurgeThenWarmup,
            _ => default
        };
        return action != CdnUploadAction.None;
    }

    private static string BuildIdempotencyKey(
        TransferTaskRecord task,
        CdnResolvedTarget target,
        CdnJobAction action)
    {
        var material = $"{task.ProfileId:N}\n{task.Bucket}\n{task.ObjectKey}\n{target.Url.AbsoluteUri}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"upload:{task.Id:N}:{target.Binding.Id:N}:{action}:{hash}";
    }
}
