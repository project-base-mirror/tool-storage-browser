using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Crud_flow_runs_against_explicit_test_instance()
    {
        var endpoint = Environment.GetEnvironmentVariable("S3EXPLORER_TEST_ENDPOINT");
        var accessKey = Environment.GetEnvironmentVariable("S3EXPLORER_TEST_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("S3EXPLORER_TEST_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey))
        {
            // Integration access is opt-in. The test never falls back to a production service.
            return;
        }

        var profile = new ConnectionProfile
        {
            Name = "Integration MinIO",
            ServiceType = S3ServiceType.MinIO,
            Endpoint = endpoint,
            Region = Environment.GetEnvironmentVariable("S3EXPLORER_TEST_REGION") ?? "us-east-1",
            AccessKey = accessKey,
            SecretKey = secretKey,
            AddressingStyle = AddressingStyle.PathStyle,
            UseHttps = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        };
        var service = new S3StorageService(new S3ClientFactory());
        var bucket = $"s3explorer-test-{Guid.NewGuid():N}";
        var source = Path.GetTempFileName();
        var target = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".download");

        try
        {
            await File.WriteAllTextAsync(source, "S3 Explorer integration test");
            await service.CreateBucketAsync(profile, bucket, profile.Region, CancellationToken.None);
            await service.UploadFileAsync(profile, bucket, "folder/source.txt", source, "STANDARD", null, CancellationToken.None);
            var page = await service.ListObjectsAsync(profile, bucket, "folder/", null, 1000, CancellationToken.None);
            Assert.Contains(page.Items, item => item.Key == "folder/source.txt");

            await service.CopyObjectAsync(profile, bucket, "folder/source.txt", bucket, "folder/copy.txt", CancellationToken.None);
            await service.MoveObjectAsync(profile, bucket, "folder/copy.txt", bucket, "folder/moved.txt", CancellationToken.None);
            var properties = await service.GetObjectPropertiesAsync(profile, bucket, "folder/moved.txt", CancellationToken.None);
            Assert.True(properties.Size > 0);

            var url = service.CreatePresignedUrl(profile, bucket, "folder/moved.txt", TimeSpan.FromMinutes(5));
            Assert.StartsWith("http", url, StringComparison.OrdinalIgnoreCase);

            await service.DownloadFileAsync(profile, bucket, "folder/source.txt", target, null, CancellationToken.None);
            Assert.Equal("S3 Explorer integration test", await File.ReadAllTextAsync(target));

            await service.DeleteObjectsAsync(profile, bucket, ["folder/source.txt", "folder/moved.txt"], CancellationToken.None);
            await service.DeleteEmptyBucketAsync(profile, bucket, CancellationToken.None);
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }
}
