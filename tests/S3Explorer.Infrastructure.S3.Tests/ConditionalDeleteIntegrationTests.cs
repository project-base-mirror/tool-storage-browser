using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConditionalDeleteIntegrationTests
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task MinioIgnoredDeleteConditionAndProductMoveRespectDestinationPolicy()
    {
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile();
        using var client = new S3ClientFactory().Create(profile);
        var bucket = $"s3explorer-condition-{Guid.NewGuid():N}";
        var token = TestContext.Current.CancellationToken;
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, token);
        try
        {
            var old = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "key", ContentBody = "old"
            }, token);
            var current = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "key", ContentBody = "new-content"
            }, token);
            // Characterize the pinned provider, not just the SDK request object's fields.
            var request = new DeleteObjectRequest { BucketName = bucket, Key = "key", IfMatch = old.ETag };
            var marshalled = Amazon.S3.Model.Internal.MarshallTransformations.DeleteObjectRequestMarshaller.Instance.Marshall(request);
            Assert.Equal(old.ETag, marshalled.Headers["If-Match"]);
            await client.DeleteObjectAsync(request, token);
            var missing = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucket, Key = "key" }, token));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // A create-only move must preserve both objects on a destination conflict.
            // Ordinary compatible-provider moves are supported with a source recheck;
            // they do not claim that MinIO implements conditional deletion.
            current = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "key", ContentBody = "new-content"
            }, token);
            var destination = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "destination", ContentBody = "keep-destination"
            }, token);
            var service = new S3StorageService(new S3ClientFactory());
            var createOnly = new TransferOperationContext(
                new TransferExecutionOptions { AllowDestinationOverwrite = false, RetainFailedTransferState = false },
                new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
            var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.MoveObjectAsync(profile, bucket, "key", bucket, "destination", createOnly, token));
            Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
            Assert.False(failure.Failure.Retryable);
            var sourceAfter = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket, Key = "key"
            }, token);
            var destinationAfter = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket, Key = "destination"
            }, token);
            Assert.Equal(current.ETag, sourceAfter.ETag);
            Assert.Equal(destination.ETag, destinationAfter.ETag);

            // The ordinary overwrite overload must copy the content successfully before
            // removing the source. Concurrent-writer protection is not asserted here.
            await service.MoveObjectAsync(profile, bucket, "key", bucket, "destination", token);
            using var moved = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket, Key = "destination"
            }, token);
            using var reader = new StreamReader(moved.ResponseStream);
            Assert.Equal("new-content", await reader.ReadToEndAsync(token));
            var movedSource = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucket, Key = "key" }, token));
            Assert.Equal(HttpStatusCode.NotFound, movedSource.StatusCode);
        }
        finally
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "key" }, CancellationToken.None);
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "destination" }, CancellationToken.None);
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, CancellationToken.None);
        }
    }
}
