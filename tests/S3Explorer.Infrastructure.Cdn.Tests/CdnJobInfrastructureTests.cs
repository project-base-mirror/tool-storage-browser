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

            await store.SaveAsync(snapshot);
            var text = await File.ReadAllTextAsync(path);
            var loaded = await store.LoadAsync();

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
            CredentialId = credentialId
        };
        var credential = new CdnCredential
        {
            Id = credentialId,
            Name = "token",
            AuthenticationType = CdnAuthenticationType.BearerToken,
            Secret = "secret"
        };
        var provider = new RecordingProvider();
        var executor = new StoreBackedCdnJobExecutor(
            new ConfigurationStore(new CdnConfiguration([profile], [])),
            new CredentialStore([credential]),
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
        Assert.Equal(credential.Id, provider.Request!.Credential?.Id);
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
            CdnCredential? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken)
        {
            calls.Add("warmup");
            return Task.FromResult(new CdnOperationResult(
                true, (int)HttpStatusCode.OK, TimeSpan.Zero, 10, "warmed"));
        }

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken)
        {
            calls.Add("purge");
            return Task.FromResult(new CdnOperationResult(
                true, (int)HttpStatusCode.Accepted, TimeSpan.Zero, 0, "purged"));
        }
    }

    private sealed class ConfigurationStore(CdnConfiguration configuration) : ICdnConfigurationStore
    {
        public Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);
        public Task SaveAsync(CdnConfiguration value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CredentialStore(IReadOnlyList<CdnCredential> credentials) : ICdnCredentialStore
    {
        public Task<IReadOnlyList<CdnCredential>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(credentials);
        public Task SaveAsync(
            IReadOnlyCollection<CdnCredential> value,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
