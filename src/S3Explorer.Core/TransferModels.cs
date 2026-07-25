namespace S3Explorer.Core;

public enum TransferDirection
{
    Upload,
    Download,
    Copy,
    Move
}

public enum TransferTaskKind
{
    File,
    FolderBatchItem,
    MultipartCopy,
    ObjectTransfer
}

public enum TransferTaskState
{
    Queued,
    Running,
    Paused,
    RetryPending,
    Interrupted,
    Completed,
    Failed,
    Cancelled,
    CleanupPending
}

public enum TransferFailureCategory
{
    None,
    Network,
    Timeout,
    Authentication,
    Authorization,
    NotFound,
    Conflict,
    LocalIo,
    Validation,
    Service,
    Cancelled,
    Unknown
}

public sealed record TransferFailureInfo(
    string Message,
    TransferFailureCategory Category = TransferFailureCategory.Unknown,
    int? HttpStatusCode = null,
    string? ServiceCode = null,
    string? RequestId = null,
    bool Retryable = false)
{
    public string SafeMessage => SensitiveDataRedactor.Redact(Message);
}

public sealed record DownloadCheckpoint(
    string TemporaryPath,
    long CompletedBytes,
    long RemoteLength,
    string? ETag = null,
    string? VersionId = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TemporaryPath))
            throw new ArgumentException("临时下载路径不能为空。", nameof(TemporaryPath));
        if (CompletedBytes < 0 || RemoteLength < 0 || CompletedBytes > RemoteLength)
            throw new ArgumentOutOfRangeException(nameof(CompletedBytes), "下载检查点字节范围无效。");
    }
}

public sealed record MultipartPartCheckpoint(int PartNumber, string ETag, long Size)
{
    public void Validate()
    {
        if (PartNumber is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(PartNumber));
        if (string.IsNullOrWhiteSpace(ETag))
            throw new ArgumentException("分片 ETag 不能为空。", nameof(ETag));
        if (Size <= 0)
            throw new ArgumentOutOfRangeException(nameof(Size));
    }
}

public sealed record MultipartUploadCheckpoint(
    string UploadId,
    long PartSize,
    IReadOnlyList<MultipartPartCheckpoint> CompletedParts,
    bool CleanupPending = false,
    string Bucket = "",
    string ObjectKey = "",
    long SourceLength = 0,
    DateTimeOffset SourceLastWriteTimeUtc = default,
    DateTimeOffset InitiatedAt = default)
{
    public bool HasSourceIdentity =>
        !string.IsNullOrWhiteSpace(Bucket) &&
        !string.IsNullOrWhiteSpace(ObjectKey) &&
        SourceLength >= 0 &&
        SourceLastWriteTimeUtc != default;

    public bool Matches(string bucket, string objectKey, long sourceLength, DateTimeOffset sourceLastWriteTimeUtc, long partSize) =>
        string.Equals(Bucket, bucket, StringComparison.Ordinal) &&
        string.Equals(ObjectKey, objectKey, StringComparison.Ordinal) &&
        SourceLength == sourceLength &&
        SourceLastWriteTimeUtc == sourceLastWriteTimeUtc &&
        PartSize == partSize &&
        !CleanupPending;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UploadId))
            throw new ArgumentException("UploadId 不能为空。", nameof(UploadId));
        if (PartSize < 5L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(PartSize), "分片大小不能小于 5 MiB。");
        if (SourceLength < 0)
            throw new ArgumentOutOfRangeException(nameof(SourceLength));
        if (string.IsNullOrWhiteSpace(Bucket) != string.IsNullOrWhiteSpace(ObjectKey))
            throw new ArgumentException("Bucket 和 ObjectKey 必须同时提供。");

        var seen = new HashSet<int>();
        foreach (var part in CompletedParts ?? Array.Empty<MultipartPartCheckpoint>())
        {
            part.Validate();
            if (!seen.Add(part.PartNumber))
                throw new ArgumentException($"分片编号重复：{part.PartNumber}", nameof(CompletedParts));
        }
    }
}

