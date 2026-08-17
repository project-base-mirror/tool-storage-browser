using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class PermissionCommandTests
{
    [Theory]
    [InlineData(403, PermissionCheckState.Denied)]
    [InlineData(404, PermissionCheckState.Indeterminate)]
    [InlineData(200, PermissionCheckState.Indeterminate)]
    [InlineData(500, PermissionCheckState.Indeterminate)]
    public async Task GenericHttpSeparatesEndpointReachabilityFromUnprovableAuthentication(
        int statusCode,
        PermissionCheckState expectedAuthentication)
    {
        var result = await Program.CheckCdnPermissionAsync(
            new CdnProfile
            {
                Name = "generic",
                BaseUrl = "https://cdn.example.test/"
            },
            null,
            new StubDeliveryService(statusCode),
            TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(result.Checks, value => value.Name == "DeliveryEndpoint");
        var authentication = Assert.Single(result.Checks, value => value.Name == "Authentication");
        Assert.False(endpoint.Required);
        Assert.Equal(statusCode >= 500 ? PermissionCheckState.Indeterminate : PermissionCheckState.Passed, endpoint.State);
        Assert.Equal(expectedAuthentication, authentication.State);
        Assert.True(authentication.Required);
    }

    private sealed class StubDeliveryService(int statusCode) : ICdnDeliveryService
    {
        public Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken) => Task.FromResult(new CdnProbeResult(
                url,
                url,
                statusCode,
                "test",
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                null,
                null,
                string.Empty,
                new Dictionary<string, string>()));

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
