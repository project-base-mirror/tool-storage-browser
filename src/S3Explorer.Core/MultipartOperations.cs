namespace S3Explorer.Core;

public sealed record MultipartPartDescriptor(int PartNumber, long Offset, long Size);

public sealed record MultipartUploadReconciliation(
    IReadOnlyList<MultipartPartCheckpoint> ConfirmedParts,
    IReadOnlyList<MultipartPartDescriptor> MissingParts)
{
    public long ConfirmedBytes => ConfirmedParts.Sum(part => part.Size);
}

public sealed record IncompleteMultipartUpload(
    string Bucket,
    string ObjectKey,
    string UploadId,
    DateTimeOffset InitiatedAt,
    long KnownUploadedBytes,
    int PartCount);

public sealed record MultipartCleanupResult(
    int RequestedCount,
    int CleanedCount,
    IReadOnlyList<IncompleteMultipartUpload> FailedUploads)
{
    public bool Succeeded => FailedUploads.Count == 0;
}

public static class MultipartUploadPlanner
{
    public static IReadOnlyList<MultipartPartDescriptor> BuildParts(long sourceLength, long partSize)
    {
        if (sourceLength <= 0) throw new ArgumentOutOfRangeException(nameof(sourceLength));
        if (partSize < 5L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(partSize));

        var count = checked((int)((sourceLength + partSize - 1) / partSize));
        if (count > 10_000)
            throw new InvalidOperationException($"分片数量 {count:N0} 超过 S3 上限 10,000。");

        var parts = new MultipartPartDescriptor[count];
        for (var index = 0; index < count; index++)
        {
            var offset = index * partSize;
            parts[index] = new MultipartPartDescriptor(index + 1, offset, Math.Min(partSize, sourceLength - offset));
        }
        return parts;
    }

    public static MultipartUploadReconciliation Reconcile(
        long sourceLength,
        long partSize,
        IEnumerable<MultipartPartCheckpoint> remoteParts)
    {
        var planned = BuildParts(sourceLength, partSize);
        var byNumber = remoteParts
            .GroupBy(part => part.PartNumber)
            .ToDictionary(group => group.Key, group => group.Last());
        var confirmed = new List<MultipartPartCheckpoint>();
        var missing = new List<MultipartPartDescriptor>();

        foreach (var part in planned)
        {
            if (byNumber.TryGetValue(part.PartNumber, out var remote) &&
                remote.Size == part.Size &&
                !string.IsNullOrWhiteSpace(remote.ETag))
            {
                confirmed.Add(remote);
            }
            else
            {
                missing.Add(part);
            }
        }
        return new MultipartUploadReconciliation(confirmed, missing);
    }

    public static IReadOnlyList<IncompleteMultipartUpload> Filter(
        IEnumerable<IncompleteMultipartUpload> uploads,
        string? keyContains,
        DateTimeOffset? initiatedBefore)
    {
        var query = uploads;
        if (!string.IsNullOrWhiteSpace(keyContains))
            query = query.Where(upload => upload.ObjectKey.Contains(keyContains.Trim(), StringComparison.OrdinalIgnoreCase));
        if (initiatedBefore is not null)
            query = query.Where(upload => upload.InitiatedAt <= initiatedBefore.Value);
        return query.OrderBy(upload => upload.InitiatedAt).ThenBy(upload => upload.ObjectKey, StringComparer.Ordinal).ToArray();
    }
}
