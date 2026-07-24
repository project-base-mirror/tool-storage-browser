namespace S3Explorer.Core;

public sealed record TransferExecutionOptions
{
    public long MultipartThresholdBytes { get; init; } = 64L * 1024 * 1024;
    public long PartSizeBytes { get; init; } = 16L * 1024 * 1024;
    public int MultipartConcurrency { get; init; } = 4;
    public long UploadBytesPerSecond { get; init; }
    public long DownloadBytesPerSecond { get; init; }

    public void Validate()
    {
        if (MultipartThresholdBytes < 5L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MultipartThresholdBytes));
        if (PartSizeBytes < 5L * 1024 * 1024 || PartSizeBytes > 5L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(PartSizeBytes));
        if (MultipartConcurrency is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(MultipartConcurrency));
        if (UploadBytesPerSecond < 0 || DownloadBytesPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(UploadBytesPerSecond));
    }
}

public interface ITransferBandwidthLimiter
{
    void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond);
    ValueTask WaitAsync(TransferDirection direction, int bytes, CancellationToken cancellationToken);
}

public sealed class SharedTransferBandwidthLimiter : ITransferBandwidthLimiter
{
    private sealed class Lane
    {
        public readonly object Sync = new();
        public long Limit;
        public DateTimeOffset NextAvailable;
    }

    private readonly Lane _upload = new();
    private readonly Lane _download = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SharedTransferBandwidthLimiter(
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond)
    {
        ConfigureLane(_upload, uploadBytesPerSecond);
        ConfigureLane(_download, downloadBytesPerSecond);
    }

    public ValueTask WaitAsync(TransferDirection direction, int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return ValueTask.CompletedTask;
        var lane = direction == TransferDirection.Upload ? _upload : _download;
        TimeSpan wait;
        lock (lane.Sync)
        {
            if (lane.Limit <= 0) return ValueTask.CompletedTask;
            var now = _clock();
            var start = lane.NextAvailable > now ? lane.NextAvailable : now;
            wait = start - now;
            var seconds = bytes / (double)lane.Limit;
            lane.NextAvailable = start.AddSeconds(seconds);
        }
        return wait <= TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new ValueTask(_delay(wait, cancellationToken));
    }

    private void ConfigureLane(Lane lane, long limit)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (lane.Sync)
        {
            lane.Limit = limit;
            lane.NextAvailable = _clock();
        }
    }
}

public sealed record RemoteObjectIdentity(long Length, string? ETag, string? VersionId);

public sealed record DownloadResumeDecision(bool Resume, bool ResetTemporaryFile, long Offset, string Reason);

public static class DownloadResumePlanner
{
    public static DownloadResumeDecision Decide(
        bool temporaryFileExists,
        long temporaryLength,
        DownloadCheckpoint? checkpoint,
        RemoteObjectIdentity remote)
    {
        if (remote.Length < 0) throw new ArgumentOutOfRangeException(nameof(remote));
        if (!temporaryFileExists || temporaryLength == 0)
            return new(false, false, 0, "没有可恢复的临时文件。");
        if (temporaryLength < 0 || temporaryLength > remote.Length)
            return new(false, true, 0, "临时文件长度超出远端对象范围。");
        if (checkpoint is null)
            return new(false, true, 0, "缺少可验证的下载检查点。");
        if (checkpoint.CompletedBytes != temporaryLength || checkpoint.RemoteLength != remote.Length)
            return new(false, true, 0, "本地长度或远端长度已变化。");
        if (!SameIdentity(checkpoint.ETag, remote.ETag) || !SameIdentity(checkpoint.VersionId, remote.VersionId))
            return new(false, true, 0, "远端 ETag 或 VersionId 已变化。");
        return new(true, false, temporaryLength, "检查点与远端对象一致。");
    }

    private static bool SameIdentity(string? expected, string? actual) =>
        string.Equals(Normalize(expected), Normalize(actual), StringComparison.Ordinal);

    private static string Normalize(string? value) => value?.Trim().Trim('\"') ?? string.Empty;
}

public static class RetryBackoff
{
    public static TimeSpan Calculate(int baseDelaySeconds, int completedAttempts)
    {
        if (baseDelaySeconds <= 0) return TimeSpan.Zero;
        var exponent = Math.Clamp(completedAttempts - 1, 0, 10);
        var seconds = Math.Min(3600d, baseDelaySeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}

public sealed record TransferOperationContext(
    TransferExecutionOptions Options,
    ITransferBandwidthLimiter BandwidthLimiter,
    DownloadCheckpoint? DownloadCheckpoint,
    MultipartUploadCheckpoint? MultipartCheckpoint,
    Action<TransferProgress> ReportProgress,
    Func<long, DownloadCheckpoint?, MultipartUploadCheckpoint?, CancellationToken, Task> UpdateCheckpointAsync);

public static class ResumableDownloadFile
{
    public static string TemporaryPath(string localPath) => localPath + ".s3explorer.download";

    public static void Prepare(string temporaryPath, bool reset, long offset)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var directory = Path.GetDirectoryName(temporaryPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(temporaryPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (reset || stream.Length != offset) stream.SetLength(offset);
        stream.Flush(flushToDisk: true);
    }

    public static void Commit(string temporaryPath, string localPath, long expectedLength)
    {
        if (!File.Exists(temporaryPath))
            throw new FileNotFoundException("下载临时文件不存在。", temporaryPath);
        var actualLength = new FileInfo(temporaryPath).Length;
        if (actualLength != expectedLength)
            throw new IOException($"下载长度校验失败：预期 {expectedLength}，实际 {actualLength}。");
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.Move(temporaryPath, localPath, overwrite: true);
    }
}

public sealed class TransferExecutionException(TransferFailureInfo failure, Exception? innerException = null)
    : Exception(failure.SafeMessage, innerException)
{
    public TransferFailureInfo Failure { get; } = failure;
}
