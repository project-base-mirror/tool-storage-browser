using System.Text;
using S3Explorer.App;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ConfigurationTransactionCoordinatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StartupRecoveryCompletesTransactionInterruptedAfterAnyPartialStoreWrite(int failAfterStep)
    {
        var root = TemporaryDirectory();
        var journalPath = Path.Combine(root, "configuration-transaction.json");
        try
        {
            var previous = Snapshot("old", "old-storage-secret", includeCdn: false);
            var target = Snapshot("new", "new-storage-secret", includeCdn: true);
            var profiles = new MemoryProfileStore(previous.Profiles);
            var configuration = new MemoryCdnConfigurationStore(previous.CdnConfiguration);
            var credentials = new MemoryCdnCredentialStore(previous.CdnCredentials);
            var interrupted = new ConfigurationTransactionCoordinator(
                profiles,
                configuration,
                credentials,
                new TestProtector(),
                journalPath,
                step =>
                {
                    if (step == failAfterStep)
                        throw new ConfigurationTransactionInterruptedException($"after step {step}");
                });

            await Assert.ThrowsAsync<ConfigurationTransactionInterruptedException>(() =>
                interrupted.SaveAsync(previous, target));

            Assert.True(File.Exists(journalPath));
            var journal = await File.ReadAllTextAsync(journalPath);
            Assert.DoesNotContain("old-storage-secret", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("new-storage-secret", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("cdn-secret", journal, StringComparison.Ordinal);

            var recovery = new ConfigurationTransactionCoordinator(
                profiles,
                configuration,
                credentials,
                new TestProtector(),
                journalPath);
            Assert.True(await recovery.RecoverPendingAsync());

            Assert.Equal("new", Assert.Single(profiles.Value).Name);
            Assert.Equal("new-cdn", Assert.Single(configuration.Value.Profiles).Name);
            Assert.Equal("cdn-secret", Assert.Single(credentials.Value).Secret);
            Assert.False(File.Exists(journalPath));
            Assert.False(File.Exists(journalPath + ".bak"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OrdinarySaveFailureRollsEveryStoreBackBeforeReturningError()
    {
        var root = TemporaryDirectory();
        var journalPath = Path.Combine(root, "configuration-transaction.json");
        try
        {
            var previous = Snapshot("old", "old-storage-secret", includeCdn: false);
            var target = Snapshot("new", "new-storage-secret", includeCdn: true);
            var profiles = new MemoryProfileStore(previous.Profiles);
            var configuration = new MemoryCdnConfigurationStore(previous.CdnConfiguration);
            var credentials = new MemoryCdnCredentialStore(previous.CdnCredentials)
            {
                FailNextSave = true
            };
            var coordinator = new ConfigurationTransactionCoordinator(
                profiles,
                configuration,
                credentials,
                new TestProtector(),
                journalPath);

            var error = await Assert.ThrowsAsync<IOException>(() =>
                coordinator.SaveAsync(previous, target));

            Assert.Contains("已恢复保存前配置", error.Message, StringComparison.Ordinal);
            Assert.Equal("old", Assert.Single(profiles.Value).Name);
            Assert.Empty(configuration.Value.Profiles);
            Assert.Empty(credentials.Value);
            Assert.False(File.Exists(journalPath));
            Assert.False(File.Exists(journalPath + ".bak"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoveryWithoutJournalDoesNothing()
    {
        var root = TemporaryDirectory();
        try
        {
            var snapshot = Snapshot("old", "secret", includeCdn: false);
            var coordinator = new ConfigurationTransactionCoordinator(
                new MemoryProfileStore(snapshot.Profiles),
                new MemoryCdnConfigurationStore(snapshot.CdnConfiguration),
                new MemoryCdnCredentialStore(snapshot.CdnCredentials),
                new TestProtector(),
                Path.Combine(root, "configuration-transaction.json"));

            Assert.False(await coordinator.RecoverPendingAsync());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ConfigurationSnapshot Snapshot(string name, string secret, bool includeCdn)
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
        {
            Name = name,
            AccessKey = name + "-access",
            SecretKey = secret
        };
        if (!includeCdn)
            return new ConfigurationSnapshot([profile], CdnConfiguration.Empty, []);

        var credential = new CdnCredential
        {
            Name = "new-cdn-credential",
            AuthenticationType = CdnAuthenticationType.BearerToken,
            Secret = "cdn-secret"
        };
        var cdnProfile = new CdnProfile
        {
            Name = "new-cdn",
            BaseUrl = "https://cdn.example.test",
            CredentialId = credential.Id
        };
        return new ConfigurationSnapshot(
            [profile],
            new CdnConfiguration([cdnProfile], []),
            [credential]);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestProtector : ICredentialProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string ciphertext) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }

    private sealed class MemoryProfileStore(IReadOnlyList<ConnectionProfile> value) : IProfileStore
    {
        public IReadOnlyList<ConnectionProfile> Value { get; private set; } = value;
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Value);

        public Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
        {
            Value = profiles.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryCdnConfigurationStore(CdnConfiguration value) : ICdnConfigurationStore
    {
        public CdnConfiguration Value { get; private set; } = value;
        public Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Value);

        public Task SaveAsync(CdnConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Value = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryCdnCredentialStore(IReadOnlyList<CdnCredential> value) : ICdnCredentialStore
    {
        public IReadOnlyList<CdnCredential> Value { get; private set; } = value;
        public bool FailNextSave { get; init; }
        private bool _failed;

        public Task<IReadOnlyList<CdnCredential>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Value);

        public Task SaveAsync(IReadOnlyCollection<CdnCredential> credentials, CancellationToken cancellationToken = default)
        {
            if (FailNextSave && !_failed)
            {
                _failed = true;
                throw new IOException("Injected credential save failure.");
            }

            Value = credentials.ToArray();
            return Task.CompletedTask;
        }
    }
}
