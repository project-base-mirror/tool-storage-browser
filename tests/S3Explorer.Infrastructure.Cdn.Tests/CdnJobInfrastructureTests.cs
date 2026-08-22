using System.Net;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using Xunit;

namespace S3Explorer.Infrastructure.Cdn.Tests;

public sealed class CdnJobInfrastructureTests
{
    [Fact]
    public async Task JobStoreRoundTripsAndUsesStringEnums()
    {
        var path = TemporaryFile();
        try
        {
            var now = DateTimeOffset.Parse("2026-07-28T00:00:00Z");
            var job = new CdnJobRecord
            {
                IdempotencyKey = "manual:1",
                CdnProfileId = Guid.NewGuid(),
                Action = CdnJobAction.PurgeThenWarmup,
                State = CdnJobState.Pending,
                Urls = ["https://cdn.example/a.js"],
                CreatedAt = now,
                UpdatedAt = now
            };
            var snapshot = new CdnJobStoreSnapshot
            {
                AutomationStartedAt = now,
                Jobs = [job]
            };
            var store = new JsonCdnJobStore(path, () => now);

            await store.SaveAsync(snapshot, TestContext.Current.CancellationToken);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Contains("\"purgeThenWarmup\"", text, StringComparison.Ordinal);
            var loadedJob = Assert.Single(loaded.Jobs);
            Assert.Equal(job with { Urls = loadedJob.Urls }, loadedJob);
            Assert.Equal(job.Urls, loadedJob.Urls);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task GenericProviderRunsPurgeBeforeWarmup()
    {
        var calls = new List<string>();
        var provider = new GenericHttpCdnProvider(new RecordingDelivery(calls));
        var profile = new CdnProfile
        {
            Name = "site",
            BaseUrl = "https://cdn.example",
            PurgeEndpointTemplate = "https://api.example/purge?url={url}"
        };
        var request = new CdnProviderRequest(
            CdnJobAction.PurgeThenWarmup,
            profile,
            null,
            [new Uri("https://cdn.example/a.js")]);

        var result = await provider.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(CdnProviderOperationState.Completed, result.State);
        Assert.Equal(["purge", "warmup"], calls);
    }

    [Fact]
    public async Task StoreBackedExecutorResolvesProfileCredentialAndProvider()
    {
        var credentialId = Guid.NewGuid();
        var profile = new CdnProfile
        {
            Name = "site",
            BaseUrl = "https://cdn.example",
            ControlCredentialId = credentialId
        };
        var credential = new CredentialProfile
        {
            Id = credentialId,
            Name = "token",
            Provider = CredentialProviderKind.GenericHttp,
            Kind = CredentialKind.BearerToken,
            Secret = "secret"
        };
        var provider = new RecordingProvider();
        var executor = new StoreBackedCdnJobExecutor(
            new ConfigurationStore(new CdnConfiguration([profile], []), [credential]),
            [provider]);
        var job = new CdnJobRecord
        {
            IdempotencyKey = "manual:test",
            CdnProfileId = profile.Id,
            Action = CdnJobAction.Warmup,
            Urls = ["https://cdn.example/a.js"]
        };

        var result = await executor.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(CdnProviderOperationState.Completed, result.State);
        Assert.NotNull(provider.Request);
        Assert.Equal(credential.Id, provider.Request!.ControlCredential?.Id);
    }

    private static string TemporaryFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "s3explorer-cdn-job-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "cdn-jobs.json");
    }

    private static void DeleteDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private sealed class RecordingDelivery(List<string> calls) : ICdnDeliveryService
    {
        public Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            Uri url,
            CancellationToken cancellationToken)
        {
            calls.Add("warmup");
            return Task.FromResult(new CdnOperationResult(
                true, (int)HttpStatusCode.OK, TimeSpan.Zero, 10, "warmed"));
        }

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CredentialProfile? controlCredential,
            Uri url,
            CancellationToken cancellationToken)
        {
            calls.Add("purge");
            return Task.FromResult(new CdnOperationResult(
                true, (int)HttpStatusCode.Accepted, TimeSpan.Zero, 0, "purged"));
        }
    }

    private sealed class ConfigurationStore(
        CdnConfiguration cdnConfiguration,
        IReadOnlyList<CredentialProfile> credentials) : IExplorerConfigurationStore
    {
        private ExplorerConfiguration _configuration = new(
            ConnectionProfileConfiguration.Empty,
            cdnConfiguration,
            credentials);

        public Task<ExplorerConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_configuration);

        public Task SaveAsync(
            ExplorerConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            _configuration = configuration;
            return Task.CompletedTask;
        }

        public Task<ExplorerConfiguration> UpdateAsync(
            Func<ExplorerConfiguration, ExplorerConfiguration> update,
            CancellationToken cancellationToken = default)
        {
            _configuration = update(_configuration);
            return Task.FromResult(_configuration);
        }
    }

    private sealed class RecordingProvider : ICdnProvider
    {
        public string ProviderId => CdnProfile.GenericHttpProviderId;
        public CdnCapabilities Capabilities => CdnCapabilities.Warmup;
        public CdnProviderRequest? Request { get; private set; }

        public Task<CdnProviderResult> SubmitAsync(
            CdnProviderRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new CdnProviderResult(
                CdnProviderOperationState.Completed,
                "done"));
        }

        public Task<CdnProviderResult> QueryAsync(
            CdnProviderRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
