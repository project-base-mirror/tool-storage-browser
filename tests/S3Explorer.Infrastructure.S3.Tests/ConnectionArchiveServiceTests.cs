using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConnectionArchiveServiceTests
{
    private readonly ConnectionArchiveService _service = new();

    [Fact]
    public void CredentialFreeExportOmitsEveryCredentialValue()
    {
        var archive = _service.Export([CreateProfile()]);
        var json = Encoding.UTF8.GetString(archive);

        Assert.DoesNotContain("access-value", json);
        Assert.DoesNotContain("secret-value", json);
        Assert.DoesNotContain("session-value", json);
        var inspection = _service.Inspect(archive);
        Assert.False(inspection.ContainsCredentials);
        Assert.False(inspection.RequiresPassword);

        var imported = Assert.Single(_service.Import(archive).Profiles);
        Assert.Empty(imported.AccessKey);
        Assert.Empty(imported.SecretKey);
        Assert.Empty(imported.SessionToken);
        Assert.Equal("https://storage.example.test", imported.Endpoint);
    }

    [Fact]
    public void PasswordProtectedExportCanMoveReadyToUseCredentials()
    {
        var (profile, credential) = CreateUnifiedProfile();
        var archive = _service.Export([profile], includeCredentials: true, password: "portable-password", credentials: [credential]);
        var json = Encoding.UTF8.GetString(archive);

        Assert.DoesNotContain("access-value", json);
        Assert.DoesNotContain("secret-value", json);
        Assert.DoesNotContain("session-value", json);
        Assert.True(_service.Inspect(archive).RequiresPassword);

        var imported = Assert.Single(_service.Import(archive, "portable-password").Profiles);
        Assert.Equal("access-value", imported.AccessKey);
        Assert.Equal("secret-value", imported.SecretKey);
        Assert.Equal("session-value", imported.SessionToken);
        imported.Validate();
    }

    [Fact]
    public void WrongPasswordIsReportedAsAuthenticationFailure()
    {
        var (profile, credential) = CreateUnifiedProfile();
        var archive = _service.Export([profile], includeCredentials: true, password: "portable-password", credentials: [credential]);

        Assert.Throws<ConnectionArchiveAuthenticationException>(() =>
            _service.Import(archive, "wrong-password"));
    }

    [Fact]
    public void MergeRequiresExplicitCredentialChoice()
    {
        var imported = CreateProfile();

        var withoutCredentials = Assert.Single(_service.Merge(
            [], [imported], importCredentials: false, ConnectionImportConflictStrategy.Rename));
        var withCredentials = Assert.Single(_service.Merge(
            [], [imported], importCredentials: true, ConnectionImportConflictStrategy.Rename));

        Assert.Empty(withoutCredentials.AccessKey);
        Assert.Empty(withoutCredentials.SecretKey);
        Assert.Equal("secret-value", withCredentials.SecretKey);
    }

    [Theory]
    [InlineData(ConnectionImportConflictStrategy.Skip, 1, "Local")]
    [InlineData(ConnectionImportConflictStrategy.Replace, 1, "Remote")]
    [InlineData(ConnectionImportConflictStrategy.Rename, 2, "Local")]
    public void MergeHandlesDuplicateNames(
        ConnectionImportConflictStrategy strategy,
        int expectedCount,
        string expectedFirstAccessKey)
    {
        var existing = CreateProfile() with
        {
            Id = Guid.NewGuid(),
            Name = "Shared",
            AccessKey = "Local"
        };
        var imported = CreateProfile() with { Name = "shared", AccessKey = "Remote" };

        var result = _service.Merge([existing], [imported], importCredentials: true, strategy);

        Assert.Equal(expectedCount, result.Count);
        Assert.Equal(expectedFirstAccessKey, result[0].AccessKey);
        if (strategy == ConnectionImportConflictStrategy.Replace)
            Assert.Equal(existing.Id, result[0].Id);
        if (strategy == ConnectionImportConflictStrategy.Rename)
            Assert.Equal("shared (导入)", result[1].Name);
    }

    [Fact]
    public void TamperedCiphertextDoesNotImport()
    {
        var (profile, credential) = CreateUnifiedProfile();
        var archive = _service.Export([profile], includeCredentials: true, password: "portable-password", credentials: [credential]);
        var document = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(archive)));
        var encryptedPayload = Assert.IsAssignableFrom<JsonValue>(document["encryptedPayload"])
            .GetValue<string>();
        var ciphertext = Convert.FromBase64String(encryptedPayload);
        ciphertext[0] ^= 0x01;
        document["encryptedPayload"] = Convert.ToBase64String(ciphertext);
        var tampered = Encoding.UTF8.GetBytes(document.ToJsonString());

        Assert.Throws<ConnectionArchiveAuthenticationException>(() =>
            _service.Import(tampered, "portable-password"));
    }

    [Fact]
    public void ExternalCredentialExportKeepsSourceReferenceButNeverCopiesSecrets()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "AWS shared",
            CredentialSource = CredentialSourceKind.AwsSharedProfile,
            AwsProfileName = "audit",
            AccessKey = "stale-access",
            SecretKey = "stale-secret",
            SessionToken = "stale-session"
        };

        var archive = _service.Export([profile]);
        var text = Encoding.UTF8.GetString(archive);
        var imported = Assert.Single(_service.Import(archive).Profiles);

        Assert.Contains("\"version\": 5", text, StringComparison.Ordinal);
        Assert.Contains("audit", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-", text, StringComparison.Ordinal);
        Assert.Equal(CredentialSourceKind.AwsSharedProfile, imported.CredentialSource);
        Assert.Equal("audit", imported.AwsProfileName);
        Assert.Empty(imported.AccessKey);
        imported.Validate();
    }

    [Fact]
    public void ExternalOnlyExportNeverRequiresPasswordOrClaimsToContainCredentials()
    {
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Workload role",
            CredentialSource = CredentialSourceKind.AwsContainerRole
        };

        var archive = _service.Export([profile], includeCredentials: true);
        var inspection = _service.Inspect(archive);

        Assert.False(inspection.ContainsCredentials);
        Assert.False(inspection.RequiresPassword);
        Assert.Equal(CredentialSourceKind.AwsContainerRole, Assert.Single(_service.Import(archive).Profiles).CredentialSource);
    }

    [Fact]
    public void AssumeRoleArchiveSeparatesPortableConfigurationFromProtectedExternalId()
    {
        var externalIdCredential = new CredentialProfile
        {
            Name = "Audit role External ID",
            Provider = CredentialProviderKind.AmazonWebServices,
            Kind = CredentialKind.SecretValue,
            Secret = "customer-external-secret"
        };
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Audit role",
            CredentialSource = CredentialSourceKind.AwsAssumeRole,
            AwsSourceProfileName = "bootstrap",
            AwsRoleArn = "arn:aws:iam::123456789012:role/Audit",
            AwsRoleSessionName = "s3explorer-audit",
            AwsRoleSourceIdentity = "operator-42",
            AwsExternalIdCredentialId = externalIdCredential.Id,
            AwsExternalId = externalIdCredential.Secret,
            AwsSessionDurationSeconds = 1800
        };

        var credentialFreeArchive = _service.Export([profile]);
        var credentialFreeText = Encoding.UTF8.GetString(credentialFreeArchive);
        var credentialFree = Assert.Single(_service.Import(credentialFreeArchive).Profiles);

        Assert.Contains(profile.AwsRoleArn, credentialFreeText, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.AwsExternalId, credentialFreeText, StringComparison.Ordinal);
        Assert.Empty(credentialFree.AwsExternalId);
        Assert.Equal("bootstrap", credentialFree.AwsSourceProfileName);
        Assert.Equal("operator-42", credentialFree.AwsRoleSourceIdentity);

        var protectedArchive = _service.Export(
            [profile],
            includeCredentials: true,
            password: "portable-password",
            credentials: [externalIdCredential]);
        Assert.DoesNotContain(profile.AwsExternalId, Encoding.UTF8.GetString(protectedArchive), StringComparison.Ordinal);
        Assert.True(_service.Inspect(protectedArchive).RequiresPassword);
        Assert.Equal(profile.AwsExternalId,
            Assert.Single(_service.Import(protectedArchive, "portable-password").Profiles).AwsExternalId);
    }

    [Fact]
    public void WebIdentityArchiveContainsOnlyTokenFileReference()
    {
        const string tokenContents = "eyJhbGciOiJub25lIn0.private-token-content";
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "OIDC workload",
            CredentialSource = CredentialSourceKind.AwsWebIdentity,
            AwsRoleArn = "arn:aws:iam::123456789012:role/Workload",
            AwsRoleSessionName = "s3explorer-workload",
            AwsWebIdentityTokenFile = Path.GetFullPath("workload-token.jwt"),
            AwsSessionDurationSeconds = 1800
        };

        var archive = _service.Export([profile], includeCredentials: true);
        var text = Encoding.UTF8.GetString(archive);
        var imported = Assert.Single(_service.Import(archive).Profiles);

        Assert.False(_service.Inspect(archive).ContainsCredentials);
        Assert.Contains("workload-token.jwt", text, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenContents, text, StringComparison.Ordinal);
        Assert.Equal(profile.AwsWebIdentityTokenFile, imported.AwsWebIdentityTokenFile);
    }

    [Fact]
    public void MergePackagePlacesNewProfilesInTheSelectedTargetGroup()
    {
        var package = _service.Import(_service.Export([CreateProfile()]));
        var groupId = Guid.NewGuid();

        var merged = _service.MergePackage(
            [], CdnConfiguration.Empty, [], package,
            new ConnectionArchiveImportSelection(package.Profiles.Select(profile => profile.Id).ToArray(), []),
            importStorageCredentials: false,
            importCredentials: false,
            ConnectionImportConflictStrategy.Rename,
            targetGroupId: groupId);

        var imported = Assert.Single(merged.Profiles);
        Assert.Equal(groupId, imported.GroupId);
        Assert.Equal(0, imported.SortOrder);
    }

    [Fact]
    public void VersionOneCredentialFreeArchiveStillImportsAsStoredKeys()
    {
        var current = JsonNode.Parse(_service.Export([CreateProfile()]))!.AsObject();
        current["version"] = 1;
        current.Remove("cdnProfileCount");
        current.Remove("cdnCredentialCount");
        current.Remove("cdnProfiles");
        current.Remove("cdnBindings");
        var versionOne = JsonSerializer.SerializeToUtf8Bytes(current);

        var imported = Assert.Single(_service.Import(versionOne).Profiles);

        Assert.Equal(CredentialSourceKind.StoredKeys, imported.CredentialSource);
        Assert.Empty(imported.AccessKey);
        Assert.Equal("https://storage.example.test", imported.Endpoint);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UnprotectedArchiveRejectsStorageSecretsAndReferencesBeforeMigration(int version)
    {
        var document = JsonNode.Parse(Encoding.UTF8.GetString(_service.Export([CreateProfile()])))!.AsObject();
        document["version"] = version;
        if (version < 3)
        {
            document.Remove("cdnProfileCount");
            document.Remove("cdnCredentialCount");
            document.Remove("credentialCount");
            document.Remove("cdnProfiles");
            document.Remove("cdnBindings");
            document.Remove("credentialProfiles");
        }

        var profile = Assert.IsType<JsonArray>(document["profiles"])[0]!.AsObject();
        profile["accessKey"] = "plaintext-access";
        profile["credentialId"] = Guid.NewGuid();

        Assert.Throws<InvalidDataException>(() =>
            _service.Import(Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UnprotectedArchiveRejectsCredentialEntriesBeforeMigration(int version)
    {
        var document = JsonNode.Parse(Encoding.UTF8.GetString(_service.Export([CreateProfile()])))!.AsObject();
        document["version"] = version;
        if (version < 3)
        {
            document.Remove("cdnProfileCount");
            document.Remove("cdnCredentialCount");
            document.Remove("credentialCount");
            document.Remove("cdnProfiles");
            document.Remove("cdnBindings");
        }

        document["credentialCount"] = version >= 4 ? 1 : 0;
        document["cdnCredentialCount"] = version < 4 ? 1 : 0;
        document["credentialProfiles"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = Guid.NewGuid(),
                ["name"] = "injected",
                ["provider"] = "genericHttp",
                ["kind"] = "bearerToken",
                ["secret"] = "plaintext-secret"
            }
        };

        Assert.Throws<InvalidDataException>(() =>
            _service.Import(Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UnprotectedArchiveRejectsCdnSecretsAndReferencesBeforeMigration(int version)
    {
        var document = JsonNode.Parse(Encoding.UTF8.GetString(_service.Export([CreateProfile()])))!.AsObject();
        document["version"] = version;
        document["cdnProfileCount"] = 1;
        document["cdnProfiles"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = Guid.NewGuid(),
                ["name"] = "injected-cdn",
                ["providerId"] = "generic-http",
                ["baseUrl"] = "https://cdn.example.test",
                ["contentAuthentication"] = new JsonObject
                {
                    ["authenticationType"] = "bearerToken",
                    ["secret"] = "plaintext-cdn-secret"
                },
                ["controlCredentialId"] = Guid.NewGuid()
            }
        };
        document["cdnBindings"] = new JsonArray();

        Assert.Throws<InvalidDataException>(() =>
            _service.Import(Encoding.UTF8.GetBytes(document.ToJsonString())));
    }

    [Fact]
    public void ImportRepairsLegacyAmazonTypeWithoutReplacingCompatibleEndpoint()
    {
        var legacy = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "legacy-compatible",
            Endpoint = "https://oss-cn-shenzhen.aliyuncs.com",
            Region = "auto",
            AccessKey = "access-value",
            SecretKey = "secret-value"
        };

        var imported = Assert.Single(_service.Import(_service.Export([legacy])).Profiles);

        Assert.Equal(S3ServiceType.Custom, imported.ServiceType);
        Assert.Equal(legacy.Endpoint, imported.Endpoint);
        Assert.Equal("auto", imported.Region);
    }

    [Fact]
    public void CredentialFreeExportKeepsCdnConfigurationButOmitsCdnSecretsAndReferences()
    {
        var storage = CreateProfile();
        var credential = CreateCdnCredential();
        var cdnProfile = CreateCdnProfile(credential.Id) with
        {
            ContentAuthentication = new CdnHttpAuthentication
            {
                AuthenticationType = CdnAuthenticationType.BearerToken,
                Secret = "content-secret-value"
            }
        };
        var configuration = new CdnConfiguration(
            [cdnProfile],
            [CreateCdnBinding(storage.Id, cdnProfile.Id)]);

        var archive = _service.Export(
            [storage],
            cdnConfiguration: configuration,
            credentials: [credential]);
        var json = Encoding.UTF8.GetString(archive);
        var inspection = _service.Inspect(archive);
        var package = _service.Import(archive);

        Assert.Contains("https://cdn.example.test", json, StringComparison.Ordinal);
        Assert.Contains("production delivery", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("content-secret-value", json, StringComparison.Ordinal);
        Assert.Equal(1, inspection.CdnProfileCount);
        Assert.Equal(0, inspection.CredentialCount);
        var importedProfile = Assert.Single(package.ImportedCdnConfiguration.Profiles);
        Assert.Null(importedProfile.ControlCredentialId);
        Assert.Equal(CdnAuthenticationType.None, importedProfile.ContentAuthentication.AuthenticationType);
        Assert.Equal("production delivery", Assert.Single(package.ImportedCdnConfiguration.Profiles).Notes);
        Assert.Single(package.ImportedCdnConfiguration.Bindings);
        Assert.Empty(package.ImportedCredentials);
    }

    [Fact]
    public void PasswordProtectedExportMovesCdnSecretsWithoutExposingThemInEnvelope()
    {
        var storage = CreateProfile();
        var credential = CreateCdnCredential();
        var cdnProfile = CreateCdnProfile(credential.Id) with
        {
            ContentAuthentication = new CdnHttpAuthentication
            {
                AuthenticationType = CdnAuthenticationType.CustomHeader,
                HeaderName = "X-CDN-Token",
                Secret = "content-secret-value"
            }
        };
        var configuration = new CdnConfiguration(
            [cdnProfile],
            [CreateCdnBinding(storage.Id, cdnProfile.Id)]);

        var archive = _service.Export(
            [storage],
            includeCredentials: true,
            password: "portable-password",
            cdnConfiguration: configuration,
            credentials: [credential]);
        var json = Encoding.UTF8.GetString(archive);
        var inspection = _service.Inspect(archive);
        var package = _service.Import(archive, "portable-password");

        Assert.DoesNotContain("cdn-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("content-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn-auth", json, StringComparison.Ordinal);
        Assert.True(inspection.ContainsCredentials);
        Assert.Equal(1, inspection.CredentialCount);
        Assert.Equal("cdn-secret-value", Assert.Single(package.ImportedCredentials).Secret);
        Assert.Equal("content-secret-value", Assert.Single(package.ImportedCdnConfiguration.Profiles).ContentAuthentication.Secret);
        Assert.Equal(
            Assert.Single(package.ImportedCredentials).Id,
            Assert.Single(package.ImportedCdnConfiguration.Profiles).ControlCredentialId);
    }

    [Fact]
    public void PackageMergeRemapsStorageCdnCredentialAndBindingIds()
    {
        var storage = CreateProfile();
        var credential = CreateCdnCredential();
        var cdnProfile = CreateCdnProfile(credential.Id);
        var package = new ConnectionArchivePackage(
            [storage],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration([cdnProfile], [CreateCdnBinding(storage.Id, cdnProfile.Id)]),
            [credential]);

        var merged = _service.MergePackage(
            [],
            CdnConfiguration.Empty,
            [],
            package,
            [storage.Id],
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);

        var importedStorage = Assert.Single(merged.Profiles);
        var importedCredential = Assert.Single(merged.Credentials);
        var importedCdn = Assert.Single(merged.CdnConfiguration.Profiles);
        var importedBinding = Assert.Single(merged.CdnConfiguration.Bindings);
        Assert.NotEqual(storage.Id, importedStorage.Id);
        Assert.NotEqual(credential.Id, importedCredential.Id);
        Assert.NotEqual(cdnProfile.Id, importedCdn.Id);
        Assert.Equal(importedCredential.Id, importedCdn.ControlCredentialId);
        Assert.Equal(importedStorage.Id, importedBinding.StorageProfileId);
        Assert.Equal(importedCdn.Id, importedBinding.CdnProfileId);
        CdnConfigurationValidator.EnsureValid(merged.CdnConfiguration, merged.Credentials);
    }

    [Fact]
    public void PackageMergeWithoutCredentialChoiceImportsCdnAsUnauthenticated()
    {
        var storage = CreateProfile();
        var credential = CreateCdnCredential();
        var cdnProfile = CreateCdnProfile(credential.Id);
        var package = new ConnectionArchivePackage(
            [storage],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration([cdnProfile], [CreateCdnBinding(storage.Id, cdnProfile.Id)]),
            [credential]);

        var merged = _service.MergePackage(
            [],
            CdnConfiguration.Empty,
            [],
            package,
            [storage.Id],
            importCredentials: false,
            ConnectionImportConflictStrategy.Rename);

        Assert.Empty(merged.Credentials);
        Assert.Null(Assert.Single(merged.CdnConfiguration.Profiles).ControlCredentialId);
        CdnConfigurationValidator.EnsureValid(merged.CdnConfiguration, merged.Credentials);
    }

    [Fact]
    public void PackageMergeReplacePreservesExistingIdsAcrossCdnReferences()
    {
        var existingStorage = CreateProfile() with { Id = Guid.NewGuid() };
        var existingCredential = CreateCdnCredential() with
        {
            Id = Guid.NewGuid(),
            Secret = "old-secret"
        };
        var existingCdn = CreateCdnProfile(existingCredential.Id) with
        {
            Id = Guid.NewGuid(),
            BaseUrl = "https://old-cdn.example.test"
        };
        var existingBinding = CreateCdnBinding(existingStorage.Id, existingCdn.Id) with
        {
            Id = Guid.NewGuid()
        };

        var importedStorage = CreateProfile() with { Id = Guid.NewGuid() };
        var importedCredential = CreateCdnCredential() with { Id = Guid.NewGuid() };
        var importedCdn = CreateCdnProfile(importedCredential.Id) with { Id = Guid.NewGuid() };
        var package = new ConnectionArchivePackage(
            [importedStorage],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration(
                [importedCdn],
                [CreateCdnBinding(importedStorage.Id, importedCdn.Id)]),
            [importedCredential]);

        var merged = _service.MergePackage(
            [existingStorage],
            new CdnConfiguration([existingCdn], [existingBinding]),
            [existingCredential],
            package,
            [importedStorage.Id],
            importCredentials: true,
            ConnectionImportConflictStrategy.Replace);

        Assert.Equal(existingStorage.Id, Assert.Single(merged.Profiles).Id);
        var mergedCredential = Assert.Single(merged.Credentials);
        var mergedCdn = Assert.Single(merged.CdnConfiguration.Profiles);
        var mergedBinding = Assert.Single(merged.CdnConfiguration.Bindings);
        Assert.Equal(existingCredential.Id, mergedCredential.Id);
        Assert.Equal("cdn-secret-value", mergedCredential.Secret);
        Assert.Equal(existingCdn.Id, mergedCdn.Id);
        Assert.Equal("https://cdn.example.test", mergedCdn.BaseUrl);
        Assert.Equal(existingCredential.Id, mergedCdn.ControlCredentialId);
        Assert.Equal(existingBinding.Id, mergedBinding.Id);
        Assert.Equal(existingStorage.Id, mergedBinding.StorageProfileId);
        Assert.Equal(existingCdn.Id, mergedBinding.CdnProfileId);
    }

    [Fact]
    public void PackageMergeReusesEquivalentConfigurationAcrossDifferentNames()
    {
        var existingStorage = CreateProfile() with
        {
            Id = Guid.NewGuid(),
            Name = "existing-storage",
            HealthStatus = ConnectionHealthStatus.Healthy,
            LastConnectionSucceededAtUtc = DateTimeOffset.UtcNow
        };
        var existingCredential = CreateCdnCredential() with
        {
            Id = Guid.NewGuid(),
            Name = "existing-token"
        };
        var existingCdn = CreateCdnProfile(existingCredential.Id) with
        {
            Id = Guid.NewGuid(),
            Name = "existing-cdn"
        };
        var existingBinding = CreateCdnBinding(existingStorage.Id, existingCdn.Id) with
        {
            Id = Guid.NewGuid()
        };
        var importedStorage = CreateProfile() with { Id = Guid.NewGuid(), Name = "portable-copy" };
        var importedCredential = CreateCdnCredential() with { Id = Guid.NewGuid(), Name = "portable-token" };
        var importedCdn = CreateCdnProfile(importedCredential.Id) with
        {
            Id = Guid.NewGuid(),
            Name = "portable-cdn"
        };
        var package = new ConnectionArchivePackage(
            [importedStorage],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration(
                [importedCdn],
                [CreateCdnBinding(importedStorage.Id, importedCdn.Id)]),
            [importedCredential]);

        var merged = _service.MergePackage(
            [existingStorage],
            new CdnConfiguration([existingCdn], [existingBinding]),
            [existingCredential],
            package,
            new ConnectionArchiveImportSelection([importedStorage.Id], [importedCdn.Id]),
            importStorageCredentials: true,
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);

        Assert.Equal(existingStorage.Id, Assert.Single(merged.Profiles).Id);
        Assert.Equal(existingCredential.Id, Assert.Single(merged.Credentials).Id);
        Assert.Equal(existingCdn.Id, Assert.Single(merged.CdnConfiguration.Profiles).Id);
        Assert.Equal(existingBinding.Id, Assert.Single(merged.CdnConfiguration.Bindings).Id);
    }

    [Fact]
    public void RepeatedPackageMergeIsIdempotent()
    {
        var storage = CreateProfile();
        var credential = CreateCdnCredential();
        var cdn = CreateCdnProfile(credential.Id);
        var package = new ConnectionArchivePackage(
            [storage],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration([cdn], [CreateCdnBinding(storage.Id, cdn.Id)]),
            [credential]);
        var selection = new ConnectionArchiveImportSelection([storage.Id], [cdn.Id]);

        var first = _service.MergePackage(
            [], CdnConfiguration.Empty, [], package, selection,
            importStorageCredentials: true,
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);
        var second = _service.MergePackage(
            first.Profiles, first.CdnConfiguration, first.Credentials, package, selection,
            importStorageCredentials: true,
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);

        Assert.Equal(first.Profiles, second.Profiles);
        Assert.Equal(first.Credentials, second.Credentials);
        Assert.Equal(first.CdnConfiguration.Profiles, second.CdnConfiguration.Profiles);
        Assert.Equal(first.CdnConfiguration.Bindings, second.CdnConfiguration.Bindings);
    }

    [Fact]
    public void CdnOnlySelectionReusesEquivalentLocalStorageDependency()
    {
        var localStorage = CreateProfile() with { Id = Guid.NewGuid(), Name = "local-storage" };
        var archivedStorage = CreateProfile() with { Id = Guid.NewGuid(), Name = "archived-storage" };
        var cdn = CreateCdnProfile(Guid.NewGuid()) with { ControlCredentialId = null };
        var package = new ConnectionArchivePackage(
            [archivedStorage],
            ContainsCredentials: false,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration([cdn], [CreateCdnBinding(archivedStorage.Id, cdn.Id)]));

        var merged = _service.MergePackage(
            [localStorage],
            CdnConfiguration.Empty,
            [],
            package,
            new ConnectionArchiveImportSelection([], [cdn.Id]),
            importStorageCredentials: false,
            importCredentials: false,
            ConnectionImportConflictStrategy.Rename);

        Assert.Equal(localStorage.Id, Assert.Single(merged.Profiles).Id);
        var importedCdn = Assert.Single(merged.CdnConfiguration.Profiles);
        var binding = Assert.Single(merged.CdnConfiguration.Bindings);
        Assert.Equal(localStorage.Id, binding.StorageProfileId);
        Assert.Equal(importedCdn.Id, binding.CdnProfileId);
    }

    [Fact]
    public void CdnOnlySelectionRejectsMissingStorageDependencyInsteadOfDroppingBinding()
    {
        var archivedStorage = CreateProfile() with { Id = Guid.NewGuid(), Name = "archived-storage" };
        var unrelatedLocal = CreateProfile() with
        {
            Id = Guid.NewGuid(),
            Name = "unrelated-local",
            Endpoint = "https://different.example.test"
        };
        var cdn = CreateCdnProfile(Guid.NewGuid()) with { ControlCredentialId = null };
        var package = new ConnectionArchivePackage(
            [archivedStorage],
            ContainsCredentials: false,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration([cdn], [CreateCdnBinding(archivedStorage.Id, cdn.Id)]));

        var exception = Assert.Throws<InvalidDataException>(() => _service.MergePackage(
            [unrelatedLocal],
            CdnConfiguration.Empty,
            [],
            package,
            new ConnectionArchiveImportSelection([], [cdn.Id]),
            importStorageCredentials: false,
            importCredentials: false,
            ConnectionImportConflictStrategy.Rename));

        Assert.Contains("依赖对象存储连接", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialNameConflictWithSkipFailsInsteadOfCreatingDisconnectedProfile()
    {
        var (profile, importedCredential) = CreateUnifiedProfile();
        var localCredential = importedCredential with
        {
            Id = Guid.NewGuid(),
            Secret = "different-secret"
        };
        var package = new ConnectionArchivePackage(
            [profile],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            Credentials: [importedCredential]);

        var exception = Assert.Throws<InvalidDataException>(() => _service.MergePackage(
            [],
            CdnConfiguration.Empty,
            [localCredential],
            package,
            new ConnectionArchiveImportSelection([profile.Id], []),
            importStorageCredentials: true,
            importCredentials: true,
            ConnectionImportConflictStrategy.Skip));

        Assert.Contains("凭据未能导入", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageOnlySelectionDoesNotImplicitlyImportCdn()
    {
        var storage = CreateProfile();
        var cdn = CreateCdnProfile(Guid.NewGuid()) with { ControlCredentialId = null };
        var package = new ConnectionArchivePackage(
            [storage],
            ContainsCredentials: false,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration([cdn], [CreateCdnBinding(storage.Id, cdn.Id)]));

        var merged = _service.MergePackage(
            [],
            CdnConfiguration.Empty,
            [],
            package,
            new ConnectionArchiveImportSelection([storage.Id], []),
            importStorageCredentials: false,
            importCredentials: false,
            ConnectionImportConflictStrategy.Rename);

        Assert.Single(merged.Profiles);
        Assert.Empty(merged.CdnConfiguration.Profiles);
        Assert.Empty(merged.CdnConfiguration.Bindings);
    }

    [Fact]
    public void PreviewOnlyComparesSecretsWhenCredentialImportIsSelected()
    {
        var archived = CreateProfile() with { SecretKey = "new-secret" };
        var existing = archived with
        {
            Id = Guid.NewGuid(),
            SecretKey = "old-secret"
        };
        var package = new ConnectionArchivePackage(
            [archived],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow);

        var withoutSecrets = _service.PreviewPackage(
            [existing], CdnConfiguration.Empty, [], package, importStorageCredentials: false);
        var withSecrets = _service.PreviewPackage(
            [existing], CdnConfiguration.Empty, [], package, importStorageCredentials: true);

        Assert.Equal(
            ConnectionArchiveImportStatus.ExistingEquivalent,
            Assert.Single(withoutSecrets.StorageProfiles).Status);
        Assert.Equal(
            ConnectionArchiveImportStatus.NameConflict,
            Assert.Single(withSecrets.StorageProfiles).Status);
    }

    [Fact]
    public void PackageMergeReusesEquivalentCdnRuntimeConfigurationAcrossRenamedDisplayMetadata()
    {
        var archivedStorage = CreateAliyunProfile("archived-oss", "game-assets");
        var localStorage = archivedStorage with
        {
            Id = Guid.NewGuid(),
            Name = "local-oss-renamed",
            HealthStatus = ConnectionHealthStatus.Healthy,
            LastConnectionCheckedAtUtc = DateTimeOffset.UtcNow,
            LastConnectionSucceededAtUtc = DateTimeOffset.UtcNow
        };
        var archivedCredential = CreateCdnCredential() with
        {
            Id = Guid.NewGuid(),
            Name = "archived-token",
            HeaderName = string.Empty
        };
        var localCredential = archivedCredential with
        {
            Id = Guid.NewGuid(),
            Name = "local-token-renamed",
            HeaderName = string.Empty
        };
        var archivedCdn = CreateCdnProfile(archivedCredential.Id) with
        {
            Id = Guid.NewGuid(),
            Name = "archived-cdn",
            Notes = "archive note"
        };
        var localCdn = archivedCdn with
        {
            Id = Guid.NewGuid(),
            Name = "local-cdn-renamed",
            Notes = "local note",
            ControlCredentialId = localCredential.Id,
            LastCertificateCheck = CreateCertificateCheck(archivedCdn.BaseUrl)
        };
        var archivedBinding = CreateCdnBinding(archivedStorage.Id, archivedCdn.Id);
        var package = new ConnectionArchivePackage(
            [archivedStorage],
            ContainsCredentials: true,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            new CdnConfiguration([archivedCdn], [archivedBinding]),
            [archivedCredential]);

        var merged = _service.MergePackage(
            [localStorage],
            new CdnConfiguration([localCdn], []),
            [localCredential],
            package,
            new ConnectionArchiveImportSelection([archivedStorage.Id], [archivedCdn.Id]),
            importStorageCredentials: true,
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);

        Assert.Equal(localStorage.Id, Assert.Single(merged.Profiles).Id);
        Assert.Equal(localCredential.Id, Assert.Single(merged.Credentials).Id);
        Assert.Equal(localCdn.Id, Assert.Single(merged.CdnConfiguration.Profiles).Id);
        var binding = Assert.Single(merged.CdnConfiguration.Bindings);
        Assert.Equal(localStorage.Id, binding.StorageProfileId);
        Assert.Equal(localCdn.Id, binding.CdnProfileId);
    }

    [Fact]
    public void PartialOssCdnImportPreservesOnlySelectedRenamedRelationshipAndIsIdempotent()
    {
        var archivedHangzhou = CreateAliyunProfile("archive-hangzhou", "game-assets");
        var archivedShanghai = CreateAliyunProfile(
            "archive-shanghai",
            "patch-assets",
            "https://oss-cn-shanghai.aliyuncs.com",
            "oss-cn-shanghai");
        var localHangzhou = archivedHangzhou with { Id = Guid.NewGuid(), Name = "local-hangzhou" };
        var localShanghai = archivedShanghai with { Id = Guid.NewGuid(), Name = "local-shanghai" };
        var selectedCredential = CreateCdnCredential() with
        {
            Id = Guid.NewGuid(),
            Name = "archive-selected-token",
            HeaderName = "Legacy-Bearer-Header"
        };
        var ignoredCredential = CreateCdnCredential() with
        {
            Id = Guid.NewGuid(),
            Name = "archive-ignored-token",
            Secret = "ignored-secret"
        };
        var localCredential = selectedCredential with
        {
            Id = Guid.NewGuid(),
            Name = "local-selected-token",
            HeaderName = "Legacy-Bearer-Header"
        };
        var selectedCdn = CreateCdnProfile(selectedCredential.Id) with
        {
            Id = Guid.NewGuid(),
            Name = "archive-selected-cdn",
            Notes = "archive selected note"
        };
        var ignoredCdn = CreateCdnProfile(ignoredCredential.Id) with
        {
            Id = Guid.NewGuid(),
            Name = "archive-ignored-cdn",
            BaseUrl = "https://ignored-cdn.example.test"
        };
        var localCdn = selectedCdn with
        {
            Id = Guid.NewGuid(),
            Name = "local-selected-cdn",
            Notes = "local selected note",
            ControlCredentialId = localCredential.Id
        };
        var archive = _service.Export(
            [archivedHangzhou, archivedShanghai],
            includeCredentials: true,
            password: "portable-password",
            cdnConfiguration: new CdnConfiguration(
                [selectedCdn, ignoredCdn],
                [
                    CreateCdnBinding(archivedHangzhou.Id, selectedCdn.Id),
                    CreateCdnBinding(archivedShanghai.Id, ignoredCdn.Id)
                ]),
            credentials: [selectedCredential, ignoredCredential]);
        var package = _service.Import(archive, "portable-password");
        var selection = new ConnectionArchiveImportSelection([], [selectedCdn.Id]);

        var first = _service.MergePackage(
            [localHangzhou, localShanghai],
            new CdnConfiguration([localCdn], []),
            [localCredential],
            package,
            selection,
            importStorageCredentials: false,
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);
        var second = _service.MergePackage(
            first.Profiles,
            first.CdnConfiguration,
            first.Credentials,
            package,
            selection,
            importStorageCredentials: false,
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);

        Assert.Equal(2, first.Profiles.Count);
        Assert.Equal(localCredential.Id, Assert.Single(first.Credentials).Id);
        Assert.Equal(localCdn.Id, Assert.Single(first.CdnConfiguration.Profiles).Id);
        var binding = Assert.Single(first.CdnConfiguration.Bindings);
        Assert.Equal(localHangzhou.Id, binding.StorageProfileId);
        Assert.Equal(localCdn.Id, binding.CdnProfileId);
        Assert.Equal(first.Profiles, second.Profiles);
        Assert.Equal(first.Credentials, second.Credentials);
        Assert.Equal(first.CdnConfiguration.Profiles, second.CdnConfiguration.Profiles);
        Assert.Equal(first.CdnConfiguration.Bindings, second.CdnConfiguration.Bindings);
    }

    [Fact]
    public void ExportRejectsCdnBindingWhoseStorageConnectionIsOutsideArchive()
    {
        var storage = CreateProfile();
        var cdn = CreateCdnProfile(Guid.NewGuid()) with { ControlCredentialId = null };
        var orphan = CreateCdnBinding(Guid.NewGuid(), cdn.Id);

        var exception = Assert.Throws<InvalidDataException>(() => _service.Export(
            [storage],
            cdnConfiguration: new CdnConfiguration([cdn], [orphan])));

        Assert.Contains("不在连接包内", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportRejectsTamperedCdnBindingWhoseStorageConnectionIsMissing()
    {
        var storage = CreateProfile();
        var cdn = CreateCdnProfile(Guid.NewGuid()) with { ControlCredentialId = null };
        var archive = _service.Export(
            [storage],
            cdnConfiguration: new CdnConfiguration(
                [cdn],
                [CreateCdnBinding(storage.Id, cdn.Id)]));
        var envelope = JsonNode.Parse(archive)!.AsObject();
        envelope["cdnBindings"]!.AsArray()[0]!["storageProfileId"] = Guid.NewGuid();

        var exception = Assert.Throws<InvalidDataException>(() =>
            _service.Import(JsonSerializer.SerializeToUtf8Bytes(envelope)));

        Assert.Contains("不在连接包内", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCredentialIdIsPreservedForAliyunStorageAndAliyunCdnRoundTripAndMerge()
    {
        var sharedCredential = new CredentialProfile
        {
            Name = "aliyun-shared",
            Provider = CredentialProviderKind.AlibabaCloud,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = "aliyun-access-id",
            Secret = "aliyun-secret-key"
        };
        var storage = CreateAliyunProfile("archived-oss", "game-assets") with
        {
            Id = Guid.NewGuid(),
            CredentialId = sharedCredential.Id,
            CredentialSource = CredentialSourceKind.StoredKeys
        };
        var cdnProfile = CreateCdnProfile(sharedCredential.Id) with
        {
            Id = Guid.NewGuid(),
            Name = "archived-cdn",
            ProviderId = CdnProfile.AlibabaCloudProviderId,
            BaseUrl = "https://cdn.aliyuncs.com"
        };

        var archive = _service.Export(
            [storage],
            includeCredentials: true,
            password: "portable-password",
            cdnConfiguration: new CdnConfiguration(
                [cdnProfile],
                [CreateCdnBinding(storage.Id, cdnProfile.Id)]),
            credentials: [sharedCredential]);
        var package = _service.Import(archive, "portable-password");

        Assert.Single(package.ImportedCredentials);
        Assert.Equal(sharedCredential.Id, Assert.Single(package.ImportedCredentials).Id);
        Assert.Equal(sharedCredential.Id, Assert.Single(package.Profiles).CredentialId);
        Assert.Equal(sharedCredential.Id, Assert.Single(package.ImportedCdnConfiguration.Profiles).ControlCredentialId);

        var merged = _service.MergePackage(
            [],
            CdnConfiguration.Empty,
            [],
            package,
            [storage.Id],
            importCredentials: true,
            ConnectionImportConflictStrategy.Rename);

        var mergedCredential = Assert.Single(merged.Credentials);
        var mergedStorage = Assert.Single(merged.Profiles);
        var mergedCdn = Assert.Single(merged.CdnConfiguration.Profiles);

        Assert.Single(merged.Credentials);
        Assert.Equal(mergedCredential.Id, mergedStorage.CredentialId);
        Assert.Equal(mergedCredential.Id, mergedCdn.ControlCredentialId);
    }

    private static ConnectionProfile CreateProfile() => new()
    {
        Name = "Portable",
        ServiceType = S3ServiceType.Custom,
        Endpoint = "https://storage.example.test",
        Region = "auto",
        SignatureRegion = "us-east-1",
        AccessKey = "access-value",
        SecretKey = "secret-value",
        SessionToken = "session-value",
        DefaultBucket = "external-bucket",
        ExternalBuckets = ["other-bucket"]
    };

    private static (ConnectionProfile Profile, CredentialProfile Credential) CreateUnifiedProfile()
    {
        var source = CreateProfile();
        var credential = new CredentialProfile
        {
            Name = "Portable storage key",
            Provider = CredentialProviderKind.S3Compatible,
            Kind = CredentialKind.AccessKeyPair,
            AccessKeyId = source.AccessKey,
            Secret = source.SecretKey,
            SessionToken = source.SessionToken
        };
        return (source with { CredentialId = credential.Id }, credential);
    }

    private static CredentialProfile CreateCdnCredential(CredentialKind kind = CredentialKind.BearerToken) => new()
    {
        Provider = CredentialProviderKind.GenericHttp,
        Kind = kind,
        Name = "cdn-auth",
        Secret = "cdn-secret-value"
    };

    private static CdnProfile CreateCdnProfile(Guid credentialId) => new()
    {
        Name = "cdn-profile",
        BaseUrl = "https://cdn.example.test",
        Notes = "production delivery",
        ControlCredentialId = credentialId
    };

    private static CdnBinding CreateCdnBinding(Guid storageId, Guid cdnProfileId) => new()
    {
        StorageProfileId = storageId,
        Bucket = "external-bucket",
        SourcePrefix = "assets/",
        CdnProfileId = cdnProfileId,
        CdnPathPrefix = "static/"
    };

    private static ConnectionProfile CreateAliyunProfile(
        string name,
        string bucket,
        string endpoint = "https://oss-cn-hangzhou.aliyuncs.com",
        string region = "oss-cn-hangzhou") =>
        ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with
        {
            Name = name,
            Endpoint = endpoint,
            Region = region,
            SignatureRegion = region,
            AccessKey = $"access-{bucket}",
            SecretKey = $"secret-{bucket}",
            DefaultBucket = bucket,
            ExternalBuckets = [$"{bucket}-backup"]
        };

    private static CdnCertificateCheckResult CreateCertificateCheck(string endpoint)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        return new CdnCertificateCheckResult(
            new Uri(endpoint),
            checkedAt,
            checkedAt.AddDays(-1),
            checkedAt.AddDays(90),
            "CN=cdn.example.test",
            "CN=Test CA",
            "AABBCC",
            "TLS 1.3",
            CdnCertificateProblems.None,
            []);
    }
}
