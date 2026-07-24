using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;
using Xunit.Abstractions;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ProviderMatrixIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ProviderMatrixIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Configured_provider_runs_compatibility_matrix_and_cleans_resources()
    {
        var configuration = ProviderMatrixCase.Selected().Resolve();
        if (!configuration.IsConfigured)
        {
            _output.WriteLine(configuration.ToReportJson(
                ProviderMatrixStatus.NotConfigured,
                "Required endpoint/access-key/secret-key environment variables are not configured."));
            return;
        }

        var service = new S3StorageService(new S3ClientFactory());
        var profile = configuration.CreateProfile();
        var bucket = string.IsNullOrWhiteSpace(configuration.KnownBucket)
            ? $"s3explorer-matrix-{Guid.NewGuid():N}"
            : configuration.KnownBucket;
        var ownsBucket = string.IsNullOrWhiteSpace(configuration.KnownBucket);
        var source = Path.GetTempFileName();
        var multipartSource = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.multipart");
        var target = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.download");
        var prefix = $"matrix-{Guid.NewGuid():N}/";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string Key(string suffix) => prefix + suffix;

        try
        {
            await File.WriteAllTextAsync(source, "S3 Explorer provider matrix");
            await using (var stream = new FileStream(multipartSource, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(20L * 1024 * 1024);

            if (ownsBucket)
                await service.CreateBucketAsync(profile, bucket, profile.Region, CancellationToken.None);

            var connection = await service.TestConnectionAsync(profile, CancellationToken.None);
            Assert.True(connection.Success, connection.Message);

            var specialKey = Key("folder/中文 space+%.txt");
            keys.Add(specialKey);
            await service.UploadFileAsync(profile, bucket, specialKey, source, "STANDARD", null, CancellationToken.None);

            var page = await service.ListObjectsAsync(profile, bucket, prefix + "folder/", null, 1, CancellationToken.None);
            var allItems = new List<S3ObjectEntry>(page.Items);
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            while (page.HasMore)
            {
                Assert.False(string.IsNullOrWhiteSpace(page.ContinuationToken));
                Assert.True(seenTokens.Add(page.ContinuationToken!));
                page = await service.ListObjectsAsync(
                    profile, bucket, prefix + "folder/", page.ContinuationToken, 1, CancellationToken.None);
                allItems.AddRange(page.Items);
            }
            Assert.Contains(allItems, item => item.Key == specialKey);

            var properties = await service.GetObjectPropertiesAsync(profile, bucket, specialKey, CancellationToken.None);
            Assert.Equal("S3 Explorer provider matrix".Length, properties.Size);

            var url = service.CreatePresignedUrl(profile, bucket, specialKey, TimeSpan.FromMinutes(5));
            Assert.StartsWith("http", url, StringComparison.OrdinalIgnoreCase);

            await service.DownloadFileAsync(profile, bucket, specialKey, target, null, CancellationToken.None);
            Assert.Equal("S3 Explorer provider matrix", await File.ReadAllTextAsync(target));

            var copyKey = Key("folder/copy.txt");
            var movedKey = Key("folder/moved.txt");
            keys.Add(copyKey);
            keys.Add(movedKey);
            await service.CopyObjectAsync(profile, bucket, specialKey, bucket, copyKey, CancellationToken.None);
            await service.MoveObjectAsync(profile, bucket, copyKey, bucket, movedKey, CancellationToken.None);
            keys.Remove(copyKey);

            var singleDeleteKey = Key("folder/single-delete.txt");
            keys.Add(singleDeleteKey);
            await service.UploadFileAsync(
                profile with { EnableMultiObjectDelete = false },
                bucket,
                singleDeleteKey,
                source,
                "STANDARD",
                null,
                CancellationToken.None);
            await service.DeleteObjectsAsync(
                profile with { EnableMultiObjectDelete = false },
                bucket,
                [singleDeleteKey],
                CancellationToken.None);
            keys.Remove(singleDeleteKey);

            var multipartKey = Key("multipart/large.bin");
            keys.Add(multipartKey);
            await service.UploadFileAsync(
                profile, bucket, multipartKey, multipartSource, "STANDARD", null, CancellationToken.None);
            var multipartProperties = await service.GetObjectPropertiesAsync(
                profile, bucket, multipartKey, CancellationToken.None);
            Assert.Equal(new FileInfo(multipartSource).Length, multipartProperties.Size);

            await service.DeleteObjectsAsync(profile, bucket, keys.ToArray(), CancellationToken.None);
            keys.Clear();
            _output.WriteLine(configuration.ToReportJson(ProviderMatrixStatus.Passed, "Compatibility matrix completed."));
        }
        catch (Exception exception)
        {
            _output.WriteLine(configuration.ToReportJson(ProviderMatrixStatus.Failed, exception.GetType().Name));
            throw;
        }
        finally
        {
            try
            {
                if (keys.Count > 0)
                    await service.DeleteObjectsAsync(profile, bucket, keys, CancellationToken.None);
            }
            catch
            {
                // Continue with multipart and bucket cleanup; the primary failure remains authoritative.
            }

            try
            {
                using var client = new S3ClientFactory().Create(profile);
                string? keyMarker = null;
                string? uploadIdMarker = null;
                do
                {
                    var uploads = await client.ListMultipartUploadsAsync(
                        new ListMultipartUploadsRequest
                        {
                            BucketName = bucket,
                            Prefix = prefix,
                            KeyMarker = keyMarker,
                            UploadIdMarker = uploadIdMarker
                        },
                        CancellationToken.None);
                    foreach (var upload in uploads.MultipartUploads)
                    {
                        await client.AbortMultipartUploadAsync(
                            new AbortMultipartUploadRequest
                            {
                                BucketName = bucket,
                                Key = upload.Key,
                                UploadId = upload.UploadId
                            },
                            CancellationToken.None);
                    }

                    keyMarker = uploads.IsTruncated ? uploads.NextKeyMarker : null;
                    uploadIdMarker = uploads.IsTruncated ? uploads.NextUploadIdMarker : null;
                } while (!string.IsNullOrEmpty(keyMarker));
            }
            catch
            {
                // The primary test failure remains authoritative.
            }

            if (ownsBucket)
            {
                try { await service.DeleteEmptyBucketAsync(profile, bucket, CancellationToken.None); }
                catch { }
            }

            File.Delete(source);
            File.Delete(multipartSource);
            File.Delete(target);
        }
    }
}
