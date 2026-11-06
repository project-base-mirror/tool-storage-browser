using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, ObjectConflictPolicy?>? TransferConflictPolicySelector { get; set; }

    internal sealed record ResolvedTransferDestination(string Path, ObjectConflictPolicy Policy);

    private ObjectConflictPolicy DefaultTransferConflictPolicy =>
        _settings.ConfirmOverwrite ? ObjectConflictPolicy.Ask : ObjectConflictPolicy.Overwrite;

    private ObjectConflictPolicy ChooseTransferConflictPolicy(string destination, bool queued)
    {
        ObjectConflictPolicy? policy;
        if (TransferConflictPolicySelector is not null)
            policy = TransferConflictPolicySelector(destination);
        else
        {
            using var dialog = new TransferConflictDialog($"目标冲突：{destination}", ObjectConflictPolicy.Skip, queued);
            policy = dialog.ShowDialog(this) == DialogResult.OK ? dialog.Policy : null;
        }
        if (policy is null) throw new OperationCanceledException();
        return policy.Value;
    }

    private bool IsQueuedTransferDestination(string destination, bool download, ConnectionProfile? profile = null, string? bucket = null)
    {
        var candidate = new TransferTaskRecord
        {
            Direction = download ? TransferDirection.Download : TransferDirection.Upload,
            ProfileId = profile?.Id ?? _currentProfile?.Id ?? Guid.Empty,
            Bucket = bucket ?? _currentBucket ?? string.Empty,
            ObjectKey = download ? string.Empty : destination,
            LocalPath = download ? destination : string.Empty
        };
        return _transfers.Snapshot.Tasks.Any(task => TransferDestinationReservation.IsReserved(task) &&
            TransferDestinationReservation.Conflicts(candidate, task));
    }

    internal ResolvedTransferDestination? ResolveDownloadDestination(
        string localPath, ObjectConflictPolicy policy, HashSet<string> allocated)
    {
        localPath = LocalObjectPath.ToExtendedLengthPath(localPath);
        var mappedCollision = allocated.Contains(TransferDestinationReservation.NormalizeLocalPath(localPath));
        var queued = IsQueuedTransferDestination(localPath, download: true);
        var exists = File.Exists(localPath) || Directory.Exists(localPath);
        if (mappedCollision) policy = ObjectConflictPolicy.AutoRename;
        else if (queued)
        {
            if (policy == ObjectConflictPolicy.Skip) return null;
            if (policy is ObjectConflictPolicy.Ask or ObjectConflictPolicy.Overwrite)
                policy = ChooseTransferConflictPolicy(localPath, queued: true);
            if (policy == ObjectConflictPolicy.Skip) return null;
            if (policy != ObjectConflictPolicy.AutoRename)
                throw new InvalidOperationException($"下载目标已有未完成任务，不能覆盖：{localPath}");
        }
        if (!mappedCollision && !queued && exists)
        {
            if (policy == ObjectConflictPolicy.Ask)
                policy = ChooseTransferConflictPolicy(localPath, queued: false);
            if (policy == ObjectConflictPolicy.Skip) return null;
        }
        if ((exists || queued || mappedCollision) && policy == ObjectConflictPolicy.AutoRename)
        {
            var original = localPath;
            var found = false;
            for (var sequence = 2; sequence <= 10_000; sequence++)
            {
                var candidate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(original) ?? string.Empty,
                    ObjectTransferPlanner.GetAutoRenameCandidate(System.IO.Path.GetFileName(original), sequence));
                if (File.Exists(candidate) || Directory.Exists(candidate) || IsQueuedTransferDestination(candidate, true) ||
                    allocated.Contains(TransferDestinationReservation.NormalizeLocalPath(candidate))) continue;
                localPath = candidate;
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException("无法生成不冲突的下载路径。");
        }
        if (Directory.Exists(localPath)) throw new InvalidOperationException($"下载目标是目录：{localPath}");
        allocated.Add(TransferDestinationReservation.NormalizeLocalPath(localPath));
        return new(localPath, policy == ObjectConflictPolicy.Ask ? ObjectConflictPolicy.Skip : policy);
    }

    internal async Task<ResolvedTransferDestination?> ResolveUploadDestinationAsync(
        ConnectionProfile profile, string bucket, string key, ObjectConflictPolicy policy, HashSet<string> allocated)
    {
        var queued = allocated.Contains(key) || IsQueuedTransferDestination(key, download: false, profile, bucket);
        if (queued)
        {
            if (policy == ObjectConflictPolicy.Skip) return null;
            if (policy is ObjectConflictPolicy.Ask or ObjectConflictPolicy.Overwrite)
                policy = ChooseTransferConflictPolicy($"s3://{bucket}/{key}", queued: true);
            if (policy == ObjectConflictPolicy.Skip) return null;
            if (policy != ObjectConflictPolicy.AutoRename)
                throw new InvalidOperationException($"上传目标已有未完成任务，不能覆盖：{key}");
        }
        var exists = policy != ObjectConflictPolicy.Overwrite &&
            await _storage.ObjectExistsAsync(profile, bucket, key, CancellationToken.None);
        if (!queued && exists)
        {
            if (policy == ObjectConflictPolicy.Ask)
                policy = ChooseTransferConflictPolicy($"s3://{bucket}/{key}", queued: false);
            if (policy == ObjectConflictPolicy.Skip) return null;
        }
        if ((queued || exists) && policy == ObjectConflictPolicy.AutoRename)
        {
            var original = key;
            var found = false;
            for (var sequence = 2; sequence <= 10_000; sequence++)
            {
                var candidate = ObjectTransferPlanner.GetAutoRenameCandidate(original, sequence);
                if (allocated.Contains(candidate) || IsQueuedTransferDestination(candidate, false, profile, bucket) ||
                    await _storage.ObjectExistsAsync(profile, bucket, candidate, CancellationToken.None)) continue;
                key = candidate;
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException("无法生成不冲突的上传 Key。");
        }
        allocated.Add(key);
        return new(key, policy == ObjectConflictPolicy.Ask ? ObjectConflictPolicy.Skip : policy);
    }

}
