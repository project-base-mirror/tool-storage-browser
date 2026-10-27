namespace S3Explorer.Core;

/// <summary>Queue-local reservation; remote conditional writes and local atomic commit still guard external races.</summary>
public static class TransferDestinationReservation
{
    public static bool IsReserved(TransferTaskRecord task) =>
        task.State is not (TransferTaskState.Completed or TransferTaskState.Cancelled);

    public static TransferTaskRecord Reserve(TransferTaskRecord task, IEnumerable<TransferTaskRecord> existing)
    {
        var reservations = new ReservationIndex(existing.Where(item => item.Id != task.Id && (IsReserved(item) ||
            (task.BatchId is not null && item.BatchId == task.BatchId))));
        return Reserve(task, reservations);
    }

    public static void ReserveBatch(TransferTaskRecord[] tasks, IEnumerable<TransferTaskRecord> existing, Guid batchId)
    {
        // Build the destination index once per chunk, not once per item × all queued tasks.
        var reservations = new ReservationIndex(existing.Where(item => IsReserved(item) || item.BatchId == batchId));
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = Reserve(tasks[index], reservations);
            reservations.Add(tasks[index]);
        }
    }

    private static TransferTaskRecord Reserve(TransferTaskRecord task, ReservationIndex reservations)
    {
        if (!reservations.Contains(task)) return task;
        if (task.ConflictPolicy != ObjectConflictPolicy.AutoRename)
            throw new InvalidOperationException("目标路径已被其他传输任务占用；请选择自动重命名或等待并核对已有任务，不能覆盖正在传输的目标。");
        for (var sequence = 2; sequence <= 10_000; sequence++)
        {
            var candidate = Rename(task, sequence);
            if (!reservations.Contains(candidate) &&
                (candidate.Direction != TransferDirection.Download ||
                    (!File.Exists(candidate.LocalPath) && !Directory.Exists(candidate.LocalPath))))
                return candidate;
        }
        throw new InvalidOperationException("无法为传输目标生成不冲突的名称。");
    }

    public static bool Conflicts(TransferTaskRecord left, TransferTaskRecord right)
    {
        if (left.Id == right.Id) return false;
        if (left.Direction == TransferDirection.Download && right.Direction == TransferDirection.Download)
        {
            var leftPath = NormalizeLocalPath(left.LocalPath);
            var rightPath = NormalizeLocalPath(right.LocalPath);
            // Checkpoints may own a unique temporary name, not the legacy canonical suffix.
            var leftTemporary = TemporaryPath(left);
            var rightTemporary = TemporaryPath(right);
            return PathsOverlap(leftPath, rightPath) || PathsOverlap(leftTemporary, rightPath) ||
                PathsOverlap(leftPath, rightTemporary) || PathsOverlap(leftTemporary, rightTemporary);
        }
        var leftRemote = RemoteDestination(left);
        var rightRemote = RemoteDestination(right);
        return leftRemote is not null && rightRemote is not null && left.ProfileId == right.ProfileId &&
            leftRemote.Value == rightRemote.Value;
    }

    public static string NormalizeLocalPath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase)) full = "//" + full[8..];
        else if (full.StartsWith("//?/", StringComparison.Ordinal)) full = full[4..];
        return full.TrimEnd('/');
    }

    private static string TemporaryPath(TransferTaskRecord task) => NormalizeLocalPath(
        task.DownloadCheckpoint?.TemporaryPath ?? ResumableDownloadFile.TemporaryPath(task.LocalPath));

    private static bool PathsOverlap(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static (string Bucket, string Key)? RemoteDestination(TransferTaskRecord task) => task.Direction switch
    {
        TransferDirection.Upload => (task.Bucket, task.ObjectKey),
        TransferDirection.Copy or TransferDirection.Move => (task.DestinationBucket, task.DestinationObjectKey),
        _ => null
    };

    private sealed class ReservationIndex
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<(Guid Profile, string Bucket, string Key)> _remote = [];

        public ReservationIndex(IEnumerable<TransferTaskRecord> tasks)
        {
            foreach (var task in tasks) Add(task);
        }

        public void Add(TransferTaskRecord task)
        {
            if (task.Direction == TransferDirection.Download)
            {
                var path = NormalizeLocalPath(task.LocalPath);
                AddFile(path);
                AddFile(TemporaryPath(task));
            }
            else if (RemoteDestination(task) is { } remote)
                _remote.Add((task.ProfileId, remote.Bucket, remote.Key));
        }

        public bool Contains(TransferTaskRecord task)
        {
            if (task.Direction == TransferDirection.Download)
            {
                var path = NormalizeLocalPath(task.LocalPath);
                return ContainsFile(path) || ContainsFile(TemporaryPath(task));
            }
            return RemoteDestination(task) is { } remote && _remote.Contains((task.ProfileId, remote.Bucket, remote.Key));
        }

        private void AddFile(string path)
        {
            _files.Add(path);
            foreach (var parent in Parents(path)) _directories.Add(parent);
        }

        private bool ContainsFile(string path) => _files.Contains(path) || _directories.Contains(path) ||
            Parents(path).Any(parent => _files.Contains(parent));

        private static IEnumerable<string> Parents(string path)
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                yield return path[..slash];
        }
    }

    private static TransferTaskRecord Rename(TransferTaskRecord task, int sequence) => task.Direction switch
    {
        TransferDirection.Download => task with
        {
            LocalPath = Path.Combine(Path.GetDirectoryName(task.LocalPath) ?? string.Empty,
                ObjectTransferPlanner.GetAutoRenameCandidate(Path.GetFileName(task.LocalPath), sequence))
        },
        TransferDirection.Upload => task with { ObjectKey = ObjectTransferPlanner.GetAutoRenameCandidate(task.ObjectKey, sequence) },
        TransferDirection.Copy or TransferDirection.Move => task with
        {
            DestinationObjectKey = ObjectTransferPlanner.GetAutoRenameCandidate(task.DestinationObjectKey, sequence)
        },
        _ => throw new InvalidOperationException("此任务没有可重命名的传输目标。")
    };
}
