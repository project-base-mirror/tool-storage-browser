using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConditionalWriteIntegrationTests
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task CreateOnlySmallServerCopyPreservesExistingTargetAndCopiesToAbsentTarget()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile()
            with { EnableMultipartCopy = true };
        using var client = new S3ClientFactory().Create(profile);
        var service = new S3StorageService(new S3ClientFactory());
        var bucket = $"s3explorer-copy-condition-{Guid.NewGuid():N}";
        var context = new TransferOperationContext(new TransferExecutionOptions
        {
            AllowDestinationOverwrite = false, RetainFailedTransferState = false
        }, new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, token);
        try
        {
            await client.PutObjectAsync(new PutObjectRequest
                { BucketName = bucket, Key = "source", ContentBody = "source" }, token);
            var original = await client.PutObjectAsync(new PutObjectRequest
                { BucketName = bucket, Key = "target", ContentBody = "keep-existing" }, token);
            var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.CopyObjectAsync(profile, bucket, "source", bucket, "target", context, token));
            Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
            var unchanged = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = bucket, Key = "target" }, token);
            Assert.Equal(original.ETag, unchanged.ETag);
            await service.CopyObjectAsync(profile, bucket, "source", bucket, "new", context, token);
            using var copied = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "new" }, token);
            using var reader = new StreamReader(copied.ResponseStream);
            Assert.Equal("source", await reader.ReadToEndAsync(token));
        }
        finally
        {
            foreach (var key in new[] { "source", "target", "new" })
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, CancellationToken.None);
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, CancellationToken.None);
        }
    }

    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task EmptyCreateOnlyCopyPreservesPropertiesAndRejectsExistingDestination()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile()
            with { EnableMultipartCopy = false };
        using var client = new S3ClientFactory().Create(profile);
        var service = new S3StorageService(new S3ClientFactory());
        var bucket = $"s3explorer-empty-copy-{Guid.NewGuid():N}";
        var context = new TransferOperationContext(new TransferExecutionOptions
        {
            AllowDestinationOverwrite = false, RetainFailedTransferState = false
        }, new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, token);
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = bucket, Key = "source", ContentBody = "",
                TagSet = [new Tag { Key = "purpose", Value = "empty-copy" }]
            };
            request.Headers.ContentType = "application/test";
            request.Headers.CacheControl = "max-age=123";
            request.Headers.ContentDisposition = "attachment";
            request.Headers.ContentLanguage = "en";
            request.Headers.Expires = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            request.Metadata["custom"] = "preserved";
            await client.PutObjectAsync(request, token);
            var original = await client.PutObjectAsync(new PutObjectRequest
                { BucketName = bucket, Key = "target", ContentBody = "keep-existing" }, token);
            var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.CopyObjectAsync(profile, bucket, "source", bucket, "target", context, token));
            Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
            var unchanged = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = bucket, Key = "target" }, token);
            Assert.Equal(original.ETag, unchanged.ETag);

            await service.CopyObjectAsync(profile, bucket, "source", bucket, "new", context, token);
            using var copied = await client.GetObjectAsync(new GetObjectRequest
                { BucketName = bucket, Key = "new" }, token);
            Assert.Equal(0, copied.ContentLength);
            Assert.Equal(0, await copied.ResponseStream.ReadAsync(new byte[1], token));
            Assert.Equal("application/test", copied.Headers.ContentType);
            Assert.Equal("max-age=123", copied.Headers.CacheControl);
            Assert.Equal("attachment", copied.Headers.ContentDisposition);
            Assert.Equal("en", copied.Headers.ContentLanguage);
            Assert.Equal("Tue, 01 Jan 2030 00:00:00 GMT", copied.ExpiresString);
            Assert.Equal("preserved", copied.Metadata["x-amz-meta-custom"]);
            var tags = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest
                { BucketName = bucket, Key = "new" }, token);
            Assert.Equal("empty-copy", Assert.Single(tags.Tagging).Value);

            var replacement = context with
            {
                Options = context.Options with
                {
                    AllowDestinationOverwrite = true, ExpectedDestinationETag = original.ETag
                }
            };
            var changed = await client.PutObjectAsync(new PutObjectRequest
                { BucketName = bucket, Key = "target", ContentBody = "changed" }, token);
            await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.CopyObjectAsync(profile, bucket, "source", bucket, "target", replacement, token));
            unchanged = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = bucket, Key = "target" }, token);
            Assert.Equal(changed.ETag, unchanged.ETag);
            await service.CopyObjectAsync(profile, bucket, "source", bucket, "target", replacement with
            {
                Options = replacement.Options with { ExpectedDestinationETag = changed.ETag }
            }, token);
            Assert.Equal(0, (await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = bucket, Key = "target" }, token)).ContentLength);
            Assert.Equal(0, (await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = bucket, Key = "source" }, token)).ContentLength);
        }
        finally
        {
            foreach (var key in new[] { "source", "target", "new" })
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, CancellationToken.None);
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, CancellationToken.None);
        }
    }

    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task ConfirmedEditUsesAtomicEtagConditionAndNeverFallsBackToOverwrite()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile();
        using var client = new S3ClientFactory().Create(profile);
        var service = new S3StorageService(new S3ClientFactory());
        var bucket = $"s3explorer-edit-{Guid.NewGuid():N}";
        var local = Path.GetTempFileName();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, token);
        try
        {
            await File.WriteAllTextAsync(local, "edited", token);
            var old = await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "key", ContentBody = "old" }, token);
            var current = await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "key", ContentBody = "changed-remotely" }, token);
            var context = new TransferOperationContext(new TransferExecutionOptions { ExpectedDestinationETag = old.ETag },
                new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
            var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.UploadFileAsync(profile, bucket, "key", local, "STANDARD", context, token));
            Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
            Assert.False(failure.Failure.Retryable);
            var retained = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "key" }, token);
            Assert.Equal(current.ETag, retained.ETag);
            await service.UploadFileAsync(profile, bucket, "key", local, "STANDARD",
                context with { Options = context.Options with { ExpectedDestinationETag = current.ETag } }, token);
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "key" }, token);
            using var reader = new StreamReader(response.ResponseStream);
            Assert.Equal("edited", await reader.ReadToEndAsync(token));
        }
        finally
        {
            File.Delete(local);
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "key" }, CancellationToken.None);
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, CancellationToken.None);
        }
    }

    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task NonOverwritePutAndMultipartCompletionPreserveExistingObject()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile();
        using var client = new S3ClientFactory().Create(profile);
        var bucket = $"s3explorer-write-{Guid.NewGuid():N}";
        string? uploadId = null;
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, token);
        try
        {
            var original = await client.PutObjectAsync(new PutObjectRequest
                { BucketName = bucket, Key = "key", ContentBody = "original" }, token);
            var putFailure = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.PutObjectAsync(new PutObjectRequest
                { BucketName = bucket, Key = "key", ContentBody = "replacement", IfNoneMatch = "*" }, token));
            Assert.Equal(HttpStatusCode.PreconditionFailed, putFailure.StatusCode);
            var initiated = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
                { BucketName = bucket, Key = "key" }, token);
            uploadId = initiated.UploadId;
            using var partBytes = new MemoryStream(new byte[] { 1, 2, 3 });
            var part = await client.UploadPartAsync(new UploadPartRequest
                { BucketName = bucket, Key = "key", UploadId = uploadId, PartNumber = 1, InputStream = partBytes, PartSize = 3 }, token);
            var completeFailure = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                { BucketName = bucket, Key = "key", UploadId = uploadId, PartETags = [new PartETag(1, part.ETag)], IfNoneMatch = "*" }, token));
            Assert.Equal(HttpStatusCode.PreconditionFailed, completeFailure.StatusCode);
            var after = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = "key" }, token);
            Assert.Equal(original.ETag, after.ETag);
        }
        finally
        {
            if (uploadId is not null)
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = "key", UploadId = uploadId }, CancellationToken.None);
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "key" }, CancellationToken.None);
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, CancellationToken.None);
        }
    }
}
