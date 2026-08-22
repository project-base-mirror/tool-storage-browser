using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class PermissionCommandTests
{
    [Fact]
    public async Task GenericHttpPermissionCheckNeverUsesContentEndpointToInferControlAuthentication()
    {
        var result = await Program.CheckCdnPermissionAsync(
            new CdnProfile
            {
                Name = "generic",
                BaseUrl = "https://cdn.example.test/",
                PurgeEndpointTemplate = "https://control.example.test/purge?url={url}"
            },
            null,
            TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(result.Checks, value => value.Name == "ControlEndpoint");
        var purge = Assert.Single(result.Checks, value => value.Name == "Purge");
        Assert.Equal("cdn-control", endpoint.Subject);
        Assert.Equal(PermissionCheckState.Indeterminate, endpoint.State);
        Assert.True(endpoint.Required);
        Assert.False(purge.Required);
    }

    [Fact]
    public async Task GenericHttpWithoutControlEndpointIsExplicitlyNotCheckable()
    {
        var result = await Program.CheckCdnPermissionAsync(
            new CdnProfile
            {
                Name = "content-only",
                BaseUrl = "https://cdn.example.test/"
            },
            null,
            TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(result.Checks, value => value.Name == "ControlEndpoint");
        Assert.Equal(PermissionCheckState.Indeterminate, endpoint.State);
        Assert.True(endpoint.Required);
        Assert.Contains("没有可执行的控制面检查", endpoint.Message);
    }
}
