using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using Xunit;

namespace S3Explorer.Infrastructure.Cdn.Tests;

public sealed class TencentCdnProviderTests
{
    private static CredentialProfile Credential() => new()
    {
        Name = "tencent",
        Provider = CredentialProviderKind.TencentCloud,
        Kind = CredentialKind.AccessKeyPair,
        AccessKeyId = "sid",
        Secret = "skey",
        SessionToken = "token"
    };

    private static CdnProviderRequest Request(CdnJobAction action, IReadOnlyList<Uri> urls) => new(
        action,
        new CdnProfile { ProviderId = CdnProfile.TencentCloudProviderId, BaseUrl = "https://cdn.example.com" },
        Credential(),
        urls);

    [Fact]
    public async Task PurgeBatchesAtMostOneThousandAndDeduplicatesUrls()
    {
        var fake = new FakeClient();
        var result = await new TencentCdnProvider(new FakeFactory(fake)).SubmitAsync(
            Request(CdnJobAction.PurgeUrl, Enumerable.Range(0, 1001)
                .Select(value => new Uri($"https://cdn.example.com/{value % 1000}"))
                .ToArray()), TestContext.Current.CancellationToken);

        Assert.Equal(CdnProviderOperationState.Accepted, result.State);
        Assert.Equal([1000], fake.Purges.Select(value => value.Count));
        Assert.Equal("purge-1", result.ProviderTaskId);
    }

    [Fact]
    public async Task PushBatchesAtMostFiveHundred()
    {
        var fake = new FakeClient();
        var result = await new TencentCdnProvider(new FakeFactory(fake)).SubmitAsync(
            Request(CdnJobAction.Warmup, Enumerable.Range(0, 501)
                .Select(value => new Uri($"https://cdn.example.com/{value}"))
                .ToArray()), TestContext.Current.CancellationToken);

        Assert.Equal(CdnProviderOperationState.Accepted, result.State);
        Assert.Equal([500, 1], fake.Pushes.Select(value => value.Count));
        Assert.Equal("push-1,push-2", result.ProviderTaskId);
    }

    [Fact]
    public async Task PermissionProbeRequiresExactDomainAndMarksMutationsOptional()
    {
        var fake = new FakeClient { Domains = ["cdn.example.com"] };
        var checks = await new TencentCdnProvider(new FakeFactory(fake)).CheckControlPermissionsAsync(
            new CdnProfile { ProviderId = CdnProfile.TencentCloudProviderId, BaseUrl = "https://cdn.example.com/path" },
            Credential(), TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCheckState.Passed, checks[0].State);
        Assert.Equal("cdn.example.com", fake.DescribedDomain);
        Assert.All(checks.Skip(1), check => Assert.False(check.Required));
    }

    [Fact]
    public async Task QueryMapsTencentStatuses()
    {
        var fake = new FakeClient { PurgeTasks = new Dictionary<string, TencentCdnTaskResult> { ["a"] = new("done"), ["b"] = new("process") } };
        var provider = new TencentCdnProvider(new FakeFactory(fake));
        var result = await provider.QueryAsync(Request(CdnJobAction.PurgeUrl, [new Uri("https://cdn.example.com/a")]) with { ProviderTaskId = "a,b" }, TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Accepted, result.State);
        fake.PurgeTasks["b"] = new("fail");
        result = await provider.QueryAsync(Request(CdnJobAction.PurgeUrl, [new Uri("https://cdn.example.com/a")]) with { ProviderTaskId = "a,b" }, TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Failed, result.State);
        fake.PurgeTasks["b"] = new("unexpected");
        result = await provider.QueryAsync(Request(CdnJobAction.PurgeUrl, [new Uri("https://cdn.example.com/a")]) with { ProviderTaskId = "a,b" }, TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Failed, result.State);
    }

    [Fact]
    public async Task QueryRejectsPurgeThenWarmup()
    {
        var provider = new TencentCdnProvider(new FakeFactory(new FakeClient()));
        var result = await provider.QueryAsync(
            Request(CdnJobAction.PurgeThenWarmup, [new Uri("https://cdn.example.com/a")]) with { ProviderTaskId = "task-1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(CdnProviderOperationState.Failed, result.State);
        Assert.False(result.Retryable);
        Assert.Contains("PurgeThenWarmup", result.Message);
    }

    [Fact]
    public async Task CredentialErrorsDoNotExposeSecrets()
    {
        var result = await new TencentCdnProvider(new FakeFactory(new FakeClient())).SubmitAsync(
            Request(CdnJobAction.PurgeUrl, [new Uri("https://cdn.example.com/a")]) with { ControlCredential = Credential() with { Provider = CredentialProviderKind.AmazonWebServices } },
            TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Failed, result.State);
        Assert.DoesNotContain("skey", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeFactory(FakeClient client) : ITencentCdnClientFactory
    {
        public ITencentCdnClient Create(TencentCdnCredential credential) => client;
    }

    private sealed class FakeClient : ITencentCdnClient
    {
        public List<IReadOnlyList<string>> Purges { get; } = [];
        public List<IReadOnlyList<string>> Pushes { get; } = [];
        public IReadOnlyList<string> Domains { get; init; } = [];
        public string DescribedDomain { get; private set; } = "";
        public Dictionary<string, TencentCdnTaskResult> PurgeTasks { get; init; } = [];

        public Task<TencentCdnDomainResult> DescribeDomainsAsync(string domainName, CancellationToken _)
        {
            DescribedDomain = domainName;
            return Task.FromResult(new TencentCdnDomainResult(Domains));
        }

        public Task<string> PurgeUrlsAsync(IReadOnlyList<string> urls, CancellationToken _)
        {
            Purges.Add(urls);
            return Task.FromResult($"purge-{Purges.Count}");
        }

        public Task<string> PushUrlsAsync(IReadOnlyList<string> urls, CancellationToken _)
        {
            Pushes.Add(urls);
            return Task.FromResult($"push-{Pushes.Count}");
        }

        public Task<TencentCdnTaskResult> QueryPurgeAsync(string taskId, CancellationToken _) =>
            Task.FromResult(PurgeTasks.TryGetValue(taskId, out var value) ? value : new TencentCdnTaskResult("unknown"));

        public Task<TencentCdnTaskResult> QueryPushAsync(string taskId, CancellationToken _) =>
            Task.FromResult(new TencentCdnTaskResult("done"));
    }
}
