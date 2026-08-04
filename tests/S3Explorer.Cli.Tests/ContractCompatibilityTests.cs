using S3Explorer.Contracts;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class ContractCompatibilityTests
{
    [Fact]
    public void Product_patch_versions_are_not_the_contract_boundary()
    {
        var info = Program.CreateCompatibilityInfo();

        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.Equal(1, info.ContractApiVersion);
        Assert.Equal(2, info.ManifestSchemaVersion);
        Assert.True(info.SupportsClient(1, 1));
        Assert.True(info.SupportsClient(1, 2));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    public void Unsupported_contract_or_manifest_versions_are_rejected(int contractVersion, int manifestVersion)
    {
        var info = Program.CreateCompatibilityInfo();

        Assert.False(info.SupportsClient(contractVersion, manifestVersion));
    }
}
