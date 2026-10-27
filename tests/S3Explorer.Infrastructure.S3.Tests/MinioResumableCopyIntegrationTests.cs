using System.Security.Cryptography;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioResumableCopyIntegrationTests
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task ParallelCopyResumesRemotePartsAndPreservesWholeObject()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile();
        var service = new S3StorageService(new S3ClientFactory());
        var bucket = $"s3explorer-copy-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "s3-copy-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.bin");
        var target = Path.Combine(root, "target.bin");
        var created = false;
        MultipartUploadCheckpoint? saved = null;
        try
        {
            await using (var stream = File.Create(source))
            {
                var buffer = new byte[1024 * 1024];
                for (var index = 0; index < 20; index++)
                {
                    Array.Fill(buffer, (byte)index);
                    await stream.WriteAsync(buffer, token);
                }
            }
            await service.CreateBucketAsync(profile, bucket, profile.Region, token);
            created = true;
            var context = new TransferOperationContext(new TransferExecutionOptions
            {
                MultipartThresholdBytes = 5L * 1024 * 1024,
                PartSizeBytes = 5L * 1024 * 1024,
                MultipartConcurrency = 2
            }, new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
            await service.UploadFileAsync(profile, bucket, "source", source, "STANDARD", context, token);
            using var pause = CancellationTokenSource.CreateLinkedTokenSource(token);
            var first = context with { UpdateCheckpointAsync = (_, _, checkpoint, _) =>
            {
                saved = checkpoint;
                if (checkpoint!.CompletedParts.Count > 0) pause.Cancel();
                return Task.CompletedTask;
            }};
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.CopyObjectAsync(profile, bucket, "source", bucket, "target", first, pause.Token));
            Assert.NotNull(saved);
            Assert.InRange(saved.CompletedParts.Count, 1, 2);
            Assert.False(saved.CopyCompleted);
            var uploadId = saved.UploadId;
            await service.CopyObjectAsync(profile, bucket, "source", bucket, "target", context with
            {
                MultipartCheckpoint = saved,
                UpdateCheckpointAsync = (_, _, checkpoint, _) => { saved = checkpoint; return Task.CompletedTask; }
            }, token);
            Assert.Equal(uploadId, saved.UploadId);
            Assert.True(saved.CopyCompleted);
            Assert.Equal(4, saved.CompletedParts.Count);
            await service.DownloadFileAsync(profile, bucket, "target", target, context, token);
            await using var before = File.OpenRead(source);
            await using var after = File.OpenRead(target);
            Assert.Equal(await SHA256.HashDataAsync(before, token), await SHA256.HashDataAsync(after, token));
        }
        finally
        {
            if (created)
            {
                if (saved is { CopyCompleted: false })
                    await service.AbortMultipartUploadAsync(profile, bucket, "target", saved.UploadId, CancellationToken.None);
                await service.DeleteObjectsAsync(profile, bucket, ["source", "target"], CancellationToken.None);
                await service.DeleteEmptyBucketAsync(profile, bucket, CancellationToken.None);
            }
            Directory.Delete(root, true);
        }
    }
}
