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
        var archive = _service.Export([CreateProfile()], includeCredentials: true, password: "portable-password");
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
        var archive = _service.Export([CreateProfile()], includeCredentials: true, password: "portable-password");

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
        var archive = _service.Export([CreateProfile()], includeCredentials: true, password: "portable-password");
        var text = Encoding.UTF8.GetString(archive);
        var payloadMarker = "\"encryptedPayload\": \"";
        var payloadStart = text.IndexOf(payloadMarker, StringComparison.Ordinal) + payloadMarker.Length;
        var changed = text[payloadStart] == 'A' ? 'B' : 'A';
        var tampered = Encoding.UTF8.GetBytes(text[..payloadStart] + changed + text[(payloadStart + 1)..]);

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

        Assert.Contains("\"version\": 3", text, StringComparison.Ordinal);
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

    [Fact]
    public void CredentialFreeExportKeepsCdnConfigurationButOmitsCdnSecretsAndReferences()
    {
        var storage = CreateProfile();
        var credential = CreateCdnCredential();
        var cdnProfile = CreateCdnProfile(credential.Id);
        var configuration = new CdnConfiguration(
            [cdnProfile],
            [CreateCdnBinding(storage.Id, cdnProfile.Id)]);

        var archive = _service.Export(
            [storage],
            cdnConfiguration: configuration,
            cdnCredentials: [credential]);
        var json = Encoding.UTF8.GetString(archive);
        var inspection = _service.Inspect(archive);
        var package = _service.Import(archive);

        Assert.Contains("https://cdn.example.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn-secret-value", json, StringComparison.Ordinal);
        Assert.Equal(1, inspection.CdnProfileCount);
        Assert.Equal(0, inspection.CdnCredentialCount);
        Assert.Null(Assert.Single(package.ImportedCdnConfiguration.Profiles).CredentialId);
        Assert.Single(package.ImportedCdnConfiguration.Bindings);
        Assert.Empty(package.ImportedCdnCredentials);
    }

    [Fact]
    public void PasswordProtectedExportMovesCdnSecretsWithoutExposingThemInEnvelope()
    {
        var storage = CreateProfile();
        var credential = CreateCdnCredential();
        var cdnProfile = CreateCdnProfile(credential.Id);
        var configuration = new CdnConfiguration(
            [cdnProfile],
            [CreateCdnBinding(storage.Id, cdnProfile.Id)]);

        var archive = _service.Export(
            [storage],
            includeCredentials: true,
            password: "portable-password",
            cdnConfiguration: configuration,
            cdnCredentials: [credential]);
        var json = Encoding.UTF8.GetString(archive);
        var inspection = _service.Inspect(archive);
        var package = _service.Import(archive, "portable-password");

        Assert.DoesNotContain("cdn-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn-auth", json, StringComparison.Ordinal);
        Assert.True(inspection.ContainsCredentials);
        Assert.Equal(1, inspection.CdnCredentialCount);
        Assert.Equal("cdn-secret-value", Assert.Single(package.ImportedCdnCredentials).Secret);
        Assert.Equal(
            Assert.Single(package.ImportedCdnCredentials).Id,
            Assert.Single(package.ImportedCdnConfiguration.Profiles).CredentialId);
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
        var importedCredential = Assert.Single(merged.CdnCredentials);
        var importedCdn = Assert.Single(merged.CdnConfiguration.Profiles);
        var importedBinding = Assert.Single(merged.CdnConfiguration.Bindings);
        Assert.NotEqual(storage.Id, importedStorage.Id);
        Assert.NotEqual(credential.Id, importedCredential.Id);
        Assert.NotEqual(cdnProfile.Id, importedCdn.Id);
        Assert.Equal(importedCredential.Id, importedCdn.CredentialId);
        Assert.Equal(importedStorage.Id, importedBinding.StorageProfileId);
        Assert.Equal(importedCdn.Id, importedBinding.CdnProfileId);
        CdnConfigurationValidator.EnsureValid(merged.CdnConfiguration, merged.CdnCredentials);
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

        Assert.Empty(merged.CdnCredentials);
        Assert.Null(Assert.Single(merged.CdnConfiguration.Profiles).CredentialId);
        CdnConfigurationValidator.EnsureValid(merged.CdnConfiguration, merged.CdnCredentials);
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
        var mergedCredential = Assert.Single(merged.CdnCredentials);
        var mergedCdn = Assert.Single(merged.CdnConfiguration.Profiles);
        var mergedBinding = Assert.Single(merged.CdnConfiguration.Bindings);
        Assert.Equal(existingCredential.Id, mergedCredential.Id);
        Assert.Equal("cdn-secret-value", mergedCredential.Secret);
        Assert.Equal(existingCdn.Id, mergedCdn.Id);
        Assert.Equal("https://cdn.example.test", mergedCdn.BaseUrl);
        Assert.Equal(existingCredential.Id, mergedCdn.CredentialId);
        Assert.Equal(existingBinding.Id, mergedBinding.Id);
        Assert.Equal(existingStorage.Id, mergedBinding.StorageProfileId);
        Assert.Equal(existingCdn.Id, mergedBinding.CdnProfileId);
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

    private static CdnCredential CreateCdnCredential() => new()
    {
        Name = "cdn-auth",
        AuthenticationType = CdnAuthenticationType.BearerToken,
        Secret = "cdn-secret-value"
    };

    private static CdnProfile CreateCdnProfile(Guid credentialId) => new()
    {
        Name = "cdn-profile",
        BaseUrl = "https://cdn.example.test",
        CredentialId = credentialId
    };

    private static CdnBinding CreateCdnBinding(Guid storageId, Guid cdnProfileId) => new()
    {
        StorageProfileId = storageId,
        Bucket = "external-bucket",
        SourcePrefix = "assets/",
        CdnProfileId = cdnProfileId,
        CdnPathPrefix = "static/"
    };
}
