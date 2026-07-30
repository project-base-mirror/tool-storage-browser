using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ConnectionProfileConfigurationTests
{
    [Fact]
    public void RemoveGroupKeepsConnectionsAndMovesThemToUngrouped()
    {
        var group = new ConnectionGroup { Name = "Production" };
        var profile = ValidProfile("Primary") with { GroupId = group.Id };
        var configuration = new ConnectionProfileConfiguration([profile], [group]);

        var result = configuration.RemoveGroup(group.Id);

        Assert.Empty(result.Groups);
        var kept = Assert.Single(result.Profiles);
        Assert.Equal(profile.Id, kept.Id);
        Assert.Null(kept.GroupId);
    }

    [Fact]
    public void PlaceProfileNormalizesOrderingInsideSourceAndTargetGroups()
    {
        var firstGroup = new ConnectionGroup { Name = "First", SortOrder = 0 };
        var secondGroup = new ConnectionGroup { Name = "Second", SortOrder = 1 };
        var first = ValidProfile("A") with { GroupId = firstGroup.Id, SortOrder = 0 };
        var moved = ValidProfile("B") with { GroupId = firstGroup.Id, SortOrder = 1 };
        var target = ValidProfile("C") with { GroupId = secondGroup.Id, SortOrder = 0 };

        var result = new ConnectionProfileConfiguration([first, moved, target], [firstGroup, secondGroup])
            .PlaceProfile(moved.Id, secondGroup.Id, 0);

        Assert.Equal([first.Id], result.Profiles.Where(item => item.GroupId == firstGroup.Id).OrderBy(item => item.SortOrder).Select(item => item.Id));
        Assert.Equal([moved.Id, target.Id], result.Profiles.Where(item => item.GroupId == secondGroup.Id).OrderBy(item => item.SortOrder).Select(item => item.Id));
        Assert.Equal([0, 1], result.Profiles.Where(item => item.GroupId == secondGroup.Id).OrderBy(item => item.SortOrder).Select(item => item.SortOrder));
    }

    [Fact]
    public void NormalizeClearsUnknownGroupReferencesWithoutDroppingProfiles()
    {
        var profile = ValidProfile("Orphan") with { GroupId = Guid.NewGuid(), SortOrder = 12 };

        var normalized = new ConnectionProfileConfiguration([profile], []).Normalize();

        var kept = Assert.Single(normalized.Profiles);
        Assert.Null(kept.GroupId);
        Assert.Equal(0, kept.SortOrder);
    }

    private static ConnectionProfile ValidProfile(string name) =>
        ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
        {
            Name = name,
            AccessKey = "access",
            SecretKey = "secret"
        };
}
