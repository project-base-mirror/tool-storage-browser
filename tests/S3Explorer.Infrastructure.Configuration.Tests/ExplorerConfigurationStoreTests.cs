using System.Runtime.Versioning;
using System.Text.Json;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using S3Explorer.Infrastructure.Configuration;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.Configuration.Tests;

[SupportedOSPlatform("windows")]
public sealed class ExplorerConfigurationStoreTests
{
    [Fact]
    public async Task EmptyConfigurationCreatesEncryptedCanonicalFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        Assert.Empty((await store.LoadAsync(cancellationToken)).Storage.Profiles);
        var text = await File.ReadAllTextAsync(store.Path, cancellationToken);
        Assert.DoesNotContain("SecretKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("encryptedPayload", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsWithoutPlaintext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp(); var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        var id = Guid.NewGuid();
        var credential = new CredentialProfile { Id = id, Name = "key", Provider = CredentialProviderKind.AmazonWebServices, Kind = CredentialKind.AccessKeyPair, AccessKeyId = "ak", Secret = "sk" };
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with { Name = "s3", CredentialId = id, AccessKey = "ak", SecretKey = "sk" };
        await store.SaveAsync(new ExplorerConfiguration(new ConnectionProfileConfiguration([profile], []), CdnConfiguration.Empty, [credential]), cancellationToken);
        var text = await File.ReadAllTextAsync(store.Path, cancellationToken);
        Assert.DoesNotContain("sk", text, StringComparison.Ordinal);
        Assert.Equal("sk", (await store.LoadAsync(cancellationToken)).CredentialVault[0].Secret);

        using var envelope = JsonDocument.Parse(text);
        var persistentJson = new FakeProtector().Unprotect(
            envelope.RootElement.GetProperty("encryptedPayload").GetString()!);
        using var persistent = JsonDocument.Parse(persistentJson);
        var storedProfile = persistent.RootElement.GetProperty("storage").GetProperty("profiles")[0];
        Assert.Equal(string.Empty, storedProfile.GetProperty("accessKey").GetString());
        Assert.Equal(string.Empty, storedProfile.GetProperty("secretKey").GetString());
        Assert.Equal("sk", persistent.RootElement.GetProperty("credentialVault")[0].GetProperty("secret").GetString());
    }

    [Fact]
    public async Task CanonicalTakesPrecedenceOverLegacy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp(); var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        await store.SaveAsync(ExplorerConfiguration.Empty, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "profiles.json"), "not used", cancellationToken);
        var reopened = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        Assert.Empty((await reopened.LoadAsync(cancellationToken)).Storage.Profiles);
        Assert.False(File.Exists(Path.Combine(root, "profiles.json")));
        Assert.True(Directory.EnumerateFiles(
            Path.Combine(root, "legacy-archive"),
            "profiles.json",
            SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task CanonicalBackupRecoveryRemainsVisibleAfterSubsequentLoads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        await store.SaveAsync(ExplorerConfiguration.Empty, cancellationToken);
        await File.WriteAllTextAsync(store.Path, "{truncated", cancellationToken);

        var reopened = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);

        Assert.True(reopened.LastRecovery?.RestoredFromBackup);
        Assert.Empty((await reopened.LoadAsync(cancellationToken)).Storage.Profiles);
        Assert.True(reopened.LastRecovery?.RestoredFromBackup);
        Assert.True(Directory.EnumerateFiles(root, "configuration.json.corrupt-*", SearchOption.TopDirectoryOnly).Any());
    }

    [Fact]
    public async Task ConcurrentStoreInstancesMergeUpdatesWithoutLostWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var first = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        var second = await ExplorerConfigurationStore.OpenAsync(root.ToUpperInvariant(), new FakeProtector(), cancellationToken);
        var firstCredential = Credential("first");
        var secondCredential = Credential("second");

        await Task.WhenAll(
            first.UpdateAsync(configuration => configuration with
            {
                CredentialVault = configuration.CredentialVault.Append(firstCredential).ToArray()
            }, cancellationToken),
            second.UpdateAsync(configuration => configuration with
            {
                CredentialVault = configuration.CredentialVault.Append(secondCredential).ToArray()
            }, cancellationToken));

        var saved = await first.LoadAsync(cancellationToken);
        Assert.Equal(2, saved.CredentialVault.Count);
        Assert.Contains(saved.CredentialVault, value => value.Id == firstCredential.Id);
        Assert.Contains(saved.CredentialVault, value => value.Id == secondCredential.Id);
    }

    [Fact]
    public async Task LegacyStoresMigrateOnceWithCollisionAndNoneAuthenticationHandled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var collidingId = Guid.NewGuid();
        var noneId = Guid.NewGuid();
        var storageProfile = ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with
        {
            Id = collidingId,
            Name = "aliyun-storage",
            AccessKey = "legacy-ak",
            SecretKey = "legacy-sk",
            DefaultBucket = "release"
        };
        await new JsonProfileStore(
                new DpapiCredentialProtector(),
                Path.Combine(root, "profiles.json"))
            .SaveConfigurationAsync(new ConnectionProfileConfiguration([storageProfile], []), cancellationToken);

        await new JsonCdnCredentialStore(
                new DpapiCdnCredentialProtector(),
                Path.Combine(root, "cdn-credentials.json"))
            .SaveAsync(
            [
                new CdnCredential
                {
                    Id = collidingId,
                    Name = "aliyun-cdn-legacy-token",
                    AuthenticationType = CdnAuthenticationType.BearerToken,
                    Secret = "legacy-token"
                },
                new CdnCredential
                {
                    Id = noneId,
                    Name = "anonymous",
                    AuthenticationType = CdnAuthenticationType.None
                }
            ], cancellationToken);
        var bearerProfile = new CdnProfile
        {
            Name = "legacy-cdn",
            BaseUrl = "https://cdn.example.com/",
            CredentialId = collidingId
        };
        var anonymousProfile = new CdnProfile
        {
            Name = "anonymous-cdn",
            BaseUrl = "https://public.example.com/",
            CredentialId = noneId
        };
        await new JsonCdnConfigurationStore(Path.Combine(root, "cdn-config.json"))
            .SaveAsync(new CdnConfiguration([bearerProfile, anonymousProfile], []), cancellationToken);

        var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        var migrated = await store.LoadAsync(cancellationToken);

        Assert.Equal(2, migrated.CredentialVault.Count);
        Assert.Equal(2, migrated.CredentialVault.Select(value => value.Id).Distinct().Count());
        Assert.Equal("legacy-sk", migrated.FindCredential(migrated.Storage.Profiles.Single().CredentialId)?.Secret);
        Assert.Equal("legacy-token", migrated.FindCredential(
            migrated.Cdn.Profiles.Single(value => value.Id == bearerProfile.Id).CredentialId)?.Secret);
        Assert.Null(migrated.Cdn.Profiles.Single(value => value.Id == anonymousProfile.Id).CredentialId);
        Assert.False(File.Exists(Path.Combine(root, "profiles.json")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(root, "legacy-archive"), "profiles.json", SearchOption.AllDirectories).Any());

        var reopened = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        Assert.Equal(2, (await reopened.LoadAsync(cancellationToken)).CredentialVault.Count);
    }

    private static CredentialProfile Credential(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Provider = CredentialProviderKind.GenericHttp,
        Kind = CredentialKind.BearerToken,
        Secret = $"{name}-secret"
    };

    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "s3explorer-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private sealed class FakeProtector : IConfigurationPayloadProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
