using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class PathIntegrityRegressionTests
{
    [Theory]
    [InlineData("report%20final.txt")]
    [InlineData("report final.txt")]
    [InlineData("literal%2520.txt")]
    [InlineData("folder//nested/中文 + #?.txt")]
    [InlineData("/leading/trailing/")]
    [InlineData("back\\slash.txt")]
    [InlineData(" ")]
    public void ObjectUrisRoundTripExactKeys(string key)
    {
        var location = new S3Location("Profile % +", "bucket", key);
        Assert.Equal(location, S3Location.Parse(location.ToString()));
    }

    [Fact]
    public void LiteralPercentEscapeAndSpaceAreDistinctUris()
    {
        var literal = new S3Location("profile", "bucket", "report%20final.txt");
        var space = new S3Location("profile", "bucket", "report final.txt");
        Assert.EndsWith("report%2520final.txt", literal.ToString());
        Assert.EndsWith("report%20final.txt", space.ToString());
        Assert.NotEqual(S3Location.Parse(literal.ToString()), S3Location.Parse(space.ToString()));
    }

    [Theory]
    [InlineData("foo_bar.txt")]
    [InlineData("foo_x005F_bar.txt")]
    public void SyncPathPreservesScannedNameInsteadOfReencoding(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-path-" + Guid.NewGuid().ToString("N"));
        var local = new FolderSyncFile(name, 10, DateTimeOffset.UtcNow);
        var item = new FolderSyncPlanItem(name, FolderSyncChange.Deleted, FolderSyncAction.DeleteLocal, local, null, "test");
        Assert.Equal(LocalObjectPath.ToExtendedLengthPath(Path.Combine(root, name)), LocalObjectPath.ResolveSyncPath(root, item));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("nested//file.txt")]
    public void RealLocalPathRejectsEscapeAndAmbiguousNames(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "path-test");
        Assert.Throws<InvalidOperationException>(() => LocalObjectPath.ResolveRelativePath(root, relative));
    }

    [Theory]
    [InlineData("file", "file.s3explorer.download")]
    [InlineData("folder", "folder/child")]
    [InlineData("case.txt", "CASE.txt")]
    public void SyncRejectsDownloadAndDeleteAliasesBeforeExecutingAnything(string download, string delete)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-alias-" + Guid.NewGuid().ToString("N"));
        var items = new[]
        {
            new FolderSyncPlanItem(download, FolderSyncChange.New, FolderSyncAction.Download, null,
                new FolderSyncFile(download, 1, DateTimeOffset.UtcNow), "test"),
            new FolderSyncPlanItem(delete, FolderSyncChange.Deleted, FolderSyncAction.DeleteLocal,
                new FolderSyncFile(delete, 1, DateTimeOffset.UtcNow), null, "test")
        };
        Assert.Throws<InvalidOperationException>(() => LocalObjectPath.ValidateSyncDestinations(root, items));
        Assert.Throws<InvalidOperationException>(() => LocalObjectPath.ValidateSyncDestinations(root, items.Reverse()));
    }

    [Fact]
    public void TraversalSkipsExternalLinksAndCyclesAndResolverRejectsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "safe-traversal-" + Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(root, "nested", "allowed.txt"), "allowed");
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "not selected");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "outside"), outside);
                Directory.CreateSymbolicLink(Path.Combine(root, "nested", "cycle"), root);
                File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), Path.Combine(outside, "secret.txt"));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Skip("Symbolic-link creation is unavailable: " + exception.Message);
            }
            var skipped = new List<string>();
            var files = SafeLocalFileTraversal.EnumerateFiles(root, (path, _) => skipped.Add(path)).ToArray();
            Assert.Equal(Path.Combine(root, "nested", "allowed.txt"), Assert.Single(files));
            Assert.Equal(3, skipped.Count);
            Assert.Throws<IOException>(() => LocalObjectPath.ResolveRelativePath(root, "outside/secret.txt"));
            Assert.Throws<IOException>(() => LocalObjectPath.ResolveRelativePath(root, "linked.txt"));
            Assert.Throws<IOException>(() => LocalObjectPath.MapRelativeKey(root, "outside/new.txt"));
            Assert.Empty(SafeLocalFileTraversal.EnumerateFiles(Path.Combine(root, "outside"), (_, _) => { }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
