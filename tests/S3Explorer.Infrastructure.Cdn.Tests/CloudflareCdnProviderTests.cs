using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using Xunit;

namespace S3Explorer.Infrastructure.Cdn.Tests;

public sealed class CloudflareCdnProviderTests
{
    [Fact]
    public async Task PurgeBatchesAtOneHundredAndDoesNotCreateTask()
    {
        var client = new FakeClient { PurgeResult = new(true, 200, null) };
        var provider = new CloudflareCdnProvider(new FakeFactory(client));
        var result = await provider.SubmitAsync(Request(CdnJobAction.PurgeUrl, Enumerable.Range(0, 101).Select(i => new Uri($"https://cdn.example/{i}"))), TestContext.Current.CancellationToken);

        Assert.Equal(CdnProviderOperationState.Completed, result.State);
        Assert.Empty(result.ProviderTaskId);
        Assert.Equal([100, 1], client.PurgeBatches.Select(batch => batch.Count).ToArray());
    }

    [Fact]
    public async Task ZonePermissionAllowsZoneSubdomainAndDoesNotProbePurge()
    {
        var client = new FakeClient { ZoneResult = new(true, 200, null, Name: "example.com") };
        var provider = new CloudflareCdnProvider(new FakeFactory(client));
        var checks = await provider.CheckControlPermissionsAsync(Profile(), Credential(), TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCheckState.Passed, checks.Single(c => c.Name == "ZoneRead").State);
        var purge = checks.Single(c => c.Name == "CachePurge");
        Assert.Equal(PermissionCheckState.Indeterminate, purge.State);
        Assert.False(purge.Required);
        Assert.Empty(client.PurgeBatches);
    }

    [Fact]
    public async Task RejectsMissingZoneOrWrongCredentialWithoutCreatingClientRequest()
    {
        var client = new FakeClient();
        var provider = new CloudflareCdnProvider(new FakeFactory(client));
        var result = await provider.SubmitAsync(Request(CdnJobAction.PurgeUrl, [new Uri("https://cdn.example/a")]) with
        {
            ControlCredential = Credential() with { Provider = CredentialProviderKind.GenericHttp },
            Profile = Profile() with { ControlResourceId = "" }
        }, TestContext.Current.CancellationToken);

        Assert.Equal(CdnProviderOperationState.Failed, result.State);
        Assert.Empty(client.PurgeBatches);
    }

    private static CdnProviderRequest Request(CdnJobAction action, IEnumerable<Uri> urls) =>
        new(action, Profile(), Credential(), urls.ToArray());

    private static CdnProfile Profile() => new()
    {
        ProviderId = CdnProfile.CloudflareProviderId,
        Name = "Cloudflare",
        BaseUrl = "https://cdn.example.com",
        ControlResourceId = "zone-1"
    };

    private static CredentialProfile Credential() => new()
    {
        Provider = CredentialProviderKind.Cloudflare,
        Kind = CredentialKind.BearerToken,
        Secret = "token-not-for-content"
    };

    private sealed class FakeFactory(FakeClient client) : ICloudflareCdnClientFactory
    {
        public ICloudflareCdnClient Create(string bearerToken, string zoneId) => client;
    }

    private sealed class FakeClient : ICloudflareCdnClient
    {
        public CloudflareApiResult ZoneResult { get; init; } = new(true, 200, null, Name: "example.com");
        public CloudflareApiResult PurgeResult { get; init; } = new(true, 200, null);
        public List<IReadOnlyList<Uri>> PurgeBatches { get; } = [];
        public Task<CloudflareApiResult> GetZoneAsync(string zoneId, CancellationToken cancellationToken) => Task.FromResult(ZoneResult);
        public Task<CloudflareApiResult> PurgeAsync(IReadOnlyList<Uri> urls, CancellationToken cancellationToken)
        {
            PurgeBatches.Add(urls);
            return Task.FromResult(PurgeResult);
        }
    }
}
