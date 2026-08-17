using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using Xunit;

namespace S3Explorer.Infrastructure.Cdn.Tests;

public sealed class AliyunCdnProviderTests
{
    private static CredentialProfile Credential() => new() { Name = "aliyun", Provider = CredentialProviderKind.AlibabaCloud, Kind = CredentialKind.AccessKeyPair, AccessKeyId = "ak", Secret = "secret" };
    private static CdnProviderRequest Request(CdnJobAction action, IReadOnlyList<Uri> urls) => new(action, new CdnProfile { ProviderId = CdnProfile.AlibabaCloudProviderId, BaseUrl = "https://cdn.example.com" }, Credential(), urls);

    [Fact]
    public async Task RefreshBatchesAtMostOneThousandAndUsesLf()
    {
        var fake = new FakeClient();
        var provider = new AliyunCdnProvider(new FakeFactory(fake));
        var urls = Enumerable.Range(0, 1001).Select(value => new Uri($"https://cdn.example.com/{value}")).ToArray();
        var result = await provider.SubmitAsync(Request(CdnJobAction.PurgeUrl, urls), TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Accepted, result.State);
        Assert.Equal(2, fake.Refreshes.Count);
        Assert.DoesNotContain("\r", fake.Refreshes[0]);
        Assert.Equal("refresh-1,refresh-2", result.ProviderTaskId);
    }

    [Fact]
    public async Task WarmupBatchesAtMostOneHundredAndDeduplicatesTaskIds()
    {
        var fake = new FakeClient { PushTaskId = "same" };
        var provider = new AliyunCdnProvider(new FakeFactory(fake));
        var urls = Enumerable.Range(0, 201).Select(value => new Uri($"https://cdn.example.com/{value}")).ToArray();
        var result = await provider.SubmitAsync(Request(CdnJobAction.Warmup, urls), TestContext.Current.CancellationToken);
        Assert.Equal(3, fake.Pushes.Count);
        Assert.Equal("same", result.ProviderTaskId);
    }

    [Fact]
    public async Task RejectsNonAlibabaCredentialAndPurgeThenWarmup()
    {
        var provider = new AliyunCdnProvider(new FakeFactory(new FakeClient()));
        var bad = Credential() with { Provider = CredentialProviderKind.AmazonWebServices };
        var result = await provider.SubmitAsync(Request(CdnJobAction.PurgeUrl, [new Uri("https://cdn.example.com/a")]) with { Credential = bad }, TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Failed, result.State);
        Assert.Contains("Alibaba", result.Message, StringComparison.OrdinalIgnoreCase);
        result = await provider.SubmitAsync(Request(CdnJobAction.PurgeThenWarmup, [new Uri("https://cdn.example.com/a")]), TestContext.Current.CancellationToken);
        Assert.Contains("两阶段", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAggregatesTerminalAndPendingStatuses()
    {
        var fake = new FakeClient();
        fake.Tasks["a"] = new("Complete"); fake.Tasks["b"] = new("Refreshing");
        var provider = new AliyunCdnProvider(new FakeFactory(fake));
        var request = Request(CdnJobAction.Warmup, [new Uri("https://cdn.example.com/a")]) with { ProviderTaskId = "a,b,a" };
        var result = await provider.QueryAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Accepted, result.State);
        fake.Tasks["b"] = new("Failed");
        result = await provider.QueryAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Failed, result.State);
    }

    [Fact]
    public async Task PermissionProbeRequiresExactDomain()
    {
        var fake = new FakeClient { Domains = ["cdn.example.com"] };
        var provider = new AliyunCdnProvider(new FakeFactory(fake));
        var result = await provider.CheckDomainPermissionAsync(
            new CdnProfile
            {
                ProviderId = CdnProfile.AlibabaCloudProviderId,
                BaseUrl = "https://cdn.example.com/path"
            },
            Credential(),
            TestContext.Current.CancellationToken);
        Assert.Equal(PermissionCheckState.Passed, result.State);
        Assert.Equal("cdn.example.com", fake.DescribedDomain);
    }

    [Fact]
    public async Task ErrorsDoNotExposeCredential()
    {
        var fake = new FakeClient { Error = new InvalidOperationException("secret ak") };
        var provider = new AliyunCdnProvider(new FakeFactory(fake));
        var result = await provider.SubmitAsync(Request(CdnJobAction.PurgeUrl, [new Uri("https://cdn.example.com/a")]), TestContext.Current.CancellationToken);
        Assert.Equal(CdnProviderOperationState.Failed, result.State);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ak", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeFactory(FakeClient client) : IAliyunCdnClientFactory { public IAliyunCdnClient Create(AliyunCredential credential) => client; }
    private sealed class FakeClient : IAliyunCdnClient
    {
        public List<string> Refreshes { get; } = []; public List<string> Pushes { get; } = []; public Dictionary<string, AliyunTaskResult> Tasks { get; } = [];
        public IReadOnlyList<string> Domains { get; init; } = []; public string DescribedDomain { get; private set; } = ""; public Exception? Error { get; init; } public string PushTaskId { get; init; } = "push";
        public Task<string> RefreshAsync(string path, CancellationToken _) { if (Error is not null) throw Error; Refreshes.Add(path); return Task.FromResult($"refresh-{Refreshes.Count}"); }
        public Task<string> PushAsync(string path, CancellationToken _) { Pushes.Add(path); return Task.FromResult(PushTaskId); }
        public Task<AliyunTaskResult> QueryAsync(string id, CancellationToken _) => Task.FromResult(Tasks.TryGetValue(id, out var result) ? result : new("Unknown"));
        public Task<AliyunDomainResult> DescribeDomainsAsync(string domain, CancellationToken _) { DescribedDomain = domain; return Task.FromResult(new AliyunDomainResult(Domains)); }
    }
}
