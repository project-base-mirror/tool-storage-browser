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
        bool move, CancellationToken cancellationToken)
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
                Key = sourceKey
            }, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(source.ETag))
                throw new TransferExecutionException(new TransferFailureInfo(
                    "源对象没有可用于条件复制的 ETag，已停止操作。", TransferFailureCategory.Conflict));

            var copyTags = S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging.Supported;
            if (profile.EnableMultipartCopy && source.ContentLength > MaximumSingleCopyBytes)
            {
                await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket,
                    destinationKey, source, copyTags, cancellationToken).ConfigureAwait(false);
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
                    await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket,
                        destinationKey, source, copyTags, cancellationToken).ConfigureAwait(false);
                }
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
        CancellationToken cancellationToken)
    {
        var objectSize = source.ContentLength;
        if (objectSize <= 0)
            throw new InvalidOperationException("无法对空对象执行 Multipart Copy。");
        var partSize = S3CompatibilityPolicy.CalculateCopyPartSize(objectSize);
        var parts = MultipartUploadPlanner.BuildParts(objectSize, partSize);
        var request = CreateMultipartCopyRequest(source, destinationBucket, destinationKey);
        if (copyTags)
        {
            var tags = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = sourceBucket,
                Key = sourceKey,
                VersionId = source.VersionId
            }, cancellationToken).ConfigureAwait(false);
            request.TagSet = tags.Tagging ?? [];
        }
        var initiate = await client.InitiateMultipartUploadAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var partEtags = new List<PartETag>();
            foreach (var part in parts)
            {
                using var lease = await SharedTransferRequestBudget.Default.EnterAsync(cancellationToken).ConfigureAwait(false);
                var response = await client.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = sourceBucket,
                    SourceKey = sourceKey,
                    SourceVersionId = source.VersionId,
                    ETagToMatch = [source.ETag],
                    DestinationBucket = destinationBucket,
                    DestinationKey = destinationKey,
                    UploadId = initiate.UploadId,
                    PartNumber = part.PartNumber,
                    FirstByte = part.Offset,
                    LastByte = part.Offset + part.Size - 1
                }, cancellationToken).ConfigureAwait(false);
                partEtags.Add(new PartETag(part.PartNumber, response.ETag));
            }
            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = destinationBucket,
                Key = destinationKey,
                UploadId = initiate.UploadId,
                PartETags = partEtags
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = destinationBucket,
                    Key = destinationKey,
                    UploadId = initiate.UploadId
                }, cleanup.Token).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original error; the existing MPU manager can find residual uploads.
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
