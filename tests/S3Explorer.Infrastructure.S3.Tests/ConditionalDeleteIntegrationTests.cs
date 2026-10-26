using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConditionalDeleteIntegrationTests
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task ConditionalDeleteMismatchPreservesCurrentObject()
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
            var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() => client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucket, Key = "key", IfMatch = old.ETag }, token));
            Assert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
            var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket, Key = "key"
            }, token);
            Assert.Equal(current.ETag, metadata.ETag);
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = "key", IfMatch = current.ETag
            }, token);
        }
        finally
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "key" }, CancellationToken.None);
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, CancellationToken.None);
        }
    }
}
