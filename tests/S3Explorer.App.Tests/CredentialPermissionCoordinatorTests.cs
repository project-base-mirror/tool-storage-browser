using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class CredentialPermissionCoordinatorTests
{
    [Fact]
    public async Task GenericHttpControlCheckDoesNotSendCredentialToContentEndpoint()
    {
        var credential = Credential();
        var profile = new CdnProfile
        {
            Name = "generic-cdn",
            BaseUrl = "https://cdn.example.com/",
            PurgeEndpointTemplate = "https://control.example.com/purge?url={url}",
            ControlCredentialId = credential.Id
        };
        var coordinator = new CredentialPermissionCoordinator(null!);

        var report = await coordinator.CheckAsync(
            credential,
            [],
            new CdnConfiguration([profile], []),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Results);
        Assert.Equal(
            PermissionCheckState.Indeterminate,
            Assert.Single(result.Checks, check => check.Name == "ControlEndpoint").State);
        Assert.Equal(
            PermissionCheckState.Indeterminate,
            Assert.Single(result.Checks, check => check.Name == "Purge").State);
    }

    [Fact]
    public async Task UnassociatedCredentialIsReportedAsSkippedWithoutNetworkCall()
    {
        var credential = Credential();
        var coordinator = new CredentialPermissionCoordinator(null!);

        var report = await coordinator.CheckAsync(
            credential,
            [],
            CdnConfiguration.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCheckState.Skipped, Assert.Single(report.Results).Checks.Single().State);
    }

    private static CredentialProfile Credential() => new()
    {
        Name = "generic-token",
        Provider = CredentialProviderKind.GenericHttp,
        Kind = CredentialKind.BearerToken,
        Secret = "test-secret"
    };

}
