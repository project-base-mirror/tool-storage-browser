using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ExplorerConfigurationMergerTests
{
    [Fact]
    public void CdnDialogMergePreservesConcurrentRecordsAndRejectsSameRecordChanges()
    {
        var original = new CdnProfile { Name = "original", BaseUrl = "https://cdn.example.test" };
        var added = new CdnProfile { Name = "CLI added", BaseUrl = "https://another.example.test" };
        var baseline = ExplorerConfiguration.Empty with { Cdn = new([original], []) };
        var proposed = baseline with { Cdn = new([original with { Name = "GUI renamed" }], []) };
        var current = baseline with { Cdn = new([original, added], []) };

        var merged = ExplorerConfigurationMerger.Merge(baseline, proposed, current);

        Assert.Equal(2, merged.Cdn.Profiles.Count);
        Assert.Contains(merged.Cdn.Profiles, value => value.Id == added.Id);
        Assert.Equal("GUI renamed", merged.Cdn.Profiles.Single(value => value.Id == original.Id).Name);
        current = current with { Cdn = new([original with { BaseUrl = "https://changed.example.test" }, added], []) };
        Assert.Throws<InvalidOperationException>(() => ExplorerConfigurationMerger.Merge(baseline, proposed, current));
    }

    [Fact]
    public void StaleTreeEditPreservesConcurrentConnectionsAndGroups()
    {
        var first = Profile("first");
        var group = new ConnectionGroup { Name = "group" };
        var baseline = new ConnectionProfileConfiguration([first], [group]);
        var added = Profile("CLI added") with { SortOrder = 1 };
        var addedGroup = new ConnectionGroup { Name = "CLI group", SortOrder = 1 };
        var current = new ConnectionProfileConfiguration([first, added], [group, addedGroup]);
        var proposed = baseline.PlaceProfile(first.Id, group.Id, 0);

        var merged = ExplorerConfigurationMerger.MergeStorage(baseline, proposed, current);

        Assert.Equal(group.Id, merged.Profiles.Single(value => value.Id == first.Id).GroupId);
        Assert.Contains(merged.Profiles, value => value.Id == added.Id);
        Assert.Contains(merged.Groups, value => value.Id == addedGroup.Id);
    }

    [Fact]
    public void SameRecordEditOrDeletionConflictsWithoutMutatingCurrent()
    {
        var first = Profile("first");
        var baseline = new ConnectionProfileConfiguration([first], []);
        var current = baseline with { Profiles = [first with { Endpoint = "https://cli.example.test" }] };
        var edited = baseline with { Profiles = [first with { Name = "GUI rename" }] };

        Assert.Throws<InvalidOperationException>(() => ExplorerConfigurationMerger.MergeStorage(baseline, edited, current));
        Assert.Throws<InvalidOperationException>(() => ExplorerConfigurationMerger.MergeStorage(baseline, baseline with { Profiles = [] }, current));
        Assert.Equal("https://cli.example.test", Assert.Single(current.Profiles).Endpoint);
    }

    [Fact]
    public void SameRecordIdenticalEditIsIdempotentAndListsCompareByValue()
    {
        var profile = Profile("first") with { ExternalBuckets = ["bucket-one"] };
        var baseline = new ConnectionProfileConfiguration([profile], []);
        var proposed = baseline with { Profiles = [profile with { Name = "renamed" }] };
        var current = baseline with { Profiles = [profile with { Name = "renamed", ExternalBuckets = new[] { "bucket-one" } }] };

        var merged = ExplorerConfigurationMerger.MergeStorage(baseline, proposed, current);

        Assert.Equal("renamed", Assert.Single(merged.Profiles).Name);
    }

    [Fact]
    public void DeletingUnrelatedConnectionKeepsRotatedSecretsAndConcurrentCdnChanges()
    {
        var credential = Credential("shared", "old-secret");
        var kept = Profile("kept") with { CredentialId = credential.Id };
        var removed = Profile("removed");
        var baseline = new ExplorerConfiguration(new([kept, removed], []), CdnConfiguration.Empty, [credential])
            .ResolveCredentialReferences();
        var cdn = new CdnProfile { Name = "CLI CDN", BaseUrl = "https://cdn.example.test" };
        var current = (baseline with
        {
            CredentialVault = [credential with { Secret = "new-secret" }],
            Cdn = new CdnConfiguration([cdn], [])
        }).ResolveCredentialReferences();
        var proposed = baseline with { Storage = baseline.Storage with { Profiles = [baseline.Storage.Profiles[0]] } };

        var merged = ExplorerConfigurationMerger.Merge(baseline, proposed, current);

        Assert.Equal("new-secret", Assert.Single(merged.Storage.Profiles).SecretKey);
        Assert.Equal(cdn.Id, Assert.Single(merged.Cdn.Profiles).Id);
        Assert.Equal("new-secret", Assert.Single(merged.CredentialVault).Secret);
    }

    [Fact]
    public void TwoCredentialFreeImportsCanBeOrganizedAndRepairedIndividually()
    {
        var first = Profile("first");
        var second = Profile("second");
        var group = new ConnectionGroup { Name = "organize" };
        var firstCredential = Credential("first credential", "first-secret");
        var secondCredential = Credential("second credential", "second-secret");
        var configuration = new ExplorerConfiguration(new([first, second], [group]), CdnConfiguration.Empty,
            [firstCredential, secondCredential]);
        configuration = configuration with { Storage = configuration.Storage.PlaceProfile(first.Id, group.Id, 0) };
        configuration.Storage.ValidateConfiguration();
        Assert.Throws<ArgumentException>(() => configuration.Storage.Validate());
        foreach (var (profile, credential) in new[] { (first, firstCredential), (second, secondCredential) })
        {
            var proposed = configuration with
            {
                Storage = configuration.Storage with
                {
                    Profiles = configuration.Storage.Profiles.Select(value => value.Id == profile.Id
                        ? value with { CredentialId = credential.Id } : value).ToArray()
                }
            };
            configuration = ExplorerConfigurationMerger.Merge(configuration, proposed, configuration);
            Assert.Equal(credential.Secret, configuration.Storage.Profiles.Single(value => value.Id == profile.Id).SecretKey);
        }
        configuration.Storage.Validate();
    }

    [Fact]
    public void ConcurrentCredentialOverwriteConflictsInsteadOfRollingBackRotation()
    {
        var credential = Credential("shared", "original");
        var baseline = new ExplorerConfiguration(ConnectionProfileConfiguration.Empty, CdnConfiguration.Empty, [credential]);
        var proposed = baseline with { CredentialVault = [credential with { Secret = "imported" }] };
        var current = baseline with { CredentialVault = [credential with { Secret = "rotated" }] };

        Assert.Throws<InvalidOperationException>(() => ExplorerConfigurationMerger.Merge(baseline, proposed, current));
        Assert.Equal("rotated", Assert.Single(current.CredentialVault).Secret);
    }

    [Fact]
    public void RemovingGroupRejectsUnseenConcurrentMembersRatherThanDetachingThem()
    {
        var group = new ConnectionGroup { Name = "group" };
        var baseline = new ConnectionProfileConfiguration([], [group]);
        var current = baseline with { Profiles = [Profile("CLI member") with { GroupId = group.Id }] };

        Assert.Throws<InvalidDataException>(() => ExplorerConfigurationMerger.MergeStorage(
            baseline, baseline.RemoveGroup(group.Id), current));
    }

    [Fact]
    public void StructuralValidationStillRejectsDuplicateAndDanglingRecords()
    {
        var profile = Profile("placeholder");
        Assert.Throws<InvalidDataException>(() => new ConnectionProfileConfiguration([profile, profile], []).ValidateConfiguration());
        Assert.Throws<InvalidDataException>(() => new ConnectionProfileConfiguration([profile with { GroupId = Guid.NewGuid() }], []).ValidateConfiguration());
        Assert.Throws<InvalidDataException>(() => new ConnectionProfileConfiguration([profile with { Id = Guid.Empty }], []).ValidateConfiguration());
    }

    private static ConnectionProfile Profile(string name) => new() { Name = name };
    private static CredentialProfile Credential(string name, string secret) => new()
    {
        Name = name, Provider = CredentialProviderKind.AmazonWebServices,
        Kind = CredentialKind.AccessKeyPair, AccessKeyId = "test-access", Secret = secret
    };
}
