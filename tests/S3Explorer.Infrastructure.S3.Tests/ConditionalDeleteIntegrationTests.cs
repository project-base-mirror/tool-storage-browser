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
    public async Task MinioIgnoredConditionIsDetectedAndProductMovePreservesSource()
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

            // Product path must not send this unsafe delete (nor overwrite the destination).
            current = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "key", ContentBody = "new-content"
            }, token);
            var destination = await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = "destination", ContentBody = "keep-destination"
            }, token);
            var service = new S3StorageService(new S3ClientFactory());
            var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.MoveObjectAsync(profile, bucket, "key", bucket, "destination", token));
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
        }
        finally
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "key" }, CancellationToken.None);
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "destination" }, CancellationToken.None);
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, CancellationToken.None);
        }
    }
}
