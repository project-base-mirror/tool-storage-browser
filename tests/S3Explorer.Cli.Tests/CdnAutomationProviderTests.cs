using S3Explorer.Contracts;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class CdnAutomationProviderTests
{
    [Fact]
    public async Task AlibabaWarmupUsesControlPlaneProviderInsteadOfEdgeHttpWarmup()
    {
        var credential = new CredentialProfile
        {
            Name = "aliyun-release",
            Provider = CredentialProviderKind.AlibabaCloud,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "test-access-key",
            Secret = "test-secret"
        };
        var profile = new CdnProfile
        {
            Name = "release-cdn",
            ProviderId = CdnProfile.AlibabaCloudProviderId,
            BaseUrl = "https://cdn.example.com/",
            ControlCredentialId = credential.Id
        };
        var provider = new RecordingProvider();
        var delivery = new RejectingDeliveryService();
        var args = CliArguments.Parse(
            ["cdn", "warmup", "--profile", profile.Name, "--path", "assets/a.js"]);

        var result = await AutomationCommands.RunAsync(
            "cdn",
            "warmup",
            args,
            null!,
            null!,
            new CdnStore(profile),
            new CredentialStore(credential),
            delivery,
            jsonOutput: true,
            TestContext.Current.CancellationToken,
            [provider]);

        Assert.Equal(0, result.ExitCode);
        var request = Assert.IsType<CdnProviderRequest>(provider.Request);
        Assert.Equal(CdnJobAction.Warmup, request.Action);
        Assert.Equal(credential.Id, request.ControlCredential?.Id);
        Assert.Equal("https://cdn.example.com/assets/a.js", Assert.Single(request.Urls).AbsoluteUri);
        Assert.False(delivery.Called);
        var batch = Assert.IsType<CdnBatchResult>(result.Data);
        Assert.True(batch.Success);
        Assert.Contains("ProviderTaskId=task-1", Assert.Single(batch.Items).Message, StringComparison.Ordinal);
    }

    private sealed class CdnStore(CdnProfile profile) : ICdnConfigurationStore
    {
        public Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CdnConfiguration([profile], []));

        public Task SaveAsync(CdnConfiguration configuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CredentialStore(CredentialProfile credential) : ICredentialStore
    {
        public Task<IReadOnlyList<CredentialProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CredentialProfile>>([credential]);

        public Task SaveAsync(
            IReadOnlyCollection<CredentialProfile> credentials,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProvider : ICdnProvider
    {
        public string ProviderId => CdnProfile.AlibabaCloudProviderId;
        public CdnCapabilities Capabilities => CdnCapabilities.Warmup;
        public CdnProviderRequest? Request { get; private set; }

        public Task<CdnProviderResult> SubmitAsync(
            CdnProviderRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new CdnProviderResult(
                CdnProviderOperationState.Accepted,
                "已接受。",
                ProviderTaskId: "task-1"));
        }

        public Task<CdnProviderResult> QueryAsync(
            CdnProviderRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RejectingDeliveryService : ICdnDeliveryService
    {
        public bool Called { get; private set; }

        public Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("Edge delivery must not run for Alibaba control-plane warmup.");
        }

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            Uri url,
            CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("Edge delivery must not run for Alibaba control-plane warmup.");
        }

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CredentialProfile? controlCredential,
            Uri url,
            CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("Edge delivery must not run for Alibaba control-plane warmup.");
        }
    }
}
