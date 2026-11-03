using System.Text.Json;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class CredentialChangePlanTests
{
    [Fact]
    public void ProviderChangeClearsOnlyConfirmedReferencesAndKeepsCredentialAndCdnContentAuthentication()
    {
        var (original, credential, storage, cdn) = CreateAliyunConfiguration();
        var changed = credential with { Provider = CredentialProviderKind.AmazonWebServices };
        var plan = CredentialChangePlan.Create(original, [changed, original.CredentialVault[1]]);
        Assert.Equal(2, plan.IncompatibleReferences.Count);
        Assert.Contains(plan.IncompatibleReferences, value => value.Kind == CredentialReferenceKind.StorageAccessKey);
        Assert.Contains(plan.IncompatibleReferences, value => value.Kind == CredentialReferenceKind.CdnControl);
        Assert.All(plan.IncompatibleReferences, value => Assert.DoesNotContain(credential.Secret, value.Display));

        var updated = plan.Apply(original, clearIncompatibleReferences: true);
        var cleared = updated.Storage.Profiles.Single(value => value.Id == storage.Id);
        Assert.Null(cleared.CredentialId);
        Assert.Empty(cleared.AccessKey);
        Assert.Empty(cleared.SecretKey);
        Assert.Empty(cleared.SessionToken);
        Assert.Null(updated.Cdn.Profiles.Single(value => value.Id == cdn.Id).ControlCredentialId);
        Assert.Equal(cdn.ContentAuthentication, updated.Cdn.Profiles[0].ContentAuthentication);
        Assert.Equal(original.Storage.Profiles[1], updated.Storage.Profiles[1]);
        Assert.Equal(original.Cdn.Profiles[1], updated.Cdn.Profiles[1]);
        Assert.Same(original.Cdn.Bindings, updated.Cdn.Bindings);
        Assert.Same(original.Storage.Groups, updated.Storage.Groups);
        Assert.Equal(changed, updated.FindCredential(credential.Id));
        Assert.Equal(original.CredentialVault.Count, updated.CredentialVault.Count);
        updated.ResolveCredentialReferences();
        updated.ToPersistentSnapshot();
        Assert.Equal(credential.Id, storage.CredentialId);
        Assert.Equal(credential.Id, cdn.ControlCredentialId);
    }

    [Fact]
    public void NoConfirmationLeavesOriginalUnchanged()
    {
        var (original, credential, _, _) = CreateAliyunConfiguration();
        var plan = CredentialChangePlan.Create(original, [credential with { Provider = CredentialProviderKind.AmazonWebServices }, original.CredentialVault[1]]);
        var exception = Assert.Throws<InvalidOperationException>(() => plan.Apply(original));
        Assert.Contains("必须先确认", exception.Message);
        Assert.Equal(credential, original.FindCredential(credential.Id));
        Assert.All(original.Storage.Profiles, value => Assert.NotNull(value.CredentialId));
        Assert.All(original.Cdn.Profiles, value => Assert.NotNull(value.ControlCredentialId));
    }

    [Fact]
    public void CompatibleSecretAndNameEditsDoNotClearReferences()
    {
        var (original, credential, _, _) = CreateAliyunConfiguration();
        var changed = credential with { Name = "renamed", Secret = "rotated-secret" };
        var plan = CredentialChangePlan.Create(original, [changed, original.CredentialVault[1]]);
        Assert.Empty(plan.IncompatibleReferences);
        var updated = plan.Apply(original).ResolveCredentialReferences();
        Assert.Equal(credential.Id, updated.Storage.Profiles[0].CredentialId);
        Assert.Equal("rotated-secret", updated.Storage.Profiles[0].SecretKey);
        Assert.Equal(credential.Id, updated.Cdn.Profiles[0].ControlCredentialId);
    }

    [Fact]
    public void CloudflareAccessKeyToBearerClearsR2ReferenceWithoutDeletingCredential()
    {
        var credential = Credential(CredentialProviderKind.Cloudflare);
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2) with
        {
            Name = "R2", Endpoint = "https://example.r2.cloudflarestorage.com", CredentialId = credential.Id,
            AccessKey = credential.AccessKeyId, SecretKey = credential.Secret, SessionToken = "temporary-session"
        };
        var original = new ExplorerConfiguration(new([profile], []), CdnConfiguration.Empty, [credential]);
        var changed = credential with { Kind = CredentialKind.BearerToken, AccessKeyId = "", SessionToken = "" };
        var plan = CredentialChangePlan.Create(original, [changed]);
        Assert.Single(plan.IncompatibleReferences);
        var updated = plan.Apply(original, true);
        Assert.Null(updated.Storage.Profiles[0].CredentialId);
        Assert.Empty(updated.Storage.Profiles[0].SessionToken);
        Assert.Equal(changed, Assert.Single(updated.CredentialVault));
        updated.ResolveCredentialReferences();
    }

    [Fact]
    public void ExternalIdKindChangeClearsReferenceAndResolvedSecretOnly()
    {
        var credential = Credential(CredentialProviderKind.AmazonWebServices) with
        {
            Kind = CredentialKind.SecretValue, AccessKeyId = ""
        };
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Assume role", CredentialSource = CredentialSourceKind.AwsAssumeRole,
            AwsSourceProfileName = "base", AwsRoleArn = "arn:aws:iam::123456789012:role/test", AwsRoleSessionName = "test-session",
            AwsExternalIdCredentialId = credential.Id, AwsExternalId = credential.Secret
        };
        var original = new ExplorerConfiguration(new([profile], []), CdnConfiguration.Empty, [credential]);
        var changed = credential with { Kind = CredentialKind.AccessKeyPair, AccessKeyId = "new-access" };
        var plan = CredentialChangePlan.Create(original, [changed]);
        Assert.Equal(CredentialReferenceKind.AwsExternalId, Assert.Single(plan.IncompatibleReferences).Kind);
        var updated = plan.Apply(original, true);
        Assert.Null(updated.Storage.Profiles[0].AwsExternalIdCredentialId);
        Assert.Empty(updated.Storage.Profiles[0].AwsExternalId);
        Assert.Equal(profile.AwsSourceProfileName, updated.Storage.Profiles[0].AwsSourceProfileName);
        Assert.Equal(profile.AwsRoleArn, updated.Storage.Profiles[0].AwsRoleArn);
        Assert.Equal(changed, Assert.Single(updated.CredentialVault));
        updated.ResolveCredentialReferences();
    }

    [Theory]
    [InlineData("a")]
    [InlineData("bad value")]
    public void InvalidExternalIdSecretEditFailsPreviewBeforeAnyApply(string invalidSecret)
    {
        var credential = Credential(CredentialProviderKind.AmazonWebServices) with
        {
            Kind = CredentialKind.SecretValue, AccessKeyId = "", Secret = "valid-external-id"
        };
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "Assume role", CredentialSource = CredentialSourceKind.AwsAssumeRole,
            AwsSourceProfileName = "base", AwsRoleArn = "arn:aws:iam::123456789012:role/test", AwsRoleSessionName = "test-session",
            AwsExternalIdCredentialId = credential.Id, AwsExternalId = credential.Secret
        };
        var original = new ExplorerConfiguration(new([profile], []), CdnConfiguration.Empty, [credential]);
        Assert.Throws<ArgumentException>(() => CredentialChangePlan.Create(original, [credential with { Secret = invalidSecret }]));
        Assert.Equal("valid-external-id", original.FindCredential(credential.Id)!.Secret);
        Assert.Equal("valid-external-id", original.Storage.Profiles[0].AwsExternalId);
    }

    [Fact]
    public void NewlyReferencedExternalIdRevalidatesValueInsideAtomicApply()
    {
        var credential = Credential(CredentialProviderKind.AmazonWebServices) with
        {
            Kind = CredentialKind.SecretValue, AccessKeyId = "", Secret = "valid-external-id"
        };
        var original = ExplorerConfiguration.Empty with { CredentialVault = new[] { credential } };
        // A short SecretValue is valid in isolation, but cannot be an AWS External ID.
        var plan = CredentialChangePlan.Create(original, [credential with { Secret = "a" }]);
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
        {
            Name = "new role", CredentialSource = CredentialSourceKind.AwsAssumeRole,
            AwsSourceProfileName = "base", AwsRoleArn = "arn:aws:iam::123456789012:role/test", AwsRoleSessionName = "test-session",
            AwsExternalIdCredentialId = credential.Id, AwsExternalId = credential.Secret
        };
        var current = original with { Storage = new([profile], []) };
        Assert.Throws<ArgumentException>(() => plan.Apply(current, true));
        Assert.Equal(credential, Assert.Single(current.CredentialVault));
    }

    [Fact]
    public void NewConcurrentReferenceBlocksEntireChangeEvenWhenClearingWasConfirmed()
    {
        var (original, credential, storage, _) = CreateAliyunConfiguration();
        var plan = CredentialChangePlan.Create(original, [credential with { Provider = CredentialProviderKind.AmazonWebServices }, original.CredentialVault[1]]);
        var current = original with { Storage = original.Storage with
        {
            Profiles = [.. original.Storage.Profiles, storage with { Id = Guid.NewGuid(), Name = "new reference" }]
        } };
        var exception = Assert.Throws<InvalidOperationException>(() => plan.Apply(current, true));
        Assert.Contains("新增或变更", exception.Message);
        Assert.Contains("new reference", exception.Message);
        Assert.Equal(credential, current.FindCredential(credential.Id));
        Assert.All(current.Storage.Profiles, value => Assert.NotNull(value.CredentialId));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("endpoint")]
    [InlineData("bucket")]
    public void ChangedConcurrentStorageTargetNeedsFreshConfirmation(string change)
    {
        var (original, credential, storage, _) = CreateAliyunConfiguration();
        var plan = CredentialChangePlan.Create(original, [credential with { Provider = CredentialProviderKind.AmazonWebServices }, original.CredentialVault[1]]);
        var changed = change switch
        {
            "name" => storage with { Name = "renamed target" },
            "endpoint" => storage with { Endpoint = "https://other.oss-cn-hangzhou.aliyuncs.com" },
            _ => storage with { ExternalBuckets = ["new-bucket"] }
        };
        var current = original with { Storage = original.Storage with { Profiles = [changed, original.Storage.Profiles[1]] } };
        Assert.Throws<InvalidOperationException>(() => plan.Apply(current, true));
        Assert.Equal(credential, current.FindCredential(credential.Id));
        Assert.Equal(credential.Id, current.Storage.Profiles[0].CredentialId);
    }

    [Fact]
    public void ChangedConcurrentCdnTargetNeedsFreshConfirmation()
    {
        var (original, credential, _, cdn) = CreateAliyunConfiguration();
        var plan = CredentialChangePlan.Create(original, [credential with { Provider = CredentialProviderKind.AmazonWebServices }, original.CredentialVault[1]]);
        var current = original with { Cdn = original.Cdn with
        {
            Profiles = [cdn with { BaseUrl = "https://new.example.com" }, original.Cdn.Profiles[1]]
        } };
        Assert.Throws<InvalidOperationException>(() => plan.Apply(current, true));
        Assert.Equal(credential.Id, current.Cdn.Profiles[0].ControlCredentialId);
    }

    [Fact]
    public void ConcurrentRetargetedReferenceIsPreserved()
    {
        var (original, credential, storage, cdn) = CreateAliyunConfiguration();
        var plan = CredentialChangePlan.Create(original, [credential with { Provider = CredentialProviderKind.AmazonWebServices }, original.CredentialVault[1]]);
        var otherId = original.CredentialVault[1].Id;
        var current = original with
        {
            Storage = original.Storage with { Profiles = [storage with { CredentialId = otherId }, original.Storage.Profiles[1]] },
            Cdn = original.Cdn with { Profiles = [cdn with { ControlCredentialId = otherId }, original.Cdn.Profiles[1]] }
        };
        var updated = plan.Apply(current, true);
        Assert.Equal(otherId, updated.Storage.Profiles[0].CredentialId);
        Assert.Equal(otherId, updated.Cdn.Profiles[0].ControlCredentialId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentCredentialChangeOrDeletionBlocksStaleEdit(bool deleted)
    {
        var credential = Credential(CredentialProviderKind.AmazonWebServices);
        var original = ExplorerConfiguration.Empty with { CredentialVault = new[] { credential } };
        var plan = CredentialChangePlan.Create(original, [credential with { Name = "edited" }]);
        var current = original with { CredentialVault = deleted ? [] : [credential with { Secret = "concurrent rotation" }] };
        Assert.Throws<InvalidOperationException>(() => plan.Apply(current, true));
        Assert.DoesNotContain(current.CredentialVault, value => value.Name == "edited");
    }

    [Fact]
    public void MergePreservesUnrelatedConcurrentCredentialEditsAndAdditions()
    {
        var (original, credential, _, _) = CreateAliyunConfiguration();
        var changed = credential with { Name = "edited" };
        var plan = CredentialChangePlan.Create(original, [changed, original.CredentialVault[1]]);
        var concurrent = original.CredentialVault[1] with { Name = "concurrently renamed", Secret = "new secret" };
        var added = Credential(CredentialProviderKind.AmazonWebServices) with { Name = "concurrently added" };
        var current = original with { CredentialVault = new[] { credential, concurrent, added } };
        var updated = plan.Apply(current);
        Assert.Equal(changed, updated.FindCredential(credential.Id));
        Assert.Equal(concurrent, updated.FindCredential(concurrent.Id));
        Assert.Equal(added, updated.FindCredential(added.Id));
    }

    [Fact]
    public void ReloadedEquivalentProfilesAndRuntimeSecretsDoNotCauseFalseConflict()
    {
        var (original, credential, _, _) = CreateAliyunConfiguration();
        var plan = CredentialChangePlan.Create(original, [credential with { Provider = CredentialProviderKind.AmazonWebServices }, original.CredentialVault[1]]);
        var reloaded = JsonSerializer.Deserialize<ExplorerConfiguration>(JsonSerializer.Serialize(original.ToPersistentSnapshot()))!;
        var updated = plan.Apply(reloaded.ResolveCredentialReferences(), true);
        Assert.Null(updated.Storage.Profiles[0].CredentialId);
        Assert.Null(updated.Cdn.Profiles[0].ControlCredentialId);
    }

    [Fact]
    public void ReferencedCredentialDeletionCannotUseReferenceClearingAsBypass()
    {
        var (original, _, _, _) = CreateAliyunConfiguration();
        Assert.Throws<InvalidDataException>(() => CredentialChangePlan.Create(original, [original.CredentialVault[1]]));
        Assert.Equal(2, original.CredentialVault.Count);
    }

    [Fact]
    public void ExplicitUnreferencedDeletionCannotDeleteCredentialReferencedAfterPreview()
    {
        var credential = Credential(CredentialProviderKind.AmazonWebServices);
        var original = ExplorerConfiguration.Empty with { CredentialVault = new[] { credential } };
        var plan = CredentialChangePlan.Create(original, []);
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with { Name = "new", CredentialId = credential.Id };
        var current = original with { Storage = new([profile], []) };
        Assert.Throws<InvalidDataException>(() => plan.Apply(current, true));
        Assert.Equal(credential, Assert.Single(current.CredentialVault));
    }

    private static CredentialProfile Credential(CredentialProviderKind provider) => new()
    {
        Name = "shared", Provider = provider, Kind = CredentialKind.AccessKeyPair,
        AccessKeyId = "synthetic-access", Secret = "synthetic-secret"
    };

    private static (ExplorerConfiguration Configuration, CredentialProfile Credential, ConnectionProfile Storage, CdnProfile Cdn)
        CreateAliyunConfiguration()
    {
        var credential = Credential(CredentialProviderKind.AlibabaCloud);
        var other = credential with { Id = Guid.NewGuid(), Name = "unrelated" };
        var storage = ConnectionProfile.CreatePreset(S3ServiceType.AliyunOss) with
        {
            Name = "affected storage", CredentialId = credential.Id,
            AccessKey = credential.AccessKeyId, SecretKey = credential.Secret, SessionToken = "runtime-session"
        };
        var otherStorage = storage with { Id = Guid.NewGuid(), Name = "other storage", CredentialId = other.Id };
        var cdn = new CdnProfile
        {
            Name = "affected CDN", ProviderId = CdnProfile.AlibabaCloudProviderId,
            BaseUrl = "https://cdn.example.com", ControlCredentialId = credential.Id,
            ContentAuthentication = new() { AuthenticationType = CdnAuthenticationType.BearerToken, Secret = "inline-content-secret" }
        };
        var otherCdn = cdn with { Id = Guid.NewGuid(), Name = "other CDN", ControlCredentialId = other.Id };
        var binding = new CdnBinding { StorageProfileId = storage.Id, CdnProfileId = cdn.Id, Bucket = "bucket" };
        return (new(new([storage, otherStorage], []), new([cdn, otherCdn], [binding]), [credential, other]), credential, storage, cdn);
    }
}
