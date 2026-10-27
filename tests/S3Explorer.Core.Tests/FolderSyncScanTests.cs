using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class FolderSyncScanTests
{
    [Fact]
    public async Task ExcludedFilesAreNeverOpenedAndPrunedTreeDoesNotConsumeItemBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bin", "nested"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "locked.tmp"), "excluded", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "bin", "nested", "hidden"), "hidden", TestContext.Current.CancellationToken);
            using var locked = new FileStream(Path.Combine(root, "locked.tmp"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var snapshot = await FolderSyncSnapshot.ReadLocalAsync(root, true, 1,
                TestContext.Current.CancellationToken, ["*.tmp", "bin/**"]);
            var file = Assert.Single(snapshot);
            Assert.Equal("keep.txt", file.RelativePath);
            Assert.NotNull(file.Checksum);
            var job = Job(root) with { ExclusionPatterns = ["*.tmp", "bin/**"], PropagateDeletions = true };
            var plan = FolderSyncPlanner.Analyze(job, snapshot.ToArray(),
                [new FolderSyncFile("bin/nested/hidden", 6, DateTimeOffset.UtcNow), new FolderSyncFile("locked.tmp", 8, DateTimeOffset.UtcNow)]);
            Assert.DoesNotContain(plan.Items, item => item.Action == FolderSyncAction.DeleteRemote);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SingleLevelExclusionMustNotPruneIncludedGrandchildren()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bin", "nested"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "bin", "direct"), "exclude", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "bin", "nested", "keep"), "keep", TestContext.Current.CancellationToken);
            var snapshot = await FolderSyncSnapshot.ReadLocalAsync(root, false, 10,
                TestContext.Current.CancellationToken, ["bin/*"]);
            Assert.Equal("bin/nested/keep", Assert.Single(snapshot).RelativePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RepeatedHashScanDoesNotTrustUnchangedMetadataForChangedContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "file");
            await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
            var before = Assert.Single(await FolderSyncSnapshot.ReadLocalAsync(root, true, 10, TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(path, "new", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, before.LastModifiedUtc.UtcDateTime);
            var after = Assert.Single(await FolderSyncSnapshot.ReadLocalAsync(root, true, 10, TestContext.Current.CancellationToken));
            Assert.Equal(before.Size, after.Size);
            Assert.Equal(before.LastModifiedUtc, after.LastModifiedUtc);
            Assert.NotEqual(before.Checksum, after.Checksum);
        }
        finally { Directory.Delete(root, true); }
    }

    private static FolderSyncJob Job(string root) => new()
    {
        Name = "scan", ProfileId = Guid.NewGuid(), ProfileName = "test", Bucket = "bucket", LocalDirectory = root
    };
}
