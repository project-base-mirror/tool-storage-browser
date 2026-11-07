using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    // Buffer only an individual request, never stage an entire large object. Small requests
    // stay in memory; larger configured parts use a seekable, automatically deleted file.
    // Seekability is required for SDK retries and R2's Content-MD5/unsigned-payload mode.
    private const long CrossConnectionMemoryBufferBytes = 16L * 1024 * 1024;

    public async Task CopyObjectAcrossConnectionsAsync(
        ConnectionProfile sourceProfile, string sourceBucket, string sourceKey,
        ConnectionProfile destinationProfile, string destinationBucket, string destinationKey,
        TransferOperationContext context, CancellationToken cancellationToken)
    {
        using var sourceClient = _factory.Create(sourceProfile);
        using var destinationClient = _factory.Create(destinationProfile);
        await CopyObjectAcrossConnectionsAsync(sourceClient, sourceProfile, sourceBucket, sourceKey,
            destinationClient, destinationProfile, destinationBucket, destinationKey, context, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task CopyObjectAcrossConnectionsAsync(
        IAmazonS3 sourceClient, ConnectionProfile sourceProfile, string sourceBucket, string sourceKey,
        IAmazonS3 destinationClient, ConnectionProfile destinationProfile, string destinationBucket, string destinationKey,
        TransferOperationContext context, CancellationToken cancellationToken)
    {
        try
        {
            context.Options.Validate();
            MinioObjectKeyPolicy.Validate(sourceProfile.ServiceType, sourceKey);
            MinioObjectKeyPolicy.Validate(destinationProfile.ServiceType, destinationKey);
            S3UploadCompatibility.ValidateEndpoint(destinationProfile);
            if (sourceBucket == destinationBucket && sourceKey == destinationKey &&
                SameCrossConnectionStorage(sourceProfile, destinationProfile))
                throw CrossConnectionConflict("来源与目标指向同一对象，已停止复制。");

            var saved = context.MultipartCheckpoint;
            if (saved is not null &&
                (saved.CleanupPending || saved.CopySourceProfileId != sourceProfile.Id ||
                 saved.CopyDestinationProfileId != destinationProfile.Id || saved.Bucket != destinationBucket ||
                 saved.ObjectKey != destinationKey || saved.CopySourceBucket != sourceBucket || saved.CopySourceKey != sourceKey))
                throw CrossConnectionConflict("跨连接复制断点与当前连接或对象不一致，请取消旧任务清理分片后重新复制。");

            GetObjectMetadataResponse source;
            using (await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false))
                source = await sourceClient.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = sourceBucket, Key = sourceKey, VersionId = saved?.CopySourceVersionId
                }, cancellationToken).ConfigureAwait(false);
            if (source.ContentLength < 0 || string.IsNullOrWhiteSpace(source.ETag))
                throw CrossConnectionConflict("源对象缺少可验证的长度或 ETag，已停止复制。");
            if (context.Options.MaximumDownloadBytes > 0 && source.ContentLength > context.Options.MaximumDownloadBytes)
                throw new TransferExecutionException(new TransferFailureInfo(
                    "源对象超过本次传输的读取大小上限，未写入目标。", TransferFailureCategory.Validation));
            if (source.MissingMeta.GetValueOrDefault() > 0)
                throw new TransferExecutionException(new TransferFailureInfo(
                    "源对象有未能读取的 Metadata，无法完整保留属性，未写入目标。", TransferFailureCategory.Validation));
            if (saved is not null &&
                (saved.CopySourceETag != source.ETag || saved.CopySourceVersionId != source.VersionId ||
                 saved.SourceLength != source.ContentLength))
                throw CrossConnectionConflict("跨连接复制来源已变化，请取消旧任务清理分片后重新复制。");

            if (saved?.CopyCompleted == true)
            {
                await VerifyCrossConnectionDestinationAsync(destinationClient, destinationBucket, destinationKey,
                    saved, context, cancellationToken).ConfigureAwait(false);
                context.ReportProgress(new TransferProgress(source.ContentLength, source.ContentLength));
                return;
            }

            var multipart = saved is not null || source.ContentLength > CrossConnectionMemoryBufferBytes ||
                !UseSingleUpload(source.ContentLength, context.Options.MultipartThresholdBytes);
            var conditional = SupportsUploadConditions(destinationProfile, multipart);
            if (!conditional && context.Options.ExpectedDestinationETag is not null)
                throw new TransferExecutionException(new TransferFailureInfo(
                    "目标服务商/上传方式不支持按指定 ETag 条件替换，未写入目标。", TransferFailureCategory.Validation));
            if (context.Options.RequireSseS3Encryption &&
                destinationProfile.ServiceType is not (S3ServiceType.AmazonS3 or S3ServiceType.MinIO))
                throw new TransferExecutionException(new TransferFailureInfo(
                    "目标服务商尚无可靠的显式 AES256 上传路径，未写入目标。", TransferFailureCategory.Validation));
            if (!conditional && !context.Options.AllowDestinationOverwrite)
                await CheckUploadDestinationAsync(destinationClient, destinationBucket, destinationKey, context, cancellationToken)
                    .ConfigureAwait(false);

            if (multipart)
                await CopyAcrossConnectionsMultipartAsync(sourceClient, sourceProfile, sourceBucket, sourceKey,
                    destinationClient, destinationProfile, destinationBucket, destinationKey, source, context,
                    conditional, cancellationToken).ConfigureAwait(false);
            else
                await CopyAcrossConnectionsSingleAsync(sourceClient, sourceProfile, sourceBucket, sourceKey,
                    destinationClient, destinationProfile, destinationBucket, destinationKey, source, context,
                    conditional, cancellationToken).ConfigureAwait(false);
            context.ReportProgress(new TransferProgress(source.ContentLength, source.ContentLength));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToTransferException(exception);
        }
    }

    private static async Task CopyAcrossConnectionsSingleAsync(
        IAmazonS3 sourceClient, ConnectionProfile sourceProfile, string sourceBucket, string sourceKey,
        IAmazonS3 destinationClient, ConnectionProfile destinationProfile, string destinationBucket, string destinationKey,
        GetObjectMetadataResponse source, TransferOperationContext context, bool conditional,
        CancellationToken cancellationToken)
    {
        var tags = await ReadCrossConnectionTagsAsync(sourceClient, sourceProfile, sourceBucket, sourceKey,
            destinationProfile, source.VersionId, context, cancellationToken).ConfigureAwait(false);
        await using var buffer = CreateCrossConnectionBuffer(source.ContentLength);
        await BufferCrossConnectionRangeAsync(sourceClient, sourceBucket, sourceKey, source,
            0, source.ContentLength, buffer, context, cancellationToken).ConfigureAwait(false);
        // A long GET must not make the fallback destination check stale before the PUT.
        if (!conditional && !context.Options.AllowDestinationOverwrite)
            await CheckUploadDestinationAsync(destinationClient, destinationBucket, destinationKey, context, cancellationToken)
                .ConfigureAwait(false);
        long reported = 0;
        await using var throttled = new ThrottledReadStream(buffer, context.BandwidthLimiter,
            TransferDirection.Upload, _ =>
            {
                // SDK retries may seek the buffer. Report a high-water mark, not duplicate bytes.
                var current = buffer.Position;
                if (current <= reported) return;
                reported = current;
                context.ReportProgress(new TransferProgress(current, source.ContentLength));
            }, leaveOpen: true);
        var request = new PutObjectRequest
        {
            BucketName = destinationBucket, Key = destinationKey, InputStream = throttled, AutoCloseStream = false,
            IfNoneMatch = conditional && !context.Options.AllowDestinationOverwrite ? "*" : null,
            IfMatch = context.Options.ExpectedDestinationETag,
            ServerSideEncryptionMethod = RequiredUploadEncryption(context.Options),
            WebsiteRedirectLocation = source.WebsiteRedirectLocation, TagSet = tags
        };
        ApplyCrossConnectionHeaders(source, request.Headers, request.Metadata);
        await S3UploadCompatibility.PrepareAsync(destinationProfile, request, buffer, cancellationToken).ConfigureAwait(false);
        using var lease = await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false);
        await destinationClient.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyAcrossConnectionsMultipartAsync(
        IAmazonS3 sourceClient, ConnectionProfile sourceProfile, string sourceBucket, string sourceKey,
        IAmazonS3 destinationClient, ConnectionProfile destinationProfile, string destinationBucket, string destinationKey,
        GetObjectMetadataResponse source, TransferOperationContext context, bool conditional,
        CancellationToken cancellationToken)
    {
        var checkpoint = context.MultipartCheckpoint;
        var partSize = checkpoint?.PartSize ?? MultipartUploadPlanner.EffectivePartSize(source.ContentLength, context.Options.PartSizeBytes);
        _ = MultipartUploadPlanner.BuildParts(source.ContentLength, partSize);
        var newlyInitiated = checkpoint is null;
        MultipartUploadReconciliation reconciliation;
        if (checkpoint is not null)
        {
            try
            {
                using var lease = await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false);
                var parts = await ListMultipartPartsAsync(destinationClient, destinationBucket, destinationKey,
                    checkpoint.UploadId, cancellationToken).ConfigureAwait(false);
                reconciliation = MultipartUploadPlanner.Reconcile(source.ContentLength, partSize, parts);
                checkpoint = checkpoint with { CompletedParts = reconciliation.ConfirmedParts };
            }
            catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
            {
                throw new TransferExecutionException(new TransferFailureInfo(
                    "跨连接复制分片已不存在，可能已经完成；请核对目标后取消或重新建立任务。",
                    TransferFailureCategory.Conflict), exception);
            }
        }
        else
        {
            var request = CreateMultipartCopyRequest(source, destinationBucket, destinationKey);
            request.WebsiteRedirectLocation = source.WebsiteRedirectLocation;
            request.ServerSideEncryptionMethod = RequiredUploadEncryption(context.Options);
            request.TagSet = await ReadCrossConnectionTagsAsync(sourceClient, sourceProfile, sourceBucket, sourceKey,
                destinationProfile, source.VersionId, context, cancellationToken).ConfigureAwait(false);
            InitiateMultipartUploadResponse initiated;
            using (await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false))
                initiated = await destinationClient.InitiateMultipartUploadAsync(request, cancellationToken).ConfigureAwait(false);
            checkpoint = new MultipartUploadCheckpoint(initiated.UploadId, partSize, [],
                Bucket: destinationBucket, ObjectKey: destinationKey, SourceLength: source.ContentLength,
                InitiatedAt: DateTimeOffset.UtcNow)
            {
                CopySourceProfileId = sourceProfile.Id, CopyDestinationProfileId = destinationProfile.Id,
                CopySourceBucket = sourceBucket, CopySourceKey = sourceKey,
                CopySourceETag = source.ETag, CopySourceVersionId = source.VersionId
            };
            reconciliation = MultipartUploadPlanner.Reconcile(source.ContentLength, partSize, []);
        }

        var uploadId = checkpoint.UploadId;
        var initialCheckpointSaved = false;
        try
        {
            await context.UpdateCheckpointAsync(reconciliation.ConfirmedBytes, null, checkpoint, cancellationToken).ConfigureAwait(false);
            initialCheckpointSaved = true;
            context.ReportProgress(new TransferProgress(reconciliation.ConfirmedBytes, source.ContentLength));
            var completed = reconciliation.ConfirmedParts.ToDictionary(part => part.PartNumber);
            var progressGate = new object();
            var transferredBytes = reconciliation.ConfirmedBytes;
            using var checkpointGate = new SemaphoreSlim(1, 1);
            await Parallel.ForEachAsync(reconciliation.MissingParts, new ParallelOptions
            {
                MaxDegreeOfParallelism = context.Options.MultipartConcurrency,
                CancellationToken = cancellationToken
            }, async (part, token) =>
            {
                await using var buffer = CreateCrossConnectionBuffer(part.Size);
                await BufferCrossConnectionRangeAsync(sourceClient, sourceBucket, sourceKey, source,
                    part.Offset, part.Size, buffer, context, token).ConfigureAwait(false);
                long reported = 0;
                await using var throttled = new ThrottledReadStream(buffer, context.BandwidthLimiter,
                    TransferDirection.Upload, _ =>
                    {
                        lock (progressGate)
                        {
                            var current = buffer.Position;
                            if (current <= reported) return;
                            transferredBytes += current - reported;
                            reported = current;
                            context.ReportProgress(new TransferProgress(transferredBytes, source.ContentLength));
                        }
                    }, leaveOpen: true);
                var request = new UploadPartRequest
                {
                    BucketName = destinationBucket, Key = destinationKey, UploadId = uploadId,
                    PartNumber = part.PartNumber, PartSize = part.Size,
                    InputStream = throttled, IsLastPart = part.Offset + part.Size == source.ContentLength
                };
                await S3UploadCompatibility.PrepareAsync(destinationProfile, request, buffer, token).ConfigureAwait(false);
                UploadPartResponse response;
                using (await context.RequestBudget.EnterAsync(token).ConfigureAwait(false))
                    response = await destinationClient.UploadPartAsync(request, token).ConfigureAwait(false);
                await checkpointGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    completed[part.PartNumber] = new MultipartPartCheckpoint(part.PartNumber, response.ETag, part.Size);
                    checkpoint = checkpoint with { CompletedParts = completed.Values.OrderBy(item => item.PartNumber).ToArray() };
                    var bytes = completed.Values.Sum(item => item.Size);
                    await context.UpdateCheckpointAsync(bytes, null, checkpoint, token).ConfigureAwait(false);
                }
                finally { checkpointGate.Release(); }
            }).ConfigureAwait(false);
            if (!conditional && !context.Options.AllowDestinationOverwrite)
                await CheckUploadDestinationAsync(destinationClient, destinationBucket, destinationKey, context, cancellationToken)
                    .ConfigureAwait(false);
            CompleteMultipartUploadResponse completion;
            using (await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false))
                completion = await destinationClient.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = destinationBucket, Key = destinationKey, UploadId = uploadId,
                    IfNoneMatch = conditional && !context.Options.AllowDestinationOverwrite ? "*" : null,
                    IfMatch = context.Options.ExpectedDestinationETag,
                    PartETags = completed.Values.OrderBy(part => part.PartNumber)
                        .Select(part => new PartETag(part.PartNumber, part.ETag)).ToList()
                }, cancellationToken).ConfigureAwait(false);
            await context.UpdateCheckpointAsync(source.ContentLength, null, checkpoint with
            {
                CopyCompleted = true, CopyDestinationETag = completion.ETag, CopyDestinationVersionId = completion.VersionId
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception transferException)
        {
            if (!context.Options.RetainFailedTransferState || (newlyInitiated && !initialCheckpointSaved))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    using var lease = await context.RequestBudget.EnterAsync(timeout.Token).ConfigureAwait(false);
                    await AbortMultipartInternalAsync(destinationClient, destinationBucket, destinationKey, uploadId, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException($"跨连接复制失败，且清理目标分片失败（UploadId={uploadId}）；请在目标连接的分片管理器中核对清理。",
                        transferException, cleanupException);
                }
            }
            throw;
        }
    }

    private static async Task BufferCrossConnectionRangeAsync(
        IAmazonS3 sourceClient, string sourceBucket, string sourceKey, GetObjectMetadataResponse source,
        long offset, long length, Stream buffer, TransferOperationContext context, CancellationToken cancellationToken)
    {
        using var lease = await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false);
        var wholeObject = offset == 0 && length == source.ContentLength;
        using var response = await sourceClient.GetObjectAsync(new GetObjectRequest
        {
            BucketName = sourceBucket, Key = sourceKey, VersionId = source.VersionId, EtagToMatch = source.ETag,
            ByteRange = wholeObject ? null : new ByteRange(offset, offset + length - 1)
        }, cancellationToken).ConfigureAwait(false);
        if (response.ETag != source.ETag || response.VersionId != source.VersionId)
            throw CrossConnectionConflict("读取期间源对象身份发生变化，未提交目标。");
        // If a compatible provider ignores Range, a full identity-pinned response can
        // still supply this part. Discard only its prefix, without buffering it.
        var fullResponse = response.HttpStatusCode == HttpStatusCode.OK;
        var expectedRange = string.Create(CultureInfo.InvariantCulture, $"bytes {offset}-{offset + length - 1}/{source.ContentLength}");
        if (fullResponse ? response.ContentLength != source.ContentLength :
            response.HttpStatusCode != HttpStatusCode.PartialContent || response.ContentLength != length ||
            !string.Equals(response.ContentRange, expectedRange, StringComparison.OrdinalIgnoreCase))
            throw CrossConnectionConflict("源对象响应长度或范围与请求不符，未提交目标。");
        var bytes = new byte[128 * 1024];
        var skip = fullResponse ? offset : 0;
        while (skip > 0)
        {
            var read = await response.ResponseStream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, skip)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new IOException("跨连接复制源响应提前结束。");
            await context.BandwidthLimiter.WaitAsync(TransferDirection.Download, read, cancellationToken).ConfigureAwait(false);
            skip -= read;
        }
        long received = 0;
        while (received < length)
        {
            var read = await response.ResponseStream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, length - received)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("跨连接复制源响应提前结束。");
            await context.BandwidthLimiter.WaitAsync(TransferDirection.Download, read, cancellationToken).ConfigureAwait(false);
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
        }
        if ((!fullResponse || offset + length == source.ContentLength) &&
            await response.ResponseStream.ReadAsync(bytes.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            throw CrossConnectionConflict("源对象响应超过请求长度，未提交目标。");
        buffer.Position = 0;
    }

    private static Stream CreateCrossConnectionBuffer(long length) => length <= CrossConnectionMemoryBufferBytes
        ? new MemoryStream((int)length)
        : new FileStream(Path.Combine(Path.GetTempPath(), $"s3explorer-copy-{Guid.NewGuid():N}.part"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

    private static async Task<List<Tag>?> ReadCrossConnectionTagsAsync(
        IAmazonS3 sourceClient, ConnectionProfile sourceProfile, string sourceBucket, string sourceKey,
        ConnectionProfile destinationProfile, string? sourceVersion, TransferOperationContext context,
        CancellationToken cancellationToken)
    {
        if (!S3ProviderCapabilityRegistry.For(sourceProfile.ServiceType).Object.Tagging.Supported ||
            !S3ProviderCapabilityRegistry.For(destinationProfile.ServiceType).Object.Tagging.Supported)
            return null;
        using var lease = await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false);
        var response = await sourceClient.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = sourceBucket, Key = sourceKey, VersionId = sourceVersion
        }, cancellationToken).ConfigureAwait(false);
        return response.Tagging is { Count: > 0 } ? response.Tagging : null;
    }

    private static void ApplyCrossConnectionHeaders(GetObjectMetadataResponse source,
        HeadersCollection headers, MetadataCollection metadata)
    {
        headers.ContentType = source.Headers.ContentType;
        headers.CacheControl = source.Headers.CacheControl;
        headers.ContentEncoding = source.Headers.ContentEncoding;
        headers.ContentDisposition = source.Headers.ContentDisposition;
        headers.ContentLanguage = source.Headers.ContentLanguage;
        headers["Expires"] = source.ExpiresString ?? source.Headers["Expires"];
        foreach (var name in source.Metadata.Keys) metadata[name] = source.Metadata[name];
    }

    private static async Task VerifyCrossConnectionDestinationAsync(
        IAmazonS3 destinationClient, string destinationBucket, string destinationKey, MultipartUploadCheckpoint checkpoint,
        TransferOperationContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.CopyDestinationETag))
            throw CrossConnectionConflict("复制完成断点缺少目标身份，请人工核对目标。");
        GetObjectMetadataResponse destination;
        try
        {
            using var lease = await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false);
            destination = await destinationClient.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = destinationBucket, Key = destinationKey
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsObjectNotFound(exception))
        {
            throw CrossConnectionConflict("复制完成后的目标已不存在，请核对后重新建立任务。");
        }
        if (destination.ETag != checkpoint.CopyDestinationETag || destination.VersionId != checkpoint.CopyDestinationVersionId ||
            destination.ContentLength != checkpoint.SourceLength)
            throw CrossConnectionConflict("复制完成后的目标身份已改变，请核对后重新建立任务。");
    }

    private static TransferExecutionException CrossConnectionConflict(string message) =>
        new(new TransferFailureInfo(message, TransferFailureCategory.Conflict));

    private static bool SameCrossConnectionStorage(ConnectionProfile source, ConnectionProfile destination)
    {
        if (source.Id == destination.Id || source.NormalizedEndpoint == destination.NormalizedEndpoint) return true;
        // AWS regional/global S3 endpoints are aliases for a bucket, not separate
        // stores. Bucket names can repeat across the commercial and China partitions.
        return source.ServiceType == S3ServiceType.AmazonS3 && destination.ServiceType == S3ServiceType.AmazonS3 &&
            AmazonS3Partition(source.NormalizedEndpoint) is { } partition &&
            partition == AmazonS3Partition(destination.NormalizedEndpoint);
    }

    private static string? AmazonS3Partition(Uri endpoint)
    {
        if (endpoint.AbsolutePath != "/" || !endpoint.IsDefaultPort) return null;
        var host = endpoint.DnsSafeHost;
        if (!host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("s3-", StringComparison.OrdinalIgnoreCase)) return null;
        if (host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase)) return "aws-cn";
        if (host.StartsWith("s3.us-gov-", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("s3-us-gov-", StringComparison.OrdinalIgnoreCase))
            return host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) ? "aws-us-gov" : null;
        return host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) ? "aws" : null;
    }
}
