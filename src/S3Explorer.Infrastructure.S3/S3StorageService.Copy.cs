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
        MinioObjectKeyPolicy.Validate(profile.ServiceType, sourceKey);
        MinioObjectKeyPolicy.Validate(profile.ServiceType, destinationKey);
        if (sourceBucket == destinationBucket && sourceKey == destinationKey)
            throw new TransferExecutionException(new TransferFailureInfo(
                "来源与目标相同。", TransferFailureCategory.Validation));
        context?.Options.Validate();
        var protectDestination = context is not null &&
            (!context.Options.AllowDestinationOverwrite || context.Options.ExpectedDestinationETag is not null);
        // Retain verified atomic conditions where available. Other compatible providers
        // use the same best-effort existence check as ordinary uploads; do not disable
        // normal create-only copying merely because a vendor lacks AWS's headers.
        var conditionalDestination = profile.ServiceType is S3ServiceType.AmazonS3 or S3ServiceType.MinIO or S3ServiceType.CloudflareR2;
        if (!conditionalDestination && context?.Options.ExpectedDestinationETag is not null)
            throw new TransferExecutionException(new TransferFailureInfo(
                "此服务商不支持按指定 ETag 条件替换目标，未写入目标。", TransferFailureCategory.Validation));
        var conditionalDelete = S3ProviderCapabilityRegistry.For(profile.ServiceType).ConditionalDelete.Supported;
        var relayCheckpoint = context?.MultipartCheckpoint?.CopySourceProfileId == profile.Id;
        if (profile.ServiceType is S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage &&
            context?.MultipartCheckpoint is not null && !relayCheckpoint)
            throw new TransferExecutionException(new TransferFailureInfo(
                "旧服务端分片复制断点不能转换为本机中转；请取消旧任务清理分片后重新复制。",
                TransferFailureCategory.Validation, Retryable: false));
        try
        {
            var source = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = sourceBucket,
                Key = sourceKey,
                VersionId = context?.MultipartCheckpoint?.CopySourceVersionId
            }, cancellationToken).ConfigureAwait(false);
            if (profile.ServiceType is S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage)
                source.VersionId = null;
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

            var relay = relayCheckpoint ||
                (protectDestination && profile.ServiceType == S3ServiceType.MinIO && !profile.EnableMultipartCopy && source.ContentLength > 0) ||
                (source.ContentLength > MaximumSingleCopyBytes &&
                 (!profile.EnableMultipartCopy || profile.ServiceType is S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage));
            if (!conditionalDestination && protectDestination && saved?.CopyCompleted != true)
                await CheckUploadDestinationAsync(client, destinationBucket, destinationKey, context!, cancellationToken)
                    .ConfigureAwait(false);
            var copyTags = S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging.Supported;
            if (relay)
            {
                await RelayCopyAsync(client, profile, sourceBucket, sourceKey, destinationBucket,
                    destinationKey, context, cancellationToken).ConfigureAwait(false);
            }
            else if (saved is null && protectDestination && profile.ServiceType == S3ServiceType.MinIO &&
                source.ContentLength == 0)
            {
                await CopyEmptyObjectConditionallyAsync(client, profile, sourceBucket, sourceKey,
                    destinationBucket, destinationKey, source, cancellationToken, context!).ConfigureAwait(false);
            }
            // Supabase's ordinary CopyObject enforces source If-Match. Do not turn a
            // safe <= 5 GiB copy into an unsafe multipart copy merely for throughput.
            else if (saved is not null || (protectDestination && profile.ServiceType == S3ServiceType.MinIO) ||
                (profile.EnableMultipartCopy &&
                (source.ContentLength > MaximumSingleCopyBytes ||
                 (context is not null && profile.ServiceType is not (S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage) &&
                  source.ContentLength >= context.Options.MultipartThresholdBytes))))
            {
                EnsureMultipartCopySourcePreconditions(profile);
                await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket,
                    destinationKey, source, copyTags, cancellationToken, context, conditionalDestination).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    if (!conditionalDestination && protectDestination)
                        await CheckUploadDestinationAsync(client, destinationBucket, destinationKey, context!, cancellationToken)
                            .ConfigureAwait(false);
                    await CopyObjectSimpleAsync(client, sourceBucket, sourceKey, destinationBucket,
                        destinationKey, source, cancellationToken, conditionalDestination ? context?.Options : null,
                        useSourceVersion: profile.ServiceType is not (S3ServiceType.SupabaseStorage or S3ServiceType.CloudflareR2),
                        useR2DestinationConditions: profile.ServiceType == S3ServiceType.CloudflareR2).ConfigureAwait(false);
                }
                catch (AmazonS3Exception exception) when (
                    S3CompatibilityPolicy.RequiresMultipartCopy(exception))
                {
                    if (!profile.EnableMultipartCopy || profile.ServiceType is S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage)
                        await RelayCopyAsync(client, profile, sourceBucket, sourceKey, destinationBucket,
                            destinationKey, context, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        EnsureMultipartCopySourcePreconditions(profile);
                        await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket,
                            destinationKey, source, copyTags, cancellationToken, context, conditionalDestination).ConfigureAwait(false);
                    }
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
                // A successful copy must precede deletion. Providers without atomic delete
                // conditions get a last-moment source recheck; the UI discloses that this
                // cannot close the race and concurrent writers must be stopped.
                var current = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = sourceBucket, Key = sourceKey
                }, cancellationToken).ConfigureAwait(false);
                if (profile.ServiceType is S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage)
                    current.VersionId = null;
                if (current.ETag != source.ETag || current.VersionId != source.VersionId ||
                    current.ContentLength != source.ContentLength)
                    throw CrossConnectionConflict("复制已完成，但源对象已变化，未删除源对象。");
                // Deliberately omit VersionId: preserve normal move semantics (a delete marker in
                // versioned buckets), never permanently delete an old version as a side effect.
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = sourceBucket,
                    Key = sourceKey,
                    IfMatch = conditionalDelete ? source.ETag : null
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failure = ToTransferException(exception).Failure;
                throw new TransferExecutionException(failure with
                {
                    Message = "复制已完成，但源对象删除未确认，请刷新核对；不要直接重试移动。" + failure.SafeMessage,
                    Retryable = false
                }, exception);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToTransferException(exception);
        }
    }

    private static Task RelayCopyAsync(IAmazonS3 client, ConnectionProfile profile,
        string sourceBucket, string sourceKey, string destinationBucket, string destinationKey,
        TransferOperationContext? context, CancellationToken cancellationToken)
    {
        // Reuse the bounded, resumable GET + upload path rather than unsupported
        // UploadPartCopy source conditions. Both bandwidth lanes remain effective.
        context ??= new TransferOperationContext(
            new TransferExecutionOptions { RetainFailedTransferState = false },
            new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
        return CopyObjectAcrossConnectionsAsync(client, profile, sourceBucket, sourceKey,
            client, profile, destinationBucket, destinationKey, context, cancellationToken);
    }

    private static void EnsureMultipartCopySourcePreconditions(ConnectionProfile profile)
    {
        // Neither provider supports immutable S3 versions or the multipart source
        // condition. An arbitrary VersionId response cannot establish safety.
        if (profile.ServiceType is S3ServiceType.SupabaseStorage or S3ServiceType.CloudflareR2)
            throw new TransferExecutionException(new TransferFailureInfo(
                "此服务商不支持安全的服务端分片复制；请重新建立任务使用本机中转复制。",
                TransferFailureCategory.Validation, Retryable: false));
    }

    private static async Task CopyObjectSimpleAsync(
        IAmazonS3 client, string sourceBucket, string sourceKey, string destinationBucket,
        string destinationKey, GetObjectMetadataResponse source, CancellationToken cancellationToken,
        TransferExecutionOptions? options = null, bool useSourceVersion = true,
        bool useR2DestinationConditions = false)
    {
        using var lease = await SharedTransferRequestBudget.Default.EnterAsync(cancellationToken).ConfigureAwait(false);
        var request = new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            // Supabase and R2 do not support S3 version addressing. Their single-copy source
            // condition is supported, unlike UploadPartCopy's ignored condition.
            SourceVersionId = useSourceVersion ? source.VersionId : null,
            ETagToMatch = source.ETag,
            DestinationBucket = destinationBucket,
            DestinationKey = destinationKey,
            IfNoneMatch = !useR2DestinationConditions && options?.AllowDestinationOverwrite == false ? "*" : null,
            IfMatch = useR2DestinationConditions ? null : options?.ExpectedDestinationETag
        };
        if (useR2DestinationConditions)
        {
            // R2 checks the source when selecting its snapshot and the destination
            // when committing it. They are NOT a joint two-key transaction. This
            // is not an atomic move; source deletion is a separate, disclosed step.
            // Keep metadata server-side, including any fields omitted from HEAD.
            request.MetadataDirective = S3MetadataDirective.COPY;
            if (options?.AllowDestinationOverwrite == false)
                request.Headers["cf-copy-destination-if-none-match"] = "*";
            if (options?.ExpectedDestinationETag is { } expected)
                request.Headers["cf-copy-destination-if-match"] = expected;
        }
        await client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyEmptyObjectConditionallyAsync(
        IAmazonS3 client, ConnectionProfile profile, string sourceBucket, string sourceKey,
        string destinationBucket, string destinationKey, GetObjectMetadataResponse source,
        CancellationToken cancellationToken, TransferOperationContext context)
    {
        // UploadPartCopy cannot represent a zero-byte source. Verify the source through a
        // conditional GET, then create the empty destination with the same atomic PUT
        // conditions used by uploads. A destination HEAD would not protect against races.
        using var lease = await context.RequestBudget.EnterAsync(cancellationToken).ConfigureAwait(false);
        using var verified = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = sourceBucket, Key = sourceKey, VersionId = source.VersionId,
            EtagToMatch = source.ETag
        }, cancellationToken).ConfigureAwait(false);
        if (verified.MissingMeta.GetValueOrDefault() > 0)
            throw new TransferExecutionException(new TransferFailureInfo(
                "源对象有未能读取的 Metadata，无法完整保留属性，未写入目标。", TransferFailureCategory.Validation, Retryable: false));
        if (verified.ContentLength != 0 || verified.ETag != source.ETag || verified.VersionId != source.VersionId ||
            await verified.ResponseStream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new TransferExecutionException(new TransferFailureInfo(
                "空对象复制前来源内容或身份已变化，未写入目标。", TransferFailureCategory.Conflict, Retryable: false));

        var tags = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = sourceBucket, Key = sourceKey, VersionId = source.VersionId
        }, cancellationToken).ConfigureAwait(false);
        using var empty = new MemoryStream(Array.Empty<byte>(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = destinationBucket, Key = destinationKey, InputStream = empty,
            AutoCloseStream = false,
            IfNoneMatch = context.Options.AllowDestinationOverwrite ? null : "*",
            IfMatch = context.Options.ExpectedDestinationETag,
            TagSet = tags.Tagging is { Count: > 0 } ? tags.Tagging : null
        };
        // Use the headers returned with the verified content, rather than a stale HEAD.
        // As with ordinary copies, the destination bucket supplies encryption/ACL defaults.
        request.Headers.ContentType = verified.Headers.ContentType;
        request.Headers.CacheControl = verified.Headers.CacheControl;
        request.Headers.ContentEncoding = verified.Headers.ContentEncoding;
        request.Headers.ContentDisposition = verified.Headers.ContentDisposition;
        request.Headers.ContentLanguage = verified.Headers.ContentLanguage;
        // Copy the wire value: the SDK's nullable Expires setter writes a year-0001
        // date for null, and parsing/reformatting can alter provider-supplied headers.
        request.Headers["Expires"] = verified.ExpiresString ?? verified.Headers["Expires"];
        foreach (var name in verified.Metadata.Keys)
            request.Metadata[name] = verified.Metadata[name];
        await S3UploadCompatibility.PrepareAsync(profile, request, empty, cancellationToken).ConfigureAwait(false);
        await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MultipartCopyAsync(
        IAmazonS3 client, string sourceBucket, string sourceKey, string destinationBucket,
        string destinationKey, GetObjectMetadataResponse source, bool copyTags,
        CancellationToken cancellationToken, TransferOperationContext? context = null, bool conditionalDestination = true)
    {
        var objectSize = source.ContentLength;
        if (objectSize <= 0)
            throw new TransferExecutionException(new TransferFailureInfo(
                "此复制方式无法安全处理空对象，未写入目标；请使用支持原子条件 CopyObject 的服务商。",
                TransferFailureCategory.Validation, Retryable: false));
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
                request.TagSet = tags.Tagging is { Count: > 0 } ? tags.Tagging : null;
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
                    PartNumber = part.PartNumber,
                    // UploadPartCopy rejects byte ranges for sources of 5 MiB or less.
                    // A single full-object part needs no range header at any size.
                    FirstByte = part.Offset == 0 && part.Size == objectSize ? null : part.Offset,
                    LastByte = part.Offset == 0 && part.Size == objectSize ? null : part.Offset + part.Size - 1
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
            if (!conditionalDestination && context?.Options.AllowDestinationOverwrite == false)
                await CheckUploadDestinationAsync(client, destinationBucket, destinationKey, context, cancellationToken)
                    .ConfigureAwait(false);
            var completion = await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = destinationBucket, Key = destinationKey, UploadId = uploadId,
                IfNoneMatch = conditionalDestination && context?.Options.AllowDestinationOverwrite == false ? "*" : null,
                IfMatch = context?.Options.ExpectedDestinationETag,
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
            if (context is null || !context.Options.RetainFailedTransferState || (newlyInitiated && !initialCheckpointSaved))
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
        request.Headers["Expires"] = source.ExpiresString ?? source.Headers["Expires"];
        foreach (var name in source.Metadata.Keys)
            request.Metadata[name] = source.Metadata[name];
        return request;
    }
}
