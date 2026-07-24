using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class MultipartOperationsTests
{
    [Fact]
    public void BuildPartsUsesExactFinalPartAndEnforcesS3PartLimit()
    {
        var partSize = 5L * 1024 * 1024;
        var parts = MultipartUploadPlanner.BuildParts(partSize * 2 + 7, partSize);

        Assert.Equal(3, parts.Count);
        Assert.Equal(partSize, parts[0].Size);
        Assert.Equal(partSize, parts[1].Size);
        Assert.Equal(7, parts[2].Size);
        Assert.Throws<InvalidOperationException>(() =>
            MultipartUploadPlanner.BuildParts(partSize * 10_000 + 1, partSize));
    }

    [Fact]
    public void ReconcileOnlySkipsRemoteConfirmedPartsWithExpectedSize()
    {
        var partSize = 5L * 1024 * 1024;
        var result = MultipartUploadPlanner.Reconcile(partSize * 3, partSize,
        [
            new MultipartPartCheckpoint(1, "etag-1", partSize),
            new MultipartPartCheckpoint(2, "wrong-size", partSize - 1),
            new MultipartPartCheckpoint(3, "etag-3", partSize)
        ]);

        Assert.Equal([1, 3], result.ConfirmedParts.Select(part => part.PartNumber));
        Assert.Single(result.MissingParts);
        Assert.Equal(2, result.MissingParts[0].PartNumber);
        Assert.Equal(partSize * 2, result.ConfirmedBytes);
    }

    [Fact]
    public void CheckpointSourceIdentityPreventsUnsafeResume()
    {
        var modified = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var checkpoint = new MultipartUploadCheckpoint(
            "upload", 5L * 1024 * 1024, [], false, "bucket", "key", 42, modified, modified);

        Assert.True(checkpoint.Matches("bucket", "key", 42, modified, checkpoint.PartSize));
        Assert.False(checkpoint.Matches("bucket", "key", 43, modified, checkpoint.PartSize));
        Assert.False(checkpoint.Matches("bucket", "other", 42, modified, checkpoint.PartSize));
        Assert.False((checkpoint with { CleanupPending = true }).Matches("bucket", "key", 42, modified, checkpoint.PartSize));
    }

    [Fact]
    public void FilterUsesKeyAndCreationTimeWithoutChangingUploadIdentity()
    {
        var cutoff = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var uploads = new[]
        {
            new IncompleteMultipartUpload("b", "logs/old.bin", "u1", cutoff.AddHours(-1), 10, 1),
            new IncompleteMultipartUpload("b", "logs/new.bin", "u2", cutoff.AddHours(1), 20, 2),
            new IncompleteMultipartUpload("b", "images/old.bin", "u3", cutoff.AddHours(-2), 30, 3)
        };

        var result = MultipartUploadPlanner.Filter(uploads, "logs", cutoff);

        var upload = Assert.Single(result);
        Assert.Equal("u1", upload.UploadId);
    }
}