public sealed record TransferTaskRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? BatchId { get; init; }
    public Guid ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public TransferTaskKind Kind { get; init; } = TransferTaskKind.File;
    public TransferTaskState State { get; init; } = TransferTaskState.Queued;
    public string Bucket { get; init; } = string.Empty;
    public string ObjectKey { get; init; } = string.Empty;
    public string DestinationBucket { get; init; } = string.Empty;
    public string DestinationObjectKey { get; init; } = string.Empty;
    public ObjectConflictPolicy ConflictPolicy { get; init; } = ObjectConflictPolicy.Overwrite;
    public string LocalPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string StorageClass { get; init; } = "STANDARD";
    public long TotalBytes { get; init; }
    public long TransferredBytes { get; init; }
    public int AttemptCount { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public int RetryBaseDelaySeconds { get; init; } = 2;
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public TransferFailureInfo? Failure { get; init; }
    public DownloadCheckpoint? DownloadCheckpoint { get; init; }
    public MultipartUploadCheckpoint? MultipartCheckpoint { get; init; }

    public bool IsTerminal => State is TransferTaskState.Completed or TransferTaskState.Cancelled;
    public bool CanResume => State is TransferTaskState.Paused or TransferTaskState.Interrupted or TransferTaskState.Failed or TransferTaskState.RetryPending;

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("任务 ID 不能为空。", nameof(Id));
        if (ProfileId == Guid.Empty) throw new ArgumentException("连接 ID 不能为空。", nameof(ProfileId));
        if (string.IsNullOrWhiteSpace(ProfileName)) throw new ArgumentException("连接名称不能为空。", nameof(ProfileName));
        if (string.IsNullOrWhiteSpace(Bucket)) throw new ArgumentException("Bucket 不能为空。", nameof(Bucket));
        if (string.IsNullOrWhiteSpace(ObjectKey)) throw new ArgumentException("对象 Key 不能为空。", nameof(ObjectKey));
        if (Direction is TransferDirection.Upload or TransferDirection.Download)
        {
            if (string.IsNullOrWhiteSpace(LocalPath))
                throw new ArgumentException("上传或下载任务的本地路径不能为空。", nameof(LocalPath));
        }
        else if (Direction is TransferDirection.Copy or TransferDirection.Move)
        {
            if (string.IsNullOrWhiteSpace(DestinationBucket))
                throw new ArgumentException("目标 Bucket 不能为空。", nameof(DestinationBucket));
            if (string.IsNullOrWhiteSpace(DestinationObjectKey))
                throw new ArgumentException("目标对象 Key 不能为空。", nameof(DestinationObjectKey));
            ObjectTransferPlanner.ValidateDestination(
                Bucket, ObjectKey, isDirectory: false, DestinationBucket, DestinationObjectKey);
        }
        if (TotalBytes < 0 || TransferredBytes < 0 || TransferredBytes > TotalBytes)
            throw new ArgumentOutOfRangeException(nameof(TransferredBytes));
        if (AttemptCount < 0 || MaxAttempts < 1 || AttemptCount > MaxAttempts)
            throw new ArgumentOutOfRangeException(nameof(AttemptCount));
        if (RetryBaseDelaySeconds is < 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelaySeconds));
        DownloadCheckpoint?.Validate();
        MultipartCheckpoint?.Validate();
    }
}

public sealed record TransferBatchRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public TransferDirection Direction { get; init; }
    public IReadOnlyList<Guid> TaskIds { get; init; } = Array.Empty<Guid>();
    public bool DiscoveryCompleted { get; init; }
    public int SkippedCount { get; init; }
    public bool CancellationRequested { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("批次 ID 不能为空。", nameof(Id));
        if (ProfileId == Guid.Empty) throw new ArgumentException("连接 ID 不能为空。", nameof(ProfileId));
        if (string.IsNullOrWhiteSpace(ProfileName)) throw new ArgumentException("连接名称不能为空。", nameof(ProfileName));
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("批次名称不能为空。", nameof(Name));
        if (string.IsNullOrWhiteSpace(Bucket)) throw new ArgumentException("Bucket 不能为空。", nameof(Bucket));
        if (string.IsNullOrWhiteSpace(RootPath)) throw new ArgumentException("批次根路径不能为空。", nameof(RootPath));
        if (SkippedCount < 0) throw new ArgumentOutOfRangeException(nameof(SkippedCount));
        if (TaskIds.Any(id => id == Guid.Empty) || TaskIds.Distinct().Count() != TaskIds.Count)
            throw new ArgumentException("批次任务 ID 必须有效且唯一。", nameof(TaskIds));
    }
}

