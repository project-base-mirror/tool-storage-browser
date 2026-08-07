using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MinioIntegrationTests(ITestOutputHelper output) => _output = output;

    private static TransferOperationContext CreateTransferContext()
    {
        var limiter = new SharedTransferBandwidthLimiter();
        limiter.Configure(0, 0);
        return new TransferOperationContext(
            new TransferExecutionOptions(),
            limiter,
            null,
            null,
            _ => { },
            (_, _, _, _) => Task.CompletedTask);
    }

    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task Crud_flow_runs_against_explicit_test_instance()
    {
        var previous = Environment.GetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", "minio");
            await new ProviderMatrixIntegrationTests(_output)
                .Configured_provider_runs_compatibility_matrix_and_cleans_resources();
        }
        finally
        {
            Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", previous);
        }
    }

    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task Copy_and_move_support_same_and_cross_bucket_with_special_keys()
    {
        var previous = Environment.GetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER");
        Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", "minio");
        try
        {
            var configuration = ProviderMatrixCase.Selected().Resolve();

            var service = new S3StorageService(new S3ClientFactory());
            var profile = configuration.CreateProfile();
            var sourceBucket = $"s3explorer-v040-src-{Guid.NewGuid():N}";
            var destinationBucket = $"s3explorer-v040-dst-{Guid.NewGuid():N}";
            var sourceFile = Path.GetTempFileName();
            const string sourceKey = "源 目录/中文 + 100%.txt";
            const string sameBucketCopyKey = "目标 目录/同桶 + 100%.txt";
            const string crossBucketCopyKey = "跨桶 目录/复制 + 100%.txt";
            const string movedBackKey = "移动 目录/完成 + 100%.txt";
            var sourceBucketCreated = false;
            var destinationBucketCreated = false;

            try
            {
                await File.WriteAllTextAsync(sourceFile, "S3 Explorer 0.4.0 MinIO copy and move");
                await service.CreateBucketAsync(profile, sourceBucket, profile.Region, CancellationToken.None);
                sourceBucketCreated = true;
                await service.CreateBucketAsync(profile, destinationBucket, profile.Region, CancellationToken.None);
                destinationBucketCreated = true;

                await service.UploadFileAsync(
                    profile, sourceBucket, sourceKey, sourceFile, "STANDARD",
                    CreateTransferContext(), CancellationToken.None);
                Assert.True(await service.ObjectExistsAsync(
                    profile, sourceBucket, sourceKey, CancellationToken.None));
                Assert.False(await service.ObjectExistsAsync(
                    profile, sourceBucket, sameBucketCopyKey, CancellationToken.None));

                await service.CopyObjectAsync(
                    profile, sourceBucket, sourceKey,
                    sourceBucket, sameBucketCopyKey, CancellationToken.None);
                Assert.True(await service.ObjectExistsAsync(
                    profile, sourceBucket, sourceKey, CancellationToken.None));
                Assert.True(await service.ObjectExistsAsync(
                    profile, sourceBucket, sameBucketCopyKey, CancellationToken.None));

                await service.CopyObjectAsync(
                    profile, sourceBucket, sourceKey,
                    destinationBucket, crossBucketCopyKey, CancellationToken.None);
                Assert.True(await service.ObjectExistsAsync(
                    profile, destinationBucket, crossBucketCopyKey, CancellationToken.None));

                await service.MoveObjectAsync(
                    profile, destinationBucket, crossBucketCopyKey,
                    sourceBucket, movedBackKey, CancellationToken.None);
                Assert.False(await service.ObjectExistsAsync(
                    profile, destinationBucket, crossBucketCopyKey, CancellationToken.None));
                Assert.True(await service.ObjectExistsAsync(
                    profile, sourceBucket, movedBackKey, CancellationToken.None));
                Assert.True(await service.ObjectExistsAsync(
                    profile, sourceBucket, sourceKey, CancellationToken.None));
            }
            finally
            {
                try
                {
                    if (sourceBucketCreated)
                    {
                        await service.DeleteObjectsAsync(
                            profile, sourceBucket,
                            [sourceKey, sameBucketCopyKey, movedBackKey],
                            CancellationToken.None);
                    }
                }
                catch { }

                try
                {
                    if (destinationBucketCreated)
                    {
                        await service.DeleteObjectsAsync(
                            profile, destinationBucket, [crossBucketCopyKey],
                            CancellationToken.None);
                    }
                }
                catch { }

                if (destinationBucketCreated)
                {
                    try { await service.DeleteEmptyBucketAsync(profile, destinationBucket, CancellationToken.None); }
                    catch { }
                }
                if (sourceBucketCreated)
                {
                    try { await service.DeleteEmptyBucketAsync(profile, sourceBucket, CancellationToken.None); }
                    catch { }
                }
                File.Delete(sourceFile);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", previous);
        }
    }

    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task Bucket_management_policy_acl_scan_and_safe_empty_runs_against_minio()
    {
        var previous = Environment.GetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER");
        Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", "minio");
        var bucket = $"s3explorer-v041-{Guid.NewGuid():N}";
        var localFile = Path.GetTempFileName();
        var historicalDownload = Path.Combine(Path.GetTempPath(), $"s3explorer-history-{Guid.NewGuid():N}.txt");
        var bucketCreated = false;
        S3StorageService? service = null;
        ConnectionProfile? profile = null;

        try
        {
            var configuration = ProviderMatrixCase.Selected().Resolve();

            service = new S3StorageService(new S3ClientFactory());
            profile = configuration.CreateProfile();
            await service.CreateBucketAsync(profile, bucket, profile.Region, CancellationToken.None);
            bucketCreated = true;

            Assert.Null(await service.GetBucketPolicyAsync(profile, bucket, CancellationToken.None));
            var policy = $$"""
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Sid": "ListBucket",
                  "Effect": "Allow",
                  "Principal": "*",
                  "Action": ["s3:ListBucket"],
                  "Resource": ["arn:aws:s3:::{{bucket}}"]
                }
              ]
            }
            """;
            await service.PutBucketPolicyAsync(profile, bucket, policy, CancellationToken.None);
            var savedPolicy = await service.GetBucketPolicyAsync(profile, bucket, CancellationToken.None);
            Assert.NotNull(savedPolicy);
            Assert.Contains(bucket, savedPolicy, StringComparison.Ordinal);

            var properties = await service.GetBucketPropertiesAsync(profile, bucket, CancellationToken.None);
            Assert.True(properties.HasPolicy);
            Assert.Equal(S3ServiceType.MinIO, properties.ServiceType);
            Assert.False(properties.Capabilities.PublicAccessBlock.Supported);
            Assert.False(properties.Capabilities.ObjectOwnership.Supported);
            Assert.False(properties.Capabilities.Cors.Supported);
            Assert.True(properties.Capabilities.Encryption.Supported);
            Assert.Contains("服务端必须", properties.Capabilities.Encryption.Reason, StringComparison.Ordinal);
            Assert.True(properties.Capabilities.Lifecycle.Supported);
            Assert.False(properties.Capabilities.LifecycleStorageTransitions.Supported);
            Assert.False(properties.Capabilities.LifecycleMultipartCleanup.Supported);
            Assert.False(properties.Capabilities.ObjectLock.Supported);

            await service.PutBucketVersioningAsync(
                profile, bucket, BucketVersioningState.Enabled, CancellationToken.None);
            Assert.Equal(BucketVersioningState.Enabled,
                await service.GetBucketVersioningAsync(profile, bucket, CancellationToken.None));

            var lifecycle = new BucketLifecycleConfiguration([
                new BucketLifecycleRule(
                    "integration-cleanup", true, "objects/", [], [], 3650, [], 3650, null)
            ]);
            await service.PutBucketLifecycleAsync(profile, bucket, lifecycle, CancellationToken.None);
            Assert.True(BucketLifecycleDocument.AreSemanticallyEquivalent(
                lifecycle,
                await service.GetBucketLifecycleAsync(profile, bucket, CancellationToken.None),
                storageTransitionsSupported: false,
                multipartCleanupSupported: false));
            await Assert.ThrowsAsync<ArgumentException>(() => service.PutBucketLifecycleAsync(
                profile,
                bucket,
                new BucketLifecycleConfiguration([
                    new BucketLifecycleRule(
                        "unsupported-multipart", true, null, [], [], null, [], null, 7)
                ]),
                CancellationToken.None));
            await service.DeleteBucketLifecycleAsync(profile, bucket, CancellationToken.None);
            Assert.Empty((await service.GetBucketLifecycleAsync(
                profile, bucket, CancellationToken.None)).Rules);

            await service.PutBucketTagsAsync(profile, bucket,
                [new BucketTag("environment", "integration")], CancellationToken.None);
            var tags = await service.GetBucketTagsAsync(profile, bucket, CancellationToken.None);
            Assert.Contains(tags, tag => tag.Key == "environment" && tag.Value == "integration");

            await service.PutBucketAclAsync(profile, bucket, BucketAclMode.Private, CancellationToken.None);
            var acl = await service.GetBucketAclAsync(profile, bucket, CancellationToken.None);
            Assert.Equal(BucketAclMode.Private, acl.Mode);

            await File.WriteAllTextAsync(localFile, "first version");
            await service.UploadFileAsync(
                profile, bucket, "objects/中文 + 100%.txt", localFile, "STANDARD",
                CreateTransferContext(), CancellationToken.None);
            await File.WriteAllTextAsync(localFile, "second version");
            await service.UploadFileAsync(
                profile, bucket, "objects/中文 + 100%.txt", localFile, "STANDARD",
                CreateTransferContext(), CancellationToken.None);

            var versionPage = await service.ListObjectVersionsAsync(
                profile, bucket, "objects/中文 + 100%.txt", null, null, 100,
                CancellationToken.None);
            var historical = Assert.Single(versionPage.Items, item =>
                !item.IsDeleteMarker && !item.IsLatest);
            await service.DownloadObjectVersionAsync(
                profile, bucket, historical.Key, historical.VersionId, historicalDownload,
                CreateTransferContext(), CancellationToken.None);
            Assert.Equal("first version", await File.ReadAllTextAsync(historicalDownload));

            await service.RestoreObjectVersionAsync(
                profile, bucket, historical.Key, historical.VersionId, CancellationToken.None);
            var restoredPage = await service.ListObjectVersionsAsync(
                profile, bucket, historical.Key, null, null, 100, CancellationToken.None);
            Assert.True(restoredPage.Items.Count(item => !item.IsDeleteMarker) >= 3);
            await service.DeleteObjectVersionAsync(
                profile, bucket, historical.Key, historical.VersionId, CancellationToken.None);
            var afterVersionDelete = await service.ListObjectVersionsAsync(
                profile, bucket, historical.Key, null, null, 100, CancellationToken.None);
            Assert.DoesNotContain(afterVersionDelete.Items, item => item.VersionId == historical.VersionId);

            await service.DeleteObjectsAsync(
                profile, bucket, [historical.Key], CancellationToken.None);
            var withMarker = await service.ListObjectVersionsAsync(
                profile, bucket, historical.Key, null, null, 100, CancellationToken.None);
            var markers = withMarker.Items.Where(item => item.IsDeleteMarker)
                .Select(item => new ObjectVersionIdentity(item.Key, item.VersionId)).ToArray();
            Assert.NotEmpty(markers);
            await service.DeleteObjectVersionsAsync(profile, bucket, markers, CancellationToken.None);
            var withoutMarker = await service.ListObjectVersionsAsync(
                profile, bucket, historical.Key, null, null, 100, CancellationToken.None);
            Assert.DoesNotContain(withoutMarker.Items, item => item.IsDeleteMarker);

            using (var client = new S3ClientFactory().Create(profile))
            {
                await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = "unfinished/中文 + 100%.bin"
                }, CancellationToken.None);
            }

            var summary = await service.ScanBucketAsync(profile, bucket, CancellationToken.None);
            Assert.True(summary.ObjectCount >= 1);
            Assert.True(summary.MultipartUploadCount >= 1);

            var emptied = await service.EmptyBucketAsync(profile, bucket, CancellationToken.None);
            Assert.True(emptied.DeletedObjects + emptied.DeletedVersions >= 1);
            Assert.True(emptied.AbortedMultipartUploads >= 1);
            Assert.True((await service.ScanBucketAsync(profile, bucket, CancellationToken.None)).IsEmpty);

            await service.DeleteBucketPolicyAsync(profile, bucket, CancellationToken.None);
            await service.DeleteBucketTagsAsync(profile, bucket, CancellationToken.None);
            Assert.Null(await service.GetBucketPolicyAsync(profile, bucket, CancellationToken.None));
            await service.DeleteEmptyBucketAsync(profile, bucket, CancellationToken.None);
            bucketCreated = false;
        }
        finally
        {
            if (service is not null && profile is not null && bucketCreated)
            {
                try { await service.DeleteBucketPolicyAsync(profile, bucket, CancellationToken.None); } catch { }
                try { await service.DeleteBucketTagsAsync(profile, bucket, CancellationToken.None); } catch { }
                try { await service.DeleteBucketLifecycleAsync(profile, bucket, CancellationToken.None); } catch { }
                try { await service.EmptyBucketAsync(profile, bucket, CancellationToken.None); } catch { }
                try { await service.DeleteEmptyBucketAsync(profile, bucket, CancellationToken.None); } catch { }
            }
            File.Delete(localFile);
            File.Delete(historicalDownload);
            Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", previous);
        }
    }
}
