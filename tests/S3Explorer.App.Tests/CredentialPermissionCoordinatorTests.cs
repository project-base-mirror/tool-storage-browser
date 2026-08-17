using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class CredentialPermissionCoordinatorTests
{
    [Theory]
    [InlineData(403, PermissionCheckState.Denied)]
    [InlineData(404, PermissionCheckState.Indeterminate)]
    [InlineData(200, PermissionCheckState.Indeterminate)]
    public async Task GenericHttpOnlyTreatsExplicitAuthorizationFailuresAsDenied(
        int statusCode,
        PermissionCheckState expectedAuthenticationState)
    {
        var credential = Credential();
        var profile = new CdnProfile
        {
            Name = "generic-cdn",
            BaseUrl = "https://cdn.example.com/",
            CredentialId = credential.Id
        };
        var coordinator = new CredentialPermissionCoordinator(
            null!,
            new ProbeDeliveryService(statusCode));

        var report = await coordinator.CheckAsync(
            credential,
            [],
            new CdnConfiguration([profile], []),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Results);
        Assert.Equal(
            expectedAuthenticationState,
            Assert.Single(result.Checks, check => check.Name == "Authentication").State);
        Assert.False(Assert.Single(result.Checks, check => check.Name == "DeliveryEndpoint").Required);
    }

    [Fact]
    public async Task UnassociatedCredentialIsReportedAsSkippedWithoutNetworkCall()
    {
        var credential = Credential();
        var delivery = new ProbeDeliveryService(200);
        var coordinator = new CredentialPermissionCoordinator(null!, delivery);

        var report = await coordinator.CheckAsync(
            credential,
            [],
            CdnConfiguration.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCheckState.Skipped, Assert.Single(report.Results).Checks.Single().State);
        Assert.False(delivery.Called);
    }

    private static CredentialProfile Credential() => new()
    {
        Name = "generic-token",
        Provider = CredentialProviderKind.GenericHttp,
        Kind = CredentialKind.BearerToken,
        Secret = "test-secret"
    };

    private sealed class ProbeDeliveryService(int statusCode) : ICdnDeliveryService
    {
        public bool Called { get; private set; }

        public Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new CdnProbeResult(
                url,
                url,
                statusCode,
                string.Empty,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                null,
                string.Empty,
                new Dictionary<string, string>()));
        }

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CredentialProfile? credential,
            Uri url,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