public sealed record TransferStoreSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<TransferTaskRecord> Tasks { get; init; } = Array.Empty<TransferTaskRecord>();
    public IReadOnlyList<TransferBatchRecord> Batches { get; init; } = Array.Empty<TransferBatchRecord>();

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidOperationException($"不支持的任务存储版本：{SchemaVersion}");
        foreach (var task in Tasks) task.Validate();
        foreach (var batch in Batches) batch.Validate();
        if (Tasks.Select(task => task.Id).Distinct().Count() != Tasks.Count)
            throw new InvalidOperationException("任务 ID 重复。");
        if (Batches.Select(batch => batch.Id).Distinct().Count() != Batches.Count)
            throw new InvalidOperationException("批次 ID 重复。");

        var batchIds = Batches.Select(batch => batch.Id).ToHashSet();
        foreach (var task in Tasks.Where(task => task.BatchId is not null))
        {
            if (!batchIds.Contains(task.BatchId!.Value))
                throw new InvalidOperationException($"任务引用了不存在的批次：{task.BatchId}");
            if (task.Kind is not (TransferTaskKind.FolderBatchItem or TransferTaskKind.ObjectTransfer))
                throw new InvalidOperationException("批次子任务必须标记为 FolderBatchItem 或 ObjectTransfer。");
        }

        var taskIds = Tasks.Select(task => task.Id).ToHashSet();
        foreach (var batch in Batches)
        {
            if (batch.TaskIds.Any(id => !taskIds.Contains(id)))
                throw new InvalidOperationException($"批次 {batch.Id} 引用了不存在的任务。");
        }
    }
}

public interface ITransferTaskStore
{
    Task<TransferStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TransferStoreSnapshot snapshot, CancellationToken cancellationToken = default);
}

public static class TransferTaskStateMachine
{
    private static readonly IReadOnlyDictionary<TransferTaskState, HashSet<TransferTaskState>> Allowed =
        new Dictionary<TransferTaskState, HashSet<TransferTaskState>>
        {
            [TransferTaskState.Queued] = [TransferTaskState.Running, TransferTaskState.Paused, TransferTaskState.Cancelled],
            [TransferTaskState.Running] = [TransferTaskState.Paused, TransferTaskState.RetryPending, TransferTaskState.Completed, TransferTaskState.Failed, TransferTaskState.Cancelled, TransferTaskState.Interrupted, TransferTaskState.CleanupPending],
            [TransferTaskState.Paused] = [TransferTaskState.Queued, TransferTaskState.Cancelled],
            [TransferTaskState.RetryPending] = [TransferTaskState.Queued, TransferTaskState.Running, TransferTaskState.Paused, TransferTaskState.Cancelled],
            [TransferTaskState.Interrupted] = [TransferTaskState.Queued, TransferTaskState.Paused, TransferTaskState.Cancelled, TransferTaskState.CleanupPending],
            [TransferTaskState.Failed] = [TransferTaskState.RetryPending, TransferTaskState.Queued, TransferTaskState.Cancelled, TransferTaskState.CleanupPending],
            [TransferTaskState.CleanupPending] = [TransferTaskState.Queued, TransferTaskState.Failed, TransferTaskState.Cancelled],
            [TransferTaskState.Completed] = [],
            [TransferTaskState.Cancelled] = []
        };

    public static bool CanTransition(TransferTaskState from, TransferTaskState to) =>
        from == to || Allowed[from].Contains(to);

    public static TransferTaskRecord Transition(
        TransferTaskRecord task,
        TransferTaskState target,
        DateTimeOffset now,
        TransferFailureInfo? failure = null)
    {
        if (task.State == target) return task;
        if (!CanTransition(task.State, target))
            throw new InvalidOperationException($"非法传输状态转换：{task.State} -> {target}");

        var updated = task with
        {
            State = target,
            UpdatedAt = now,
            Failure = target is TransferTaskState.Queued or TransferTaskState.Running or TransferTaskState.Completed
                ? null
                : failure ?? task.Failure
        };

        if (target == TransferTaskState.Running)
            updated = updated with
            {
                StartedAt = task.StartedAt ?? now,
                AttemptCount = Math.Min(task.MaxAttempts, task.AttemptCount + 1),
                CompletedAt = null,
                NextAttemptAt = null
            };
        if (target is TransferTaskState.Queued or TransferTaskState.Paused)
            updated = updated with { NextAttemptAt = null };
        if (target == TransferTaskState.Completed)
            updated = updated with
            {
                CompletedAt = now,
                TransferredBytes = task.TotalBytes,
                NextAttemptAt = null,
                DownloadCheckpoint = null,
                MultipartCheckpoint = null
            };
        if (target == TransferTaskState.Cancelled)
            updated = updated with { CompletedAt = now };
        return updated;
    }

    public static TransferStoreSnapshot RecoverInterrupted(TransferStoreSnapshot snapshot, DateTimeOffset now)
    {
        snapshot.Validate();
        return snapshot with
        {
            Tasks = snapshot.Tasks.Select(task => task.State == TransferTaskState.Running
                ? Transition(task, TransferTaskState.Interrupted, now)
                : task).ToArray()
        };
    }
}
