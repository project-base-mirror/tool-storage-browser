using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    internal static async Task CopyOrMoveObjectAsync(
        IAmazonS3 client, ConnectionProfile profile,
        string sourceBucket, string sourceKey, string destinationBucket, string destinationKey,
        bool move, CancellationToken cancellationToken, TransferOperationContext? context = null)
    {
        if (sourceBucket == destinationBucket && sourceKey == destinationKey)
            throw new TransferExecutionException(new TransferFailureInfo(
                "来源与目标相同。", TransferFailureCategory.Validation));
        var conditionalDelete = S3ProviderCapabilityRegistry.For(profile.ServiceType).ConditionalDelete;
        if (move && !conditionalDelete.Supported)
            throw new TransferExecutionException(new TransferFailureInfo(
                conditionalDelete.Reason, TransferFailureCategory.Validation, Retryable: false));
        try
        {
            var source = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = sourceBucket,
                Key = sourceKey,
                VersionId = context?.MultipartCheckpoint?.CopySourceVersionId
            }, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(source.ETag))
                throw new TransferExecutionException(new TransferFailureInfo(
                    "源对象没有可用于条件复制的 ETag，已停止操作。", TransferFailureCategory.Conflict));

            var saved = context?.MultipartCheckpoint;
            if (saved is not null &&
                (saved.CleanupPending || saved.Bucket != destinationBucket || saved.ObjectKey != destinationKey ||
                 saved.CopySourceBucket != sourceBucket || saved.CopySourceKey != sourceKey ||
                 saved.CopySourceETag != source.ETag || saved.CopySourceVersionId != source.VersionId ||
                 saved.SourceLength != source.ContentLength))
                throw new TransferExecutionException(new TransferFailureInfo(
                    "复制断点与当前来源或目标不一致，请取消旧任务清理分片后重新复制。", TransferFailureCategory.Conflict));

            var copyTags = S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging.Supported;
            if (saved is not null || (profile.EnableMultipartCopy &&
                (source.ContentLength > MaximumSingleCopyBytes ||
                 (context is not null && profile.ServiceType != S3ServiceType.CloudflareR2 &&
                  source.ContentLength >= context.Options.MultipartThresholdBytes))))
            {
                EnsureMultipartCopySourcePreconditions(profile, source);
                await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket,
                    destinationKey, source, copyTags, cancellationToken, context).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await CopyObjectSimpleAsync(client, sourceBucket, sourceKey, destinationBucket,
                        destinationKey, source, cancellationToken).ConfigureAwait(false);
                }
                catch (AmazonS3Exception exception) when (
                    profile.EnableMultipartCopy && S3CompatibilityPolicy.RequiresMultipartCopy(exception))
                {
                    EnsureMultipartCopySourcePreconditions(profile, source);
                    await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket,
                        destinationKey, source, copyTags, cancellationToken, context).ConfigureAwait(false);
                }
            }

            context?.ReportProgress(new TransferProgress(source.ContentLength, source.ContentLength));
            if (saved?.CopyCompleted == true)
            {
                // A paused completed copy is not proof that its destination still exists.
                // HEAD the current version (not the historic version) before removing source.
                if (string.IsNullOrWhiteSpace(saved.CopyDestinationETag))
                    throw new TransferExecutionException(new TransferFailureInfo(
                        "复制完成断点缺少目标身份，未删除源对象，请人工核对目标。", TransferFailureCategory.Conflict));
                GetObjectMetadataResponse destination;
                try
                {
                    destination = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                    {
                        BucketName = destinationBucket, Key = destinationKey
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new TransferExecutionException(new TransferFailureInfo(
                        "复制完成后的目标已不存在，未删除源对象。", TransferFailureCategory.Conflict), exception);
                }
                if (destination.ETag != saved.CopyDestinationETag ||
                    destination.VersionId != saved.CopyDestinationVersionId || destination.ContentLength != source.ContentLength)
                    throw new TransferExecutionException(new TransferFailureInfo(
                        "复制完成后的目标身份已改变，未删除源对象。", TransferFailureCategory.Conflict));
            }
            if (!move) return;
            try
            {
                // Deliberately omit VersionId: preserve normal move semantics (a delete marker in
                // versioned buckets), never permanently delete an old version as a side effect.
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = sourceBucket,
                    Key = sourceKey,
                    IfMatch = source.ETag
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failure = ToTransferException(exception).Failure;
                throw new TransferExecutionException(failure with
                {
                    Message = "复制已完成，但源对象条件删除未确认；未执行无条件删除，请刷新核对。" + failure.SafeMessage,
                    Retryable = false
                }, exception);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToTransferException(exception);
        }
    }

    private static void EnsureMultipartCopySourcePreconditions(ConnectionProfile profile, GetObjectMetadataResponse source)
    {
        if (profile.ServiceType == S3ServiceType.CloudflareR2 && string.IsNullOrWhiteSpace(source.VersionId))
            throw new TransferExecutionException(new TransferFailureInfo(
                "R2 不支持分片复制的源 If-Match 条件，且来源没有固定版本；为避免混合内容已停止分片复制。",
                TransferFailureCategory.Validation, Retryable: false));
    }

    private static async Task CopyObjectSimpleAsync(
        IAmazonS3 client, string sourceBucket, string sourceKey, string destinationBucket,
        string destinationKey, GetObjectMetadataResponse source, CancellationToken cancellationToken)
    {
        using var lease = await SharedTransferRequestBudget.Default.EnterAsync(cancellationToken).ConfigureAwait(false);
        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            SourceVersionId = source.VersionId,
            ETagToMatch = source.ETag,
            DestinationBucket = destinationBucket,
            DestinationKey = destinationKey
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MultipartCopyAsync(
        IAmazonS3 client, string sourceBucket, string sourceKey, string destinationBucket,
        string destinationKey, GetObjectMetadataResponse source, bool copyTags,
        CancellationToken cancellationToken, TransferOperationContext? context = null)
    {
        var objectSize = source.ContentLength;
        if (objectSize <= 0)
            throw new InvalidOperationException("无法对空对象执行 Multipart Copy。");
        var checkpoint = context?.MultipartCheckpoint;
        if (checkpoint?.CopyCompleted == true) return;
        var partSize = checkpoint?.PartSize ?? (context is null
            ? S3CompatibilityPolicy.CalculateCopyPartSize(objectSize)
            : MultipartUploadPlanner.EffectivePartSize(objectSize, context.Options.PartSizeBytes));
        _ = MultipartUploadPlanner.BuildParts(objectSize, partSize);
        MultipartUploadReconciliation reconciliation;
        var newlyInitiated = checkpoint is null;
        if (checkpoint is not null)
        {
            try
            {
                var remote = await ListMultipartPartsAsync(client, destinationBucket, destinationKey,
                    checkpoint.UploadId, cancellationToken).ConfigureAwait(false);
                reconciliation = MultipartUploadPlanner.Reconcile(objectSize, partSize, remote);
                checkpoint = checkpoint with { CompletedParts = reconciliation.ConfirmedParts };
            }
            catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
            {
                // Completion may have succeeded but its response/checkpoint was lost. Do not
                // overwrite the destination or re-copy a changed source automatically.
                throw new TransferExecutionException(new TransferFailureInfo(
                    "复制分片已不存在，可能已经完成；请核对目标后取消或重新建立任务。",
                    TransferFailureCategory.Conflict, Retryable: false), exception);
            }
        }
        else
        {
            var request = CreateMultipartCopyRequest(source, destinationBucket, destinationKey);
            if (copyTags)
            {
                var tags = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest
                {
                    BucketName = sourceBucket, Key = sourceKey, VersionId = source.VersionId
                }, cancellationToken).ConfigureAwait(false);
                request.TagSet = tags.Tagging ?? [];
            }
            var initiate = await client.InitiateMultipartUploadAsync(request, cancellationToken).ConfigureAwait(false);
            checkpoint = new MultipartUploadCheckpoint(initiate.UploadId, partSize, [],
                Bucket: destinationBucket, ObjectKey: destinationKey, SourceLength: objectSize,
                InitiatedAt: DateTimeOffset.UtcNow)
            {
                CopySourceBucket = sourceBucket, CopySourceKey = sourceKey,
                CopySourceETag = source.ETag, CopySourceVersionId = source.VersionId
            };
            reconciliation = MultipartUploadPlanner.Reconcile(objectSize, partSize, []);
        }
        var uploadId = checkpoint.UploadId;
        var initialCheckpointSaved = false;
        try
        {
            if (context is not null)
                await context.UpdateCheckpointAsync(reconciliation.ConfirmedBytes, null, checkpoint, cancellationToken)
                    .ConfigureAwait(false);
            initialCheckpointSaved = true;
            context?.ReportProgress(new TransferProgress(reconciliation.ConfirmedBytes, objectSize));
            var completed = reconciliation.ConfirmedParts.ToDictionary(part => part.PartNumber);
            using var checkpointGate = new SemaphoreSlim(1, 1);
            await Parallel.ForEachAsync(reconciliation.MissingParts, new ParallelOptions
            {
                MaxDegreeOfParallelism = context?.Options.MultipartConcurrency ?? 4,
                CancellationToken = cancellationToken
            }, async (part, token) =>
            {
                using var lease = await (context?.RequestBudget ?? SharedTransferRequestBudget.Default)
                    .EnterAsync(token).ConfigureAwait(false);
                var response = await client.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = sourceBucket, SourceKey = sourceKey, SourceVersionId = source.VersionId,
                    ETagToMatch = [source.ETag], DestinationBucket = destinationBucket,
                    DestinationKey = destinationKey, UploadId = uploadId,
                    PartNumber = part.PartNumber, FirstByte = part.Offset, LastByte = part.Offset + part.Size - 1
                }, token).ConfigureAwait(false);
                lease.Dispose();
                await checkpointGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    completed[part.PartNumber] = new MultipartPartCheckpoint(part.PartNumber, response.ETag, part.Size);
                    checkpoint = checkpoint with { CompletedParts = completed.Values.OrderBy(item => item.PartNumber).ToArray() };
                    var bytes = completed.Values.Sum(item => item.Size);
                    if (context is not null)
                        await context.UpdateCheckpointAsync(bytes, null, checkpoint, token).ConfigureAwait(false);
                    context?.ReportProgress(new TransferProgress(bytes, objectSize));
                }
                finally { checkpointGate.Release(); }
            }).ConfigureAwait(false);
            var completion = await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = destinationBucket, Key = destinationKey, UploadId = uploadId,
                PartETags = completed.Values.OrderBy(part => part.PartNumber)
                    .Select(part => new PartETag(part.PartNumber, part.ETag)).ToList()
            }, cancellationToken).ConfigureAwait(false);
            if (context is not null)
                await context.UpdateCheckpointAsync(objectSize, null, checkpoint with
                {
                    CopyCompleted = true, CopyDestinationETag = completion.ETag,
                    CopyDestinationVersionId = completion.VersionId
                }, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch
        {
            // Persistent tasks retain the upload ID across pause/transient errors; queue cancel
            // uses the existing MPU cleanup path. Legacy API calls have no durable owner.
            if (context is null || (newlyInitiated && !initialCheckpointSaved))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await AbortMultipartInternalAsync(client, destinationBucket, destinationKey,
                        uploadId, cleanup.Token).ConfigureAwait(false);
                }
                catch { /* Preserve the original failure; MPU manager can find the residual upload. */ }
            }
            throw;
        }
    }

    internal static InitiateMultipartUploadRequest CreateMultipartCopyRequest(
        GetObjectMetadataResponse source, string bucket, string key)
    {
        // Like ordinary CopyObject, use the destination's storage/encryption defaults. Metadata
        // and content headers belong to the source and must not change with the copy size.
        var request = new InitiateMultipartUploadRequest { BucketName = bucket, Key = key };
        request.Headers.ContentType = source.Headers.ContentType;
        request.Headers.CacheControl = source.Headers.CacheControl;
        request.Headers.ContentEncoding = source.Headers.ContentEncoding;
        request.Headers.ContentDisposition = source.Headers.ContentDisposition;
        request.Headers.ContentLanguage = source.Headers.ContentLanguage;
        request.Headers.Expires = source.Headers.Expires;
        foreach (var name in source.Metadata.Keys)
            request.Metadata[name] = source.Metadata[name];
        return request;
    }
}
