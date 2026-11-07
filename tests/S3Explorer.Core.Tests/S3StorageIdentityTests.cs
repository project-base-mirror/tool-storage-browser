using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class S3StorageIdentityTests
{
    private static ConnectionProfile Aws(string endpoint, string host = "") =>
        ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with { Endpoint = endpoint, CustomHostHeader = host };

    [Theory]
    [InlineData("https://s3.amazonaws.com", "https://s3.eu-west-1.amazonaws.com", true)]
    [InlineData("https://s3.us-gov-west-1.amazonaws.com", "https://s3.us-gov-east-1.amazonaws.com", true)]
    [InlineData("https://s3.cn-north-1.amazonaws.com.cn", "https://s3.cn-northwest-1.amazonaws.com.cn", true)]
    [InlineData("https://s3.amazonaws.com", "https://s3.cn-north-1.amazonaws.com.cn", false)]
    [InlineData("https://s3.amazonaws.com", "https://s3.us-gov-west-1.amazonaws.com", false)]
    [InlineData("https://s3.example.test", "https://s3.other.test", false)]
    public void AwsAliasesRespectPartitions(string left, string right, bool same) =>
        Assert.Equal(same, S3StorageIdentity.IsSameStorage(Aws(left), Aws(right)));

    [Fact]
    public void DifferentVirtualHostsOnOneProxyAreDifferentStores()
    {
        var left = Aws("https://proxy.example.test", "tenant-a.example.test");
        var right = Aws("https://proxy.example.test", "tenant-b.example.test");
        Assert.False(S3StorageIdentity.IsSameStorage(left, right));
        ObjectTransferPlanner.ValidateDestination(left, "bucket", "folder/", true, right, "bucket", "folder/child/");
    }

    [Fact]
    public void CustomHostMatchingNormalEndpointIsAnAlias()
    {
        var left = Aws("https://proxy.example.test", "s3.amazonaws.com");
        var right = Aws("https://s3.eu-west-1.amazonaws.com");
        Assert.True(S3StorageIdentity.IsSameStorage(left, right));
    }

    [Theory]
    [InlineData("folder/")]
    [InlineData("folder/child/")]
    public void DirectoryCannotCopyToItselfThroughRegionalAlias(string target)
    {
        var left = Aws("https://s3.amazonaws.com");
        var right = Aws("https://s3.eu-west-1.amazonaws.com");
        Assert.Throws<InvalidOperationException>(() => ObjectTransferPlanner.ValidateDestination(
            left, "bucket", "folder/", true, right, "bucket", target));
        ObjectTransferPlanner.ValidateDestination(left, "bucket", "folder/", true, right, "other", target);
    }

    [Fact]
    public void HostPortAndBasePathStillDistinguishNamespaces()
    {
        Assert.False(S3StorageIdentity.IsSameStorage(Aws("https://proxy.test", "tenant.test:8443"), Aws("https://proxy.test", "tenant.test")));
        Assert.False(S3StorageIdentity.IsSameStorage(Aws("https://proxy.test/a", "tenant.test"), Aws("https://proxy.test/b", "tenant.test")));
        Assert.True(S3StorageIdentity.IsSameStorage(Aws("https://proxy.test", "TENANT.TEST:443"), Aws("https://proxy.test", "tenant.test")));
    }
}
