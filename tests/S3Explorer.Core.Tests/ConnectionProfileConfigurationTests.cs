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

    [Theory]
    [InlineData(0, 2, true, "B,C,A")]
    [InlineData(0, 2, false, "B,A,C")]
    [InlineData(2, 0, false, "C,A,B")]
    [InlineData(2, 0, true, "A,C,B")]
    [InlineData(0, 1, false, "A,B,C")]
    [InlineData(1, 1, true, "A,B,C")]
    public void RelativePlacementHandlesBothDirectionsAndNoOps(int source, int target, bool after, string expected)
    {
        var profiles = new[] { "A", "B", "C" }.Select((name, order) => ValidProfile(name) with { SortOrder = order }).ToArray();
        var result = new ConnectionProfileConfiguration(profiles, []).PlaceProfileRelative(profiles[source].Id, profiles[target].Id, after);
        Assert.Equal(expected, string.Join(",", result.Profiles.OrderBy(item => item.SortOrder).Select(item => item.Name)));
        Assert.Equal([0, 1, 2], result.Profiles.OrderBy(item => item.SortOrder).Select(item => item.SortOrder));
        Assert.Equal([0, 1, 2], profiles.Select(item => item.SortOrder));
        result.Validate();
    }

    [Fact]
    public void RelativePlacementMovesAcrossGroupsAndBackToUngroupedWithoutChangingConnectionDetails()
    {
        var group = new ConnectionGroup { Name = "Group", IsExpanded = false };
        var source = ValidProfile("Source") with { SessionToken = "test-token", DefaultBucket = "bucket" };
        var anchor = ValidProfile("Anchor") with { GroupId = group.Id };
        var ungrouped = ValidProfile("Ungrouped") with { SortOrder = 1 };
        var configuration = new ConnectionProfileConfiguration([source, anchor, ungrouped], [group]);
        var grouped = configuration.PlaceProfileRelative(source.Id, anchor.Id, true);
        Assert.Equal([anchor.Id, source.Id], grouped.Profiles.Where(item => item.GroupId == group.Id).OrderBy(item => item.SortOrder).Select(item => item.Id));
        Assert.Equal(source with { GroupId = group.Id, SortOrder = 1 }, grouped.Profiles.Single(item => item.Id == source.Id));
        var restored = grouped.PlaceProfileRelative(source.Id, ungrouped.Id, false);
        Assert.Equal(source, restored.Profiles.Single(item => item.Id == source.Id));
        Assert.False(Assert.Single(restored.Groups).IsExpanded);
    }

    [Theory]
    [InlineData(false, "B,A,C")]
    [InlineData(true, "B,C,A")]
    public void RelativeGroupPlacementKeepsMembersAndExpansion(bool after, string expected)
    {
        var groups = new[] { "A", "B", "C" }.Select((name, order) => new ConnectionGroup { Name = name, SortOrder = order, IsExpanded = false }).ToArray();
        var profile = ValidProfile("Member") with { GroupId = groups[0].Id };
        var configuration = new ConnectionProfileConfiguration([profile], groups);
        var result = configuration.PlaceGroupRelative(groups[0].Id, groups[2].Id, after);
        Assert.Equal(expected, string.Join(",", result.Groups.Select(item => item.Name)));
        Assert.Equal(profile, Assert.Single(result.Profiles));
        Assert.All(result.Groups, item => Assert.False(item.IsExpanded));
        Assert.Same(configuration, configuration.PlaceGroupRelative(groups[0].Id, groups[0].Id, true));
    }

    [Fact]
    public void InvalidMoveTargetsFailWithoutChangingConfiguration()
    {
        var group = new ConnectionGroup { Name = "Group" };
        var profile = ValidProfile("Profile");
        var configuration = new ConnectionProfileConfiguration([profile], [group]);
        Assert.Throws<ArgumentException>(() => configuration.PlaceProfileRelative(profile.Id, Guid.NewGuid(), false));
        Assert.Throws<ArgumentException>(() => configuration.PlaceProfileRelative(Guid.NewGuid(), profile.Id, false));
        Assert.Throws<ArgumentException>(() => configuration.PlaceProfile(profile.Id, Guid.NewGuid(), 0));
        Assert.Throws<ArgumentException>(() => configuration.PlaceGroupRelative(group.Id, Guid.NewGuid(), false));
        Assert.Equal(profile, Assert.Single(configuration.Profiles));
    }

    private static ConnectionProfile ValidProfile(string name) =>
        ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
        {
            Name = name,
            AccessKey = "access",
            SecretKey = "secret"
        };
}
