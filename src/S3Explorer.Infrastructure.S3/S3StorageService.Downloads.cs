using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    private sealed class ParallelRangeNotSupportedException()
        : IOException("服务端不支持分段下载，改用单流下载。");

    internal static async Task DownloadParallelAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        string localPath,
        RemoteObjectIdentity remote,
        long segmentSize,
        TransferOperationContext context,
        CancellationToken cancellationToken)
    {
        if (segmentSize <= 0) throw new ArgumentOutOfRangeException(nameof(segmentSize));
        var segmentCount = checked((int)(remote.Length / segmentSize + (remote.Length % segmentSize == 0 ? 0 : 1)));
        var temporaryPath = ResumableDownloadFile.TemporaryPath(localPath);
        var existing = context.DownloadCheckpoint;
        var exists = File.Exists(temporaryPath);
        var length = exists ? new FileInfo(temporaryPath).Length : 0;
        var identityMatches = existing is not null && existing.RemoteLength == remote.Length &&
                              SameIdentity(existing.ETag, remote.ETag) && SameIdentity(existing.VersionId, remote.VersionId);
        var completed = new HashSet<int>();
        if (identityMatches && existing!.SegmentSizeBytes == segmentSize && length == remote.Length)
        {
            existing.Validate();
            completed.UnionWith(existing.CompletedSegments);
        }
        else if (identityMatches && existing!.SegmentSizeBytes == 0 && length >= existing.CompletedBytes && length <= remote.Length)
        {
            // Preserve complete segments from a legacy contiguous checkpoint, not its unconfirmed tail.
            for (var index = 0; index < existing.CompletedBytes / segmentSize; index++)
                completed.Add(index);
        }

        long confirmedBytes = completed.Sum(index => Math.Min(segmentSize, remote.Length - index * segmentSize));
        long transferredBytes = confirmedBytes;
        var checkpoint = new DownloadCheckpoint(temporaryPath, confirmedBytes, remote.Length, remote.ETag, remote.VersionId)
        {
            SegmentSizeBytes = segmentSize,
            CompletedSegments = completed.Order().ToArray()
        };
        await context.UpdateCheckpointAsync(confirmedBytes, checkpoint, context.MultipartCheckpoint, cancellationToken)
            .ConfigureAwait(false);
        // Forget any old segment claims before resetting/preallocating a sparse file. A crash
        // between file creation and the checkpoint must never revive an old completed bitmap.
        var directory = Path.GetDirectoryName(temporaryPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var handle = File.OpenHandle(temporaryPath,
            completed.Count == 0 ? FileMode.Create : FileMode.Open,
            FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        RandomAccess.SetLength(handle, remote.Length);
        RandomAccess.FlushToDisk(handle);
        context.ReportProgress(new TransferProgress(confirmedBytes, remote.Length));
        using var checkpointGate = new SemaphoreSlim(1, 1);
        var missing = Enumerable.Range(0, segmentCount).Where(index => !completed.Contains(index)).ToArray();
        await Parallel.ForEachAsync(missing, new ParallelOptions
        {
            MaxDegreeOfParallelism = context.Options.MultipartConcurrency,
            CancellationToken = cancellationToken
        }, async (index, token) =>
        {
            var first = index * segmentSize;
            var size = Math.Min(segmentSize, remote.Length - first);
            var last = first + size - 1;
            using var requestLease = await context.RequestBudget.EnterAsync(token).ConfigureAwait(false);
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = remote.VersionId,
                EtagToMatch = remote.ETag,
                ByteRange = new ByteRange(first, last)
            }, token).ConfigureAwait(false);
            ValidateRangeResponse(response, first, last, remote);
            var buffer = new byte[128 * 1024];
            long received = 0;
            while (received < size)
            {
                var read = await response.ResponseStream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - received)), token).ConfigureAwait(false);
                if (read == 0) throw new IOException("分段下载响应提前结束。");
                await context.BandwidthLimiter.WaitAsync(TransferDirection.Download, read, token).ConfigureAwait(false);
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), first + received, token).ConfigureAwait(false);
                received += read;
                var total = Interlocked.Add(ref transferredBytes, read);
                context.ReportProgress(new TransferProgress(total, remote.Length));
            }
            if (await response.ResponseStream.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("分段下载响应超过请求范围。");

            response.Dispose();
            requestLease.Dispose();
            await checkpointGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Flush completed data before publishing its bit. Other in-flight ranges may
                // also be flushed, but remain unconfirmed and will be downloaded again after a crash.
                RandomAccess.FlushToDisk(handle);
                completed.Add(index);
                confirmedBytes += size;
                checkpoint = checkpoint with
                {
                    CompletedBytes = confirmedBytes,
                    CompletedSegments = completed.Order().ToArray()
                };
                await context.UpdateCheckpointAsync(confirmedBytes, checkpoint, context.MultipartCheckpoint, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                checkpointGate.Release();
            }
        }).ConfigureAwait(false);

        if (confirmedBytes != remote.Length || completed.Count != segmentCount)
            throw new InvalidDataException("下载分段尚未全部完成。");
        handle.Dispose();
        await VerifyFullObjectChecksumAsync(temporaryPath, remote, cancellationToken).ConfigureAwait(false);
        ResumableDownloadFile.Commit(temporaryPath, localPath, remote.Length);
        context.ReportProgress(new TransferProgress(remote.Length, remote.Length));
    }

    private static async Task VerifyFullObjectChecksumAsync(
        string temporaryPath, RemoteObjectIdentity remote, CancellationToken cancellationToken)
    {
        // Only compare explicitly full-object hashes. Multipart composite hashes and ETags
        // cannot be compared with a plain whole-file SHA-256.
        if (string.IsNullOrWhiteSpace(remote.FullObjectSha256)) return;
        await using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToBase64String(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, remote.FullObjectSha256, StringComparison.Ordinal))
            throw new TransferExecutionException(new TransferFailureInfo(
                "下载文件 SHA-256 与服务端完整对象校验值不符。", TransferFailureCategory.Validation, Retryable: false));
    }

    private static void ValidateRangeResponse(GetObjectResponse response, long first, long last, RemoteObjectIdentity remote)
    {
        if (response.HttpStatusCode == HttpStatusCode.OK)
            throw new ParallelRangeNotSupportedException();
        var expectedRange = string.Create(CultureInfo.InvariantCulture, $"bytes {first}-{last}/{remote.Length}");
        if (response.HttpStatusCode != HttpStatusCode.PartialContent ||
            !string.Equals(response.ContentRange, expectedRange, StringComparison.OrdinalIgnoreCase) ||
            response.ContentLength != last - first + 1)
            throw new InvalidDataException("分段下载响应范围或长度与请求不符。");
        if (!SameIdentity(response.ETag, remote.ETag) || !SameIdentity(response.VersionId, remote.VersionId))
            throw new TransferExecutionException(new TransferFailureInfo(
                "下载期间远端对象身份发生变化。", TransferFailureCategory.Conflict, Retryable: true));
    }
}
