using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class FolderSyncScanTests
{
    [Fact]
    public void ExcludingALinkItselfStillProtectsUnobservedDescendants()
    {
        var plan = FolderSyncPlanner.Analyze(
            Job(Path.GetTempPath()) with { PropagateDeletions = true, ExclusionPatterns = ["linked.txt"] },
            [new("linked.txt", 0, DateTimeOffset.MinValue) { IsReparsePoint = true }],
            [new("linked.txt/child.txt", 4, DateTimeOffset.UtcNow), new("other.txt", 4, DateTimeOffset.UtcNow)]);
        Assert.Equal(FolderSyncAction.None, Assert.Single(plan.Items, item => item.RelativePath == "linked.txt/child.txt").Action);
        Assert.Equal(FolderSyncAction.DeleteRemote, Assert.Single(plan.Items, item => item.RelativePath == "other.txt").Action);
    }

    [Theory]
    [InlineData(FolderSyncDirection.Upload, false)]
    [InlineData(FolderSyncDirection.Upload, true)]
    [InlineData(FolderSyncDirection.Download, false)]
    [InlineData(FolderSyncDirection.Download, true)]
    public async Task SkippedLinksProtectOnlyTheirOwnPathsAndSubtrees(FolderSyncDirection direction, bool calculateHashes)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-links-" + Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var token = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(root, "local-only.txt"), "local", token);
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "must not be read", token);
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
                Directory.CreateSymbolicLink(Path.Combine(root, "cycle"), root);
                File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), Path.Combine(outside, "secret.txt"));
                File.CreateSymbolicLink(Path.Combine(root, "broken.txt"), Path.Combine(outside, "missing.txt"));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Skip("Symbolic-link creation is unavailable: " + exception.Message);
            }

            var snapshot = await FolderSyncSnapshot.ReadLocalAsync(root, calculateHashes, 20, token);
            var plan = FolderSyncPlanner.Analyze(Job(root) with { Direction = direction, PropagateDeletions = true },
                snapshot.ToArray(),
                [
                    new("linked/secret.txt", 16, DateTimeOffset.UtcNow),
                    new("linked/deep/remote-only.txt", 4, DateTimeOffset.UtcNow),
                    new("cycle/local-only.txt", 5, DateTimeOffset.UtcNow),
                    new("linked.txt", 16, DateTimeOffset.UtcNow),
                    new("broken.txt", 4, DateTimeOffset.UtcNow),
                    new("linked-neighbor.txt", 4, DateTimeOffset.UtcNow)
                ]);

            Assert.Equal(4, snapshot.Count(file => file.IsReparsePoint));
            Assert.Equal(4, plan.SkippedLocalLinkCount);
            Assert.All(snapshot.Where(file => file.IsReparsePoint), file => Assert.Null(file.Checksum));
            Assert.DoesNotContain(snapshot, file => file.RelativePath == "linked/secret.txt");
            foreach (var path in new[] { "linked", "linked/secret.txt", "linked/deep/remote-only.txt", "cycle", "cycle/local-only.txt", "linked.txt", "broken.txt" })
            {
                var item = Assert.Single(plan.Items, item => item.RelativePath == path);
                Assert.Equal(FolderSyncChange.Excluded, item.Change);
                Assert.Equal(FolderSyncAction.None, item.Action);
                Assert.Contains("符号链接", item.Reason, StringComparison.Ordinal);
            }
            Assert.Equal(direction == FolderSyncDirection.Upload ? FolderSyncAction.Upload : FolderSyncAction.DeleteLocal,
                Assert.Single(plan.Items, item => item.RelativePath == "local-only.txt").Action);
            Assert.Equal(direction == FolderSyncDirection.Upload ? FolderSyncAction.DeleteRemote : FolderSyncAction.Download,
                Assert.Single(plan.Items, item => item.RelativePath == "linked-neighbor.txt").Action);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

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
