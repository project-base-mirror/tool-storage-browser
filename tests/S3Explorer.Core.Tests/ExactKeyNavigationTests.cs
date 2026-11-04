using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ExactKeyNavigationTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/leading//back\\slash/%2F/./../ ")]
    [InlineData(" ")]
    public void PrefixAndUriNeverNormalizeObjectKey(string prefix)
    {
        Assert.Equal(prefix, S3Path.NormalizePrefix(prefix));
        Assert.Equal(prefix, ObjectTransferPlanner.NormalizePrefix(prefix));
        var location = new S3Location("profile", "bucket", prefix);
        Assert.Equal(prefix, S3Location.Parse(location.ToString()).Prefix);
        Assert.Equal(prefix + "file", S3Path.Combine(prefix, "file"));
    }

    [Theory]
    [InlineData("/", "")]
    [InlineData("//", "/")]
    [InlineData("a//", "a/")]
    [InlineData("a///", "a//")]
    [InlineData("/a/../", "/a/")]
    [InlineData("/a/./", "/a/")]
    [InlineData("back\\slash/", "")]
    [InlineData("a/ ", "a/")]
    public void UpRemovesExactlyOneSegment(string prefix, string parent)
    {
        Assert.Equal(parent, S3Path.ParentPrefix(prefix));
        Assert.Equal(parent, new S3Location("profile", "bucket", prefix).Parent().Prefix);
    }

    [Theory]
    [InlineData("/", true, "")]
    [InlineData("root//", true, "")]
    [InlineData("root/../", true, "..")]
    [InlineData("root/./", true, ".")]
    [InlineData("root/back\\slash.txt", false, "back\\slash.txt")]
    [InlineData("root/%2F.txt", false, "%2F.txt")]
    public void LiteralLeafIsIndependentOfDisplayLabel(string key, bool directory, string leaf)
    {
        Assert.Equal(leaf, S3Path.LeafName(key, directory));
        if (leaf is "" or "." or "..")
            Assert.NotEqual(leaf, S3Path.DisplayName(key, directory));
    }

    [Theory]
    [InlineData("/target//", "back\\slash.txt", null, "/target//back\\slash.txt")]
    [InlineData("/target//", "", "/file%20.txt", "/target////file%20.txt")]
    [InlineData("dest", "..", "./file", "dest/.././file")]
    [InlineData("", " ", null, " ")]
    [InlineData("dest/", "folder", "", "dest/folder/")]
    public void TransfersPreserveEveryLiteralSegment(string prefix, string name, string? relative, string expected)
    {
        Assert.Equal(expected, ObjectTransferPlanner.BuildDestinationKey(prefix, name, relative));
    }

    [Fact]
    public void EmptyDirectoryAndFolderMarkerAreNotCollapsed()
    {
        Assert.Equal("dest//", ObjectTransferPlanner.BuildDirectoryDestinationKey("dest/", ""));
        Assert.Equal("/child", ObjectTransferPlanner.GetRelativePath("source//", "source///child"));
        Assert.Equal("", ObjectTransferPlanner.GetRelativePath("source//", "source//"));
        Assert.Equal("source/ (2)/", ObjectTransferPlanner.GetAutoRenameCandidate("source//", 2));
        ObjectTransferPlanner.ValidateDestination("bucket", "source/", true, "bucket", "/source/");
        Assert.Throws<InvalidOperationException>(() => ObjectTransferPlanner.ValidateDestination(
            "bucket", "source//", true, "bucket", "source///"));
    }

    [Fact]
    public void FolderCreationAndRenameDoNotTrimUserKey()
    {
        Assert.Equal("/root// back\\slash /", S3Path.FolderMarker("/root//", " back\\slash "));
        Assert.Equal("/root///", S3Path.FolderMarker("/root//", "/"));
        Assert.Equal("/root///new%20\\ ", S3Path.RenameLeaf("/root//old", "/new%20\\ "));
    }

    [Theory]
    [InlineData("/file")]
    [InlineData("a//file")]
    [InlineData("back\\slash.txt")]
    public void SyncRejectsLossyRemoteRelativePaths(string relative)
    {
        Assert.Throws<InvalidOperationException>(() => FolderSyncSnapshot.FromRemote("root/", [
            new("root/" + relative, "display", 1, false, null, "STANDARD")
        ]));
    }

    [Fact]
    public async Task RecursiveListingVisitsLeadingAndEmptySegmentsVerbatim()
    {
        var visited = new List<string>();
        var prefixes = new[] { "/", "//", "//back\\slash/", "//back\\slash/../" };
        var objects = await RecursiveObjectListing.ListFilesAsync("/", 100, 100, (prefix, _, _) =>
        {
            visited.Add(prefix);
            var index = Array.IndexOf(prefixes, prefix);
            Assert.InRange(index, 0, prefixes.Length - 1);
            var key = index + 1 < prefixes.Length ? prefixes[index + 1] : prefix + "%2F.txt";
            return Task.FromResult(new PagedObjectResult([
                new(key, S3Path.DisplayName(key, index + 1 < prefixes.Length), 1,
                    index + 1 < prefixes.Length, null, "STANDARD")
            ], null, false));
        }, TestContext.Current.CancellationToken);
        Assert.Equal(prefixes, visited);
        Assert.Equal("//back\\slash/../%2F.txt", Assert.Single(objects).Key);
    }

    [Fact]
    public void WhitespaceObjectKeysAreValidTransferIdentities()
    {
        new TransferTaskRecord
        {
            ProfileId = Guid.NewGuid(), ProfileName = "profile", Bucket = "bucket", ObjectKey = " ",
            Direction = TransferDirection.Copy, DestinationBucket = "other", DestinationObjectKey = "  "
        }.Validate();
    }
}
