using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ProviderCapabilityGateTests
{
    private readonly S3StorageService _storage = new(new S3ClientFactory());

    [Fact]
    public async Task UnsupportedObjectAclIsRejectedBeforeNetworkAccess()
    {
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            _storage.PutObjectAclAsync(
                Profile(S3ServiceType.AliyunOss),
                "bucket",
                "object.txt",
                ObjectAclMode.Private,
                TestContext.Current.CancellationToken));

        Assert.Contains("尚未纳入持续验证", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedVersionListingIsRejectedBeforeNetworkAccess()
    {
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            _storage.ListObjectVersionsAsync(
                Profile(S3ServiceType.CloudflareR2),
                "bucket",
                string.Empty,
                null,
                null,
                100,
                TestContext.Current.CancellationToken));

        Assert.Contains("尚未验证", exception.Message, StringComparison.Ordinal);
    }

    private static ConnectionProfile Profile(S3ServiceType type) =>
        ConnectionProfile.CreatePreset(type) with
        {
            Name = "capability-gate",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key"
        };
}
