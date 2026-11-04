using System.Text;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioExactKeyIdentityIntegrationTests
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task LeadingSlashAndAdjacentSlashKeysRemainDistinctAcrossStorageOperations()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile()
            with { EnableMultipartCopy = true };
        var factory = new S3ClientFactory();
        using var client = factory.Create(profile);
        var service = new S3StorageService(factory);
        var bucket = $"s3explorer-exact-key-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "s3-exact-key-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new TransferOperationContext(new TransferExecutionOptions
        {
            RetainFailedTransferState = false
        }, new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
        var createOnly = context with { Options = context.Options with { AllowDestinationOverwrite = false } };
        const string leadingSource = "/leading//file";
        const string plainSource = "leading//file";
        const string leadingCopy = "/copy//file";
        const string plainCopy = "copy//file";
        const string leadingProtectedCopy = "/protected//file";
        const string plainProtectedCopy = "protected//file";
        string[] allKeys = [leadingSource, plainSource, leadingCopy, plainCopy, leadingProtectedCopy, plainProtectedCopy];
        var leadingBytes = Encoding.UTF8.GetBytes("leading-slash object: keep this exact identity");
        var plainBytes = Encoding.UTF8.GetBytes("plain object");
        var created = false;
        try
        {
            await service.CreateBucketAsync(profile, bucket, profile.Region, token);
            created = true;
            var leadingFile = Path.Combine(root, "leading-source.bin");
            var plainFile = Path.Combine(root, "plain-source.bin");
            await File.WriteAllBytesAsync(leadingFile, leadingBytes, token);
            await File.WriteAllBytesAsync(plainFile, plainBytes, token);
            await service.UploadFileAsync(profile, bucket, leadingSource, leadingFile, "STANDARD", context, token);
            await service.UploadFileAsync(profile, bucket, plainSource, plainFile, "STANDARD", context, token);

            // This assertion cannot pass if both SDK requests were normalized to one key.
            var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, token);
            Assert.Equal(new[] { leadingSource, plainSource }.Order(StringComparer.Ordinal),
                (listed.S3Objects ?? []).Select(item => item.Key).Order(StringComparer.Ordinal));
            var leadingListing = await service.ListObjectsAsync(profile, bucket, "/leading//", null, 100, token);
            Assert.Equal(leadingSource, Assert.Single(leadingListing.Items).Key);
            var plainListing = await service.ListObjectsAsync(profile, bucket, "leading//", null, 100, token);
            Assert.Equal(plainSource, Assert.Single(plainListing.Items).Key);
            await VerifyBytesAsync(leadingSource, leadingBytes);
            await VerifyBytesAsync(plainSource, plainBytes);

            // Exercise ordinary CopyObject and its x-amz-copy-source header separately
            // from the protected MinIO multipart-copy route below.
            await service.CopyObjectAsync(profile, bucket, plainSource, bucket, plainCopy, token);
            await service.CopyObjectAsync(profile, bucket, leadingSource, bucket, leadingCopy, token);
            await VerifyBytesAsync(leadingCopy, leadingBytes);
            await VerifyBytesAsync(plainCopy, plainBytes);

            await service.CopyObjectAsync(profile, bucket, plainSource, bucket, plainProtectedCopy, createOnly, token);
            // The plain alias already exists: a create-only write to the leading-slash
            // key must still succeed, without colliding with or changing that alias.
            await service.CopyObjectAsync(profile, bucket, leadingSource, bucket, leadingProtectedCopy, createOnly, token);
            await VerifyBytesAsync(leadingProtectedCopy, leadingBytes);
            await VerifyBytesAsync(plainProtectedCopy, plainBytes);

            // Both destinations exist. A rejected attempt to replace the leading key
            // with plain-source bytes must not touch either destination or either source.
            var conflict = await Assert.ThrowsAsync<TransferExecutionException>(() =>
                service.CopyObjectAsync(profile, bucket, plainSource, bucket, leadingProtectedCopy, createOnly, token));
            Assert.Equal(TransferFailureCategory.Conflict, conflict.Failure.Category);
            Assert.False(conflict.Failure.Retryable);
            await VerifyBytesAsync(leadingProtectedCopy, leadingBytes);
            await VerifyBytesAsync(plainProtectedCopy, plainBytes);
            await VerifyBytesAsync(leadingSource, leadingBytes);
            await VerifyBytesAsync(plainSource, plainBytes);
            listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, token);
            Assert.Equal(allKeys.Order(StringComparer.Ordinal),
                (listed.S3Objects ?? []).Select(item => item.Key).Order(StringComparer.Ordinal));
        }
        finally
        {
            try
            {
                if (created)
                {
                    // Only this test's random bucket is touched; batch XML retains keys
                    // even when a failing object-path implementation cannot address them.
                    await service.DeleteObjectsAsync(profile, bucket, allKeys, CancellationToken.None);
                    await service.DeleteEmptyBucketAsync(profile, bucket, CancellationToken.None);
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        async Task VerifyBytesAsync(string key, byte[] expected)
        {
            var properties = await service.GetObjectPropertiesAsync(profile, bucket, key, token);
            Assert.Equal(key, properties.Key);
            Assert.Equal(expected.LongLength, properties.Size);
            var destination = Path.Combine(root, $"download-{Guid.NewGuid():N}.bin");
            await service.DownloadFileAsync(profile, bucket, key, destination, context, token);
            Assert.Equal(expected, await File.ReadAllBytesAsync(destination, token));
        }
    }
}
