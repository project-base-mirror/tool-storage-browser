using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ObjectTransferPlannerTests
{
    [Fact]
    public void DestinationKeyPreservesUnicodeSpacesPlusAndPercent()
    {
        var key = ObjectTransferPlanner.BuildDestinationKey(
            "目标 目录/+/%", "源 文件夹", "子目录/文件 + 100%.txt");

        Assert.Equal("目标 目录/+/%/源 文件夹/子目录/文件 + 100%.txt", key);
    }

    [Theory]
    [InlineData("folder/", "folder/")]
    [InlineData("folder/", "folder/child/")]
    public void FolderCannotTargetItselfOrDescendant(string sourceKey, string destinationKey)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ObjectTransferPlanner.ValidateDestination(
                "bucket", sourceKey, true, "bucket", destinationKey));

        Assert.Contains("自身或其后代", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossBucketFolderMayKeepSameKey()
    {
        ObjectTransferPlanner.ValidateDestination(
            "source", "folder/", true, "destination", "folder/");
    }

    [Theory]
    [InlineData("folder/file.txt", 2, "folder/file (2).txt")]
    [InlineData("folder/archive", 3, "folder/archive (3)")]
    [InlineData("folder/sub/", 4, "folder/sub (4)/")]
    public void AutoRenamePreservesParentAndExtension(string key, int sequence, string expected)
    {
        Assert.Equal(expected, ObjectTransferPlanner.GetAutoRenameCandidate(key, sequence));
    }

    [Fact]
    public void CopyTaskUsesDestinationAndDoesNotRequireLocalPath()
    {
        var task = new TransferTaskRecord
        {
            ProfileId = Guid.NewGuid(),
            ProfileName = "profile",
            Direction = TransferDirection.Copy,
            Kind = TransferTaskKind.ObjectTransfer,
            Bucket = "source",
            ObjectKey = "a.txt",
            DestinationBucket = "destination",
            DestinationObjectKey = "b.txt",
            TotalBytes = 1
        };

        task.Validate();
    }

    [Fact]
    public void UploadTaskStillRequiresLocalPath()
    {
        var task = new TransferTaskRecord
        {
            ProfileId = Guid.NewGuid(),
            ProfileName = "profile",
            Direction = TransferDirection.Upload,
            Bucket = "bucket",
            ObjectKey = "a.txt"
        };

        Assert.Throws<ArgumentException>(() => task.Validate());
    }
}
