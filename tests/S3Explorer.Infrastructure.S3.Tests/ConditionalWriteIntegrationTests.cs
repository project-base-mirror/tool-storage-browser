using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConditionalWriteIntegrationTests
{
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
