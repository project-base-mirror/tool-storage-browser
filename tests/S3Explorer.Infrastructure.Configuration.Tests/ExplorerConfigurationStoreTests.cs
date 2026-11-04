using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using S3Explorer.Infrastructure.Configuration;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.Configuration.Tests;

[SupportedOSPlatform("windows")]
public sealed class ExplorerConfigurationStoreTests
{
    [Theory]
    [InlineData("ciphertext")]
    [InlineData("authentication")]
    [InlineData("json")]
    [InlineData("reference")]
    [InlineData("null-record")]
    public async Task FullPayloadValidationRecoversHealthyBackup(string corruption)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Temp();
        try
        {
            var protector = new RecoveryTestProtector();
            var store = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            var first = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with { Name = "backup profile" };
            var healthy = new ExplorerConfiguration(new([first], []), CdnConfiguration.Empty, []);
            await store.SaveAsync(healthy, token);
            await store.SaveAsync(healthy with { Storage = new([first with { Name = "primary profile" }], []) }, token);
            var backup = await File.ReadAllBytesAsync(store.Path + ".bak", token);
            var envelope = JsonNode.Parse(await File.ReadAllTextAsync(store.Path, token))!.AsObject();
            if (corruption is "reference" or "null-record")
            {
                var payload = JsonNode.Parse(protector.Unprotect(envelope["encryptedPayload"]!.GetValue<string>()))!;
                if (corruption == "reference")
                    payload["storage"]!["profiles"]![0]!["credentialId"] = Guid.NewGuid().ToString();
                else
                    payload["storage"]!["profiles"]![0] = null;
                envelope["encryptedPayload"] = protector.Protect(payload.ToJsonString());
            }
            else
            {
                envelope["encryptedPayload"] = corruption switch
                {
                    "ciphertext" => "not-base64!",
                    "authentication" => "authentication-failed",
                    _ => protector.Protect("{invalid-json")
                };
            }
            var corruptText = envelope.ToJsonString();
            await File.WriteAllTextAsync(store.Path, corruptText, token);

            var reopened = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            var restored = await reopened.LoadAsync(token);

            Assert.Equal("backup profile", Assert.Single(restored.Storage.Profiles).Name);
            Assert.True(reopened.LastRecovery?.RestoredFromBackup);
            Assert.Equal(backup, await File.ReadAllBytesAsync(store.Path, token));
            Assert.Equal(corruptText, await File.ReadAllTextAsync(reopened.LastRecovery!.CorruptPath!, token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GuiAdapterMergeKeepsCliAdditionsAndRejectsSameRecordConflicts()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Temp();
        try
        {
            var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), token);
            var first = new ConnectionProfile { Name = "GUI profile" };
            var group = new ConnectionGroup { Name = "Group" };
            await store.SaveAsync(new(new([first], [group]), CdnConfiguration.Empty, []), token);
            var gui = new ExplorerProfileStore(store);
            var baseline = await gui.LoadConfigurationAsync(token);
            var cli = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), token);
            var added = new ConnectionProfile { Name = "CLI profile", SortOrder = 1 };
            await cli.UpdateAsync(current => current with
            {
                Storage = current.Storage with { Profiles = current.Storage.Profiles.Append(added).ToArray() }
            }, token);

            var saved = await gui.MergeConfigurationAsync(baseline, baseline.PlaceProfile(first.Id, group.Id, 0), token);

