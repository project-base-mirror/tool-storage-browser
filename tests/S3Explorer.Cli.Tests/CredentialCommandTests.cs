using System.Text.Json;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class CredentialCommandTests
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    [Fact]
    public async Task CredentialListAndShowHideSecretsInJsonAndTextOutput()
    {
        var credential = new CredentialProfile
        {
            Name = "release-key",
            Provider = CredentialProviderKind.AmazonWebServices,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_TEST",
            Secret = "secret-json-hidden",
            SessionToken = "session-json-hidden"
        };
        var store = new InMemoryExplorerConfigurationStore(new ExplorerConfiguration(
            ConnectionProfileConfiguration.Empty,
            CdnConfiguration.Empty,
            [credential]));

        var jsonListOutput = await RunCredentialAsyncCaptureOutputAsync(
            ["credential", "list", "--output", "json"],
            store,
            json: true);
        var listDocument = JsonDocument.Parse(jsonListOutput).RootElement;
        var listData = listDocument.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, listData.ValueKind);
        var listed = listData.EnumerateArray().Single();
        Assert.False(listed.TryGetProperty("secret", out _));
        Assert.False(listed.TryGetProperty("sessionToken", out _));
        Assert.DoesNotContain("secret-json-hidden", jsonListOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("session-json-hidden", jsonListOutput, StringComparison.Ordinal);

        var jsonShowOutput = await RunCredentialAsyncCaptureOutputAsync(
            ["credential", "show", "release-key", "--output", "json"],
            store,
            json: true);
        var showData = JsonDocument.Parse(jsonShowOutput).RootElement.GetProperty("data");
        Assert.False(showData.TryGetProperty("secret", out _));
        Assert.False(showData.TryGetProperty("sessionToken", out _));
        Assert.DoesNotContain("secret-json-hidden", jsonShowOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("session-json-hidden", jsonShowOutput, StringComparison.Ordinal);

        var textListOutput = await RunCredentialAsyncCaptureOutputAsync(
            ["credential", "list"],
            store,
            json: false);
        Assert.DoesNotContain("secret-json-hidden", textListOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("session-json-hidden", textListOutput, StringComparison.Ordinal);

        var textShowOutput = await RunCredentialAsyncCaptureOutputAsync(
            ["credential", "show", "release-key"],
            store,
            json: false);
        Assert.DoesNotContain("secret-json-hidden", textShowOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("session-json-hidden", textShowOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialAddOnlyReadsSecretFromEnvironmentAndWritesUnifiedConfiguration()
    {
        var secretEnvironmentVariable = "S3EX_TEST_CRED_SECRET";
        var sessionEnvironmentVariable = "S3EX_TEST_CRED_SESSION";
        var originalSecret = Environment.GetEnvironmentVariable(secretEnvironmentVariable);
        var originalSession = Environment.GetEnvironmentVariable(sessionEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(secretEnvironmentVariable, "env-secret-value");
            Environment.SetEnvironmentVariable(sessionEnvironmentVariable, "env-session-value");

            var store = new InMemoryExplorerConfigurationStore(new ExplorerConfiguration(
                ConnectionProfileConfiguration.Empty,
                CdnConfiguration.Empty,
                []));

            var output = await RunCredentialAsyncCaptureOutputAsync(
                [
                    "credential", "add",
                    "--name", "env-add",
                    "--provider", "aws",
                    "--kind", "access-key-pair",
                    "--access-key-id", "AKIA_ENV_TEST",
                    "--secret-env", secretEnvironmentVariable,
                    "--session-token-env", sessionEnvironmentVariable
                ],
                store,
                json: true);
            Assert.Contains("env-add", output, StringComparison.Ordinal);
            var configuration = await store.LoadAsync(TestContext.Current.CancellationToken);
            var saved = Assert.Single(configuration.CredentialVault);
            Assert.Equal("env-add", saved.Name);
            Assert.Equal("env-secret-value", saved.Secret);
            Assert.Equal("env-session-value", saved.SessionToken);
            Assert.Equal(CredentialProviderKind.AmazonWebServices, saved.Provider);
            Assert.Equal(CredentialKind.AccessKeyPair, saved.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretEnvironmentVariable, originalSecret);
            Environment.SetEnvironmentVariable(sessionEnvironmentVariable, originalSession);
        }
    }

    [Fact]
    public async Task CredentialDeleteRefusesStorageAndCdnReferencedCredentials()
    {
        var credential = new CredentialProfile
        {
            Name = "shared",
            Provider = CredentialProviderKind.AmazonWebServices,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_SHARED",
            Secret = "shared-secret",
            SessionToken = "shared-session"
        };
        var storageProfile = new ConnectionProfile
        {
            Name = "storage",
            ServiceType = S3ServiceType.AmazonS3,
            CredentialSource = CredentialSourceKind.StoredKeys,
            CredentialId = credential.Id,
            Endpoint = "https://s3.example.com",
            Region = "us-east-1"
        };
        var cdnProfile = new CdnProfile
        {
            Name = "cdn",
            ProviderId = CdnProfile.GenericHttpProviderId,
            BaseUrl = "https://cdn.example.com",
            ControlCredentialId = credential.Id
        };
        var store = new InMemoryExplorerConfigurationStore(new ExplorerConfiguration(
            new ConnectionProfileConfiguration([storageProfile], []),
            new CdnConfiguration([cdnProfile], []),
            [credential]));

        var exception = await Assert.ThrowsAsync<CliUsageException>(
            async () =>
            {
                await RunCredentialAsyncCaptureOutputAsync(
                    ["credential", "delete", "shared", "--yes"],
                    store,
                    json: false);
            });
        Assert.Contains("对象存储：", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CDN：", exception.Message, StringComparison.Ordinal);
        var configuration = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(configuration.CredentialVault);
    }

    [Fact]
    public async Task CredentialDeleteRemovesUnreferencedCredential()
    {
        var credential = new CredentialProfile
        {
            Name = "orphan",
            Provider = CredentialProviderKind.AmazonWebServices,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_ORPHAN",
            Secret = "orphan-secret"
        };
        var store = new InMemoryExplorerConfigurationStore(new ExplorerConfiguration(
            ConnectionProfileConfiguration.Empty,
            CdnConfiguration.Empty,
            [credential]));

        await RunCredentialAsyncCaptureOutputAsync(
            ["credential", "delete", "orphan", "--yes"],
            store,
            json: true);

        var configuration = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(configuration.CredentialVault);
    }

    [Fact]
    public async Task CredentialAddUsesAtomicUpdateAndPreservesConcurrentStorageCdnChanges()
    {
        var baseCredential = new CredentialProfile
        {
            Name = "base",
            Provider = CredentialProviderKind.AmazonWebServices,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_BASE",
            Secret = "base-secret"
        };
        var cdnCredential = new CredentialProfile
        {
            Name = "cdn-token",
            Provider = CredentialProviderKind.GenericHttp,
            Kind = CredentialKind.BearerToken,
            Secret = "cdn-secret"
        };
        var storageProfile = new ConnectionProfile
        {
            Name = "storage",
            ServiceType = S3ServiceType.AmazonS3,
            CredentialSource = CredentialSourceKind.StoredKeys,
            CredentialId = baseCredential.Id,
            Endpoint = "https://s3.example.com",
            Region = "us-east-1"
        };
        var cdnProfile = new CdnProfile
        {
            Name = "cdn",
            ProviderId = CdnProfile.GenericHttpProviderId,
            BaseUrl = "https://cdn.example.com",
            ControlCredentialId = cdnCredential.Id
        };
        var secretEnvironmentVariable = "S3EX_TEST_CRED_SECRET_ATOMIC";
        var originalSecret = Environment.GetEnvironmentVariable(secretEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(secretEnvironmentVariable, "atomic-secret");
            var store = new InMemoryExplorerConfigurationStore(new ExplorerConfiguration(
                ConnectionProfileConfiguration.Empty,
                CdnConfiguration.Empty,
                [baseCredential, cdnCredential]));
            store.UpdateMutation = current =>
            {
                return current with
                {
                    Storage = new ConnectionProfileConfiguration([storageProfile], []),
                    Cdn = new CdnConfiguration([cdnProfile], [])
                };
            };

            await RunCredentialAsyncCaptureOutputAsync(
                [
                    "credential", "add",
                    "--name", "new-credential",
                    "--provider", "aws",
                    "--kind", "access-key-pair",
                    "--access-key-id", "AKIA_ATOMIC_TEST",
                    "--secret-env", secretEnvironmentVariable
                ],
                store,
                json: true);

            var configuration = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.True(store.UpdateCalled);
            Assert.Equal(0, store.SaveCalledCount);
            Assert.Collection(configuration.Storage.Profiles, item => Assert.Equal("storage", item.Name));
            Assert.Collection(configuration.Cdn.Profiles, item => Assert.Equal("cdn", item.Name));
            Assert.Equal(3, configuration.CredentialVault.Count);
            var names = configuration.CredentialVault.Select(credential => credential.Name).ToHashSet();
            Assert.Equal(3, names.Count);
            Assert.Contains("base", names);
            Assert.Contains("cdn-token", names);
            Assert.Contains("new-credential", names);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretEnvironmentVariable, originalSecret);
        }
    }

    [Fact]
    public async Task CredentialDeleteIsAtomicAndBlocksDeletionWhenConcurrentReferencesAppear()
    {
        var target = new CredentialProfile
        {
            Name = "target",
            Provider = CredentialProviderKind.AmazonWebServices,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_TARGET",
            Secret = "target-secret"
        };
        var cdnCredential = new CredentialProfile
        {
            Name = "cdn-aliyun",
            Provider = CredentialProviderKind.AlibabaCloud,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "AKIA_CDN",
            Secret = "cdn-secret"
        };
        var store = new InMemoryExplorerConfigurationStore(new ExplorerConfiguration(
            ConnectionProfileConfiguration.Empty,
            CdnConfiguration.Empty,
            [target, cdnCredential]));
        store.UpdateMutation = current =>
        {
            var storageProfile = new ConnectionProfile
            {
                Name = "concurrent-storage",
                ServiceType = S3ServiceType.AmazonS3,
                CredentialSource = CredentialSourceKind.StoredKeys,
                CredentialId = target.Id,
                Endpoint = "https://s3.example.com",
                Region = "us-east-1"
            };
            var cdnProfile = new CdnProfile
            {
                Name = "concurrent-cdn",
                ProviderId = CdnProfile.AlibabaCloudProviderId,
                BaseUrl = "https://cdn.example.com",
                ControlCredentialId = cdnCredential.Id
            };
            return current with
            {
                Storage = new ConnectionProfileConfiguration([storageProfile], []),
                Cdn = new CdnConfiguration([cdnProfile], [])
            };
        };

        var exception = await Assert.ThrowsAsync<CliUsageException>(async () =>
        {
            await RunCredentialAsyncCaptureOutputAsync(
                ["credential", "delete", "target", "--yes"],
                store,
                json: false);
        });
        Assert.Contains("凭据仍被引用", exception.Message, StringComparison.Ordinal);
        Assert.True(store.UpdateCalled);
        Assert.Equal(0, store.SaveCalledCount);

        var configuration = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, configuration.CredentialVault.Count);
        Assert.Contains(configuration.CredentialVault, credential => credential.Name == "target");
        Assert.Collection(configuration.Storage.Profiles, item => Assert.Equal("concurrent-storage", item.Name));
        Assert.Collection(configuration.Cdn.Profiles, item => Assert.Equal("concurrent-cdn", item.Name));
    }

    private static async Task<string> RunCredentialAsyncCaptureOutputAsync(
        string[] args,
        IExplorerConfigurationStore store,
        bool json)
    {
        await ConsoleGate.WaitAsync(TestContext.Current.CancellationToken);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(TextWriter.Null);
            var parsed = CliArguments.Parse(args);
            var exitCode = await Program.RunCredentialAsync(
                parsed.Positionals.Count > 1 ? parsed.Positionals[1] : string.Empty,
                parsed,
                store,
                json,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            return output.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleGate.Release();
        }
    }

    private sealed class InMemoryExplorerConfigurationStore(ExplorerConfiguration configuration)
        : IExplorerConfigurationStore
    {
        private ExplorerConfiguration _configuration = configuration;
        public bool UpdateCalled { get; private set; }
        public int SaveCalledCount { get; private set; }

        public Func<ExplorerConfiguration, ExplorerConfiguration>? UpdateMutation { get; set; }
        public Func<ExplorerConfiguration, ExplorerConfiguration>? SaveMutation { get; set; }

        public Task<ExplorerConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_configuration);

        public Task SaveAsync(ExplorerConfiguration configuration, CancellationToken cancellationToken = default)
        {
            SaveCalledCount++;
            configuration = SaveMutation?.Invoke(configuration) ?? configuration;
            _configuration = configuration;
            return Task.CompletedTask;
        }

        public Task<ExplorerConfiguration> UpdateAsync(
            Func<ExplorerConfiguration, ExplorerConfiguration> update,
            CancellationToken cancellationToken = default)
        {
            UpdateCalled = true;
            var current = _configuration;
            if (UpdateMutation is not null)
                _configuration = current = UpdateMutation(current);
            _configuration = update(current);
            return Task.FromResult(_configuration);
        }
    }
}
