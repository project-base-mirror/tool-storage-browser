using S3Explorer.Core;
using Xunit;
using Xunit.Abstractions;

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

    [Fact]
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Copy_and_move_support_same_and_cross_bucket_with_special_keys()
    {
        var previous = Environment.GetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER");
        Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", "minio");
        try
        {
            var configuration = ProviderMatrixCase.Selected().Resolve();
            if (!configuration.IsConfigured)
            {
                _output.WriteLine("MinIO copy/move integration test is not configured.");
                return;
            }

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
}