            Assert.Equal(2, saved.Profiles.Count);
            Assert.Contains(saved.Profiles, value => value.Id == added.Id);
            var onDisk = await cli.LoadAsync(token);
            Assert.Equal(group.Id, onDisk.Storage.Profiles.Single(value => value.Id == first.Id).GroupId);
            await cli.UpdateAsync(current => current with
            {
                Storage = current.Storage with
                {
                    Profiles = current.Storage.Profiles.Select(value => value.Id == first.Id
                        ? value with { Name = "CLI renamed" } : value).ToArray()
                }
            }, token);
            var beforeConflict = await File.ReadAllBytesAsync(store.Path, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => gui.MergeConfigurationAsync(saved,
                saved with { Profiles = saved.Profiles.Select(value => value.Id == first.Id
                    ? value with { Name = "GUI renamed" } : value).ToArray() }, token));
            Assert.Equal(beforeConflict, await File.ReadAllBytesAsync(store.Path, token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecoveryTestProtector : IConfigurationPayloadProtector
    {
        private readonly FakeProtector _inner = new();
        public string Protect(string plaintext) => _inner.Protect(plaintext);
        public string Unprotect(string ciphertext) => ciphertext == "authentication-failed"
            ? throw new System.Security.Cryptography.CryptographicException("Test authentication failure")
            : _inner.Unprotect(ciphertext);
    }

    [Theory]
    [InlineData(UploadSigningMode.Auto)]
    [InlineData(UploadSigningMode.Streaming)]
    [InlineData(UploadSigningMode.UnsignedPayload)]
    public async Task UploadSigningModeSurvivesEncryptedConfigurationAndLegacyOmission(UploadSigningMode mode)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Temp();
        try
        {
            var protector = new FakeProtector();
            var store = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Name = "signing", CredentialSource = CredentialSourceKind.AwsEnvironmentVariables,
                UploadSigningMode = mode
            };
            await store.SaveAsync(new ExplorerConfiguration(new ConnectionProfileConfiguration([profile], []), CdnConfiguration.Empty, []), token);
            var reopened = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            Assert.Equal(mode, Assert.Single((await reopened.LoadAsync(token)).Storage.Profiles).UploadSigningMode);
            var envelope = JsonNode.Parse(await File.ReadAllTextAsync(store.Path, token))!.AsObject();
            var payload = JsonNode.Parse(protector.Unprotect(envelope["encryptedPayload"]!.GetValue<string>()))!.AsObject();
            payload["storage"]!["profiles"]![0]!.AsObject().Remove("uploadSigningMode");
            envelope["encryptedPayload"] = protector.Protect(payload.ToJsonString());
            await File.WriteAllTextAsync(store.Path, envelope.ToJsonString(), token);
            reopened = await ExplorerConfigurationStore.OpenAsync(root, protector, token);
            Assert.Equal(UploadSigningMode.Auto, Assert.Single((await reopened.LoadAsync(token)).Storage.Profiles).UploadSigningMode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("a", false)]
    [InlineData("bad value", false)]
    [InlineData("a", true)]
    [InlineData("bad value", true)]
    public async Task InvalidExternalIdEditDoesNotReplaceConfigurationOrBackup(string invalidSecret, bool atomicUpdate)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Temp();
        try
        {
            var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), token);
            var credential = new CredentialProfile
            {
                Name = "external-id", Provider = CredentialProviderKind.AmazonWebServices,
                Kind = CredentialKind.SecretValue, Secret = "valid-external-id"
            };
            var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
            {
                Name = "role", CredentialSource = CredentialSourceKind.AwsAssumeRole,
                AwsSourceProfileName = "base", AwsRoleArn = "arn:aws:iam::123456789012:role/test", AwsRoleSessionName = "test-session",
                AwsExternalIdCredentialId = credential.Id, AwsExternalId = credential.Secret
            };
            await store.SaveAsync(new(new([profile], []), CdnConfiguration.Empty, [credential]), token);
            var before = await File.ReadAllBytesAsync(store.Path, token);
            var backupBefore = await File.ReadAllBytesAsync(store.Path + ".bak", token);
            var loaded = await store.LoadAsync(token);
            var changed = credential with { Secret = invalidSecret };
            if (atomicUpdate)
                await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(
                    current => current with { CredentialVault = new[] { changed } }, token));
            else
                await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
                    loaded with { CredentialVault = new[] { changed } }, token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.Path, token));
            Assert.Equal(backupBefore, await File.ReadAllBytesAsync(store.Path + ".bak", token));
            var reopened = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), token);
            var unchanged = await reopened.LoadAsync(token);
            Assert.Equal(credential, Assert.Single(unchanged.CredentialVault));
            Assert.Equal("valid-external-id", Assert.Single(unchanged.Storage.Profiles).AwsExternalId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
        var bearerProfile = new CdnProfile { Name = "legacy-cdn", BaseUrl = "https://cdn.example.com/" };
        var anonymousProfile = new CdnProfile { Name = "anonymous-cdn", BaseUrl = "https://public.example.com/" };
        await WriteLegacyCdnConfigurationAsync(
            Path.Combine(root, "cdn-config.json"),
            [
                LegacyCdnProfile(bearerProfile, collidingId, "https://api.example/purge?url={url}"),
                LegacyCdnProfile(anonymousProfile, noneId, string.Empty)
            ],
            cancellationToken);

        var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        var migrated = await store.LoadAsync(cancellationToken);

        Assert.Equal(2, migrated.CredentialVault.Count);
        Assert.Equal(2, migrated.CredentialVault.Select(value => value.Id).Distinct().Count());
        Assert.Equal("legacy-sk", migrated.FindCredential(migrated.Storage.Profiles.Single().CredentialId)?.Secret);
        var migratedBearer = migrated.Cdn.Profiles.Single(value => value.Id == bearerProfile.Id);
        Assert.Equal("legacy-token", migratedBearer.ContentAuthentication.Secret);
        Assert.NotNull(migratedBearer.ControlCredentialId);
        Assert.Equal("legacy-token", migrated.FindCredential(migratedBearer.ControlCredentialId)?.Secret);
        Assert.Equal(CdnAuthenticationType.None, migrated.Cdn.Profiles.Single(value => value.Id == anonymousProfile.Id).ContentAuthentication.AuthenticationType);
        Assert.Null(migrated.Cdn.Profiles.Single(value => value.Id == anonymousProfile.Id).ControlCredentialId);
        Assert.False(File.Exists(Path.Combine(root, "profiles.json")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(root, "legacy-archive"), "profiles.json", SearchOption.AllDirectories).Any());

        var reopened = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        Assert.Equal(2, (await reopened.LoadAsync(cancellationToken)).CredentialVault.Count);
    }

    [Fact]
    public async Task Schema1GenericContentCredentialBecomesInlineAndIsRemovedWithoutControlUse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var credential = Credential("delivery") with { Id = Guid.NewGuid() };
        var profile = new CdnProfile { Name = "cdn", BaseUrl = "https://cdn.example/" };
        var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        await store.SaveAsync(new ExplorerConfiguration(
            new ConnectionProfileConfiguration([], []),
            new CdnConfiguration([profile], []),
            [credential]), cancellationToken);
        await RewriteAsSchema1Async(store.Path, credential.Id, cancellationToken);

        var migratedStore = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        var migrated = await migratedStore.LoadAsync(cancellationToken);
        var migratedProfile = Assert.Single(migrated.Cdn.Profiles);

        Assert.Equal(CdnAuthenticationType.BearerToken, migratedProfile.ContentAuthentication.AuthenticationType);
        Assert.Equal("delivery-secret", migratedProfile.ContentAuthentication.Secret);
        Assert.Null(migratedProfile.ControlCredentialId);
        Assert.Empty(migrated.CredentialVault);
        Assert.Equal(2, ReadSchema(migratedStore.Path));
    }

    [Fact]
    public async Task Schema1GenericPurgeCredentialRemainsAsControlCredential()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var credential = Credential("control") with { Id = Guid.NewGuid() };
        var profile = new CdnProfile
        {
            Name = "cdn",
            BaseUrl = "https://cdn.example/",
            PurgeEndpointTemplate = "https://api.example/purge?url={url}"
        };
        var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        await store.SaveAsync(new ExplorerConfiguration(
            new ConnectionProfileConfiguration([], []),
            new CdnConfiguration([profile], []),
            [credential]), cancellationToken);
        await RewriteAsSchema1Async(store.Path, credential.Id, cancellationToken);

        var migrated = await (await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken)).LoadAsync(cancellationToken);
        var migratedProfile = Assert.Single(migrated.Cdn.Profiles);

        Assert.Equal(credential.Id, migratedProfile.ControlCredentialId);
        Assert.Equal("control-secret", migratedProfile.ContentAuthentication.Secret);
        Assert.Equal(credential.Id, Assert.Single(migrated.CredentialVault).Id);
    }

    [Fact]
    public async Task Schema1AliyunCredentialBecomesControlCredentialWithAnonymousContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var credential = new CredentialProfile
        {
            Id = Guid.NewGuid(),
            Name = "aliyun",
            Provider = CredentialProviderKind.AlibabaCloud,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "ak",
            Secret = "sk"
        };
        var profile = new CdnProfile
        {
            Name = "aliyun-cdn",
            ProviderId = CdnProfile.AlibabaCloudProviderId,
            BaseUrl = "https://cdn.example/",
            ControlCredentialId = credential.Id
        };
        var store = await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken);
        await store.SaveAsync(new ExplorerConfiguration(
            new ConnectionProfileConfiguration([], []),
            new CdnConfiguration([profile], []),
            [credential]), cancellationToken);
        await RewriteAsSchema1Async(store.Path, credential.Id, cancellationToken);

        var migrated = await (await ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken)).LoadAsync(cancellationToken);
        var migratedProfile = Assert.Single(migrated.Cdn.Profiles);

        Assert.Equal(credential.Id, migratedProfile.ControlCredentialId);
        Assert.Equal(CdnAuthenticationType.None, migratedProfile.ContentAuthentication.AuthenticationType);
        Assert.Equal(credential.Id, Assert.Single(migrated.CredentialVault).Id);
    }

    [Fact]
    public async Task UnknownConfigurationSchemaIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Temp();
        var path = Path.Combine(root, "configuration.json");
        await File.WriteAllTextAsync(path, "{\"schema\":99,\"encryptedPayload\":\"ignored\"}", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExplorerConfigurationStore.OpenAsync(root, new FakeProtector(), cancellationToken));
    }

    private static CredentialProfile Credential(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Provider = CredentialProviderKind.GenericHttp,
        Kind = CredentialKind.BearerToken,
        Secret = $"{name}-secret"
    };

    private static JsonObject LegacyCdnProfile(CdnProfile profile, Guid credentialId, string purgeEndpoint) => new()
    {
        ["id"] = profile.Id.ToString("D"),
        ["name"] = profile.Name,
        ["notes"] = string.Empty,
        ["providerId"] = CdnProfile.GenericHttpProviderId,
        ["baseUrl"] = profile.BaseUrl,
        ["credentialId"] = credentialId.ToString("D"),
        ["warmupMode"] = (int)CdnWarmupMode.RangeGet,
        ["warmupRangeBytes"] = 1048576,
        ["purgeEndpointTemplate"] = purgeEndpoint,
        ["purgeHttpMethod"] = "POST",
        ["purgeBodyTemplate"] = string.Empty,
        ["purgeContentType"] = "application/json",
        ["timeoutSeconds"] = 100,
        ["followRedirects"] = true,
        ["enabled"] = true
    };

    private static async Task WriteLegacyCdnConfigurationAsync(
        string path,
        IReadOnlyList<JsonObject> profiles,
        CancellationToken cancellationToken)
    {
        var document = new JsonObject
        {
            ["version"] = 1,
            ["profiles"] = new JsonArray(profiles.Cast<JsonNode?>().ToArray()),
            ["bindings"] = new JsonArray()
        };
        await File.WriteAllTextAsync(path, document.ToJsonString(), cancellationToken);
    }

    private static async Task RewriteAsSchema1Async(string path, Guid credentialId, CancellationToken cancellationToken)
    {
        var protector = new FakeProtector();
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject();
        var payload = JsonNode.Parse(protector.Unprotect(envelope["encryptedPayload"]!.GetValue<string>()))!.AsObject();
        var profile = payload["cdn"]!["profiles"]!.AsArray().Single()!.AsObject();
        profile.Remove("contentAuthentication");
        profile.Remove("controlCredentialId");
        profile["credentialId"] = credentialId.ToString("D");
        envelope["schema"] = 1;
        envelope["encryptedPayload"] = protector.Protect(payload.ToJsonString());
        await File.WriteAllTextAsync(path, envelope.ToJsonString(), cancellationToken);
    }

    private static int ReadSchema(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("schema").GetInt32();
    }

    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "s3explorer-config-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private sealed class FakeProtector : IConfigurationPayloadProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
