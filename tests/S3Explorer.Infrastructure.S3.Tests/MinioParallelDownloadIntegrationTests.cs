using System.Security.Cryptography;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioParallelDownloadIntegrationTests(ITestOutputHelper output)
{
    [ConfiguredProviderFact("minio")]
    [Trait("Category", "Integration")]
    public async Task Real_ranges_resume_confirmed_segments_and_match_source_hash()
    {
        var token = TestContext.Current.CancellationToken;
        var profile = ProviderMatrixCase.All.Single(item => item.Id == "minio").Resolve().CreateProfile();
        var service = new S3StorageService(new S3ClientFactory());
        var bucket = $"s3explorer-ranges-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "s3-range-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.bin");
        var targetPath = Path.Combine(root, "target.bin");
        var created = false;
        try
        {
            await using (var source = File.Create(sourcePath))
            {
                var buffer = new byte[1024 * 1024];
                for (var block = 0; block < 128; block++)
                {
                    Array.Fill(buffer, (byte)block);
                    await source.WriteAsync(buffer, token);
                }
            }
            await service.CreateBucketAsync(profile, bucket, profile.Region, token);
            created = true;
            var options = new TransferExecutionOptions { MultipartThresholdBytes = 5L * 1024 * 1024, MultipartConcurrency = 2 };
            var context = new TransferOperationContext(options, new SharedTransferBandwidthLimiter(), null, null,
                _ => { }, (_, _, _, _) => Task.CompletedTask);
            await service.UploadFileAsync(profile, bucket, "ranges.bin", sourcePath, "STANDARD", context, token);

            DownloadCheckpoint? saved = null;
            using var pause = CancellationTokenSource.CreateLinkedTokenSource(token);
            var first = context with
            {
                UpdateCheckpointAsync = (_, checkpoint, _, _) =>
                {
                    saved = checkpoint;
                    if (checkpoint!.CompletedBytes > 0 && checkpoint.CompletedBytes < checkpoint.RemoteLength)
                        pause.Cancel();
                    return Task.CompletedTask;
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadFileAsync(
                profile, bucket, "ranges.bin", targetPath, first, pause.Token));
            Assert.NotNull(saved);
            Assert.Equal(32L * 1024 * 1024, saved.SegmentSizeBytes);
            // Both already in-flight requests may durably finish before cancellation is
            // observed. Four segments ensure this still exercises an incomplete resume.
            Assert.Equal(128L * 1024 * 1024, saved.RemoteLength);
            Assert.InRange(saved.CompletedSegments.Count, 1, options.MultipartConcurrency);
            Assert.Equal(saved.CompletedSegments.Count * saved.SegmentSizeBytes, saved.CompletedBytes);
            saved.Validate();
            Assert.True(saved.CompletedBytes < saved.RemoteLength);
            Assert.False(File.Exists(targetPath));
            var resumedWatch = System.Diagnostics.Stopwatch.StartNew();
            await service.DownloadFileAsync(profile, bucket, "ranges.bin", targetPath,
                context with { DownloadCheckpoint = saved }, token);
            output.WriteLine($"MinIO parallel resume: objectBytes={saved.RemoteLength}; confirmedBeforeResume={saved.CompletedBytes}; segmentBytes={saved.SegmentSizeBytes}; resumeElapsedMs={resumedWatch.ElapsedMilliseconds}; managedHeapBytes={GC.GetTotalMemory(false)}.");
            await using var sourceStream = File.OpenRead(sourcePath);
            await using var targetStream = File.OpenRead(targetPath);
            Assert.Equal(await SHA256.HashDataAsync(sourceStream, token), await SHA256.HashDataAsync(targetStream, token));
            Assert.False(File.Exists(ResumableDownloadFile.TemporaryPath(targetPath)));
        }
        finally
        {
            if (created)
            {
                await service.DeleteObjectsAsync(profile, bucket, ["ranges.bin"], token);
                await service.DeleteEmptyBucketAsync(profile, bucket, token);
            }
            Directory.Delete(root, true);
        }
    }
}
