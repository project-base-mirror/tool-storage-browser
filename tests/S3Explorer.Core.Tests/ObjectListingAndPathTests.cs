using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ObjectListingAndPathTests
{
    [Fact]
    public void ObjectCacheStopsAtConfiguredLimitWithoutEnumeratingMillionItems()
    {
        var cache = new BoundedObjectCache(1000);
        var result = cache.AddRange(Enumerable.Range(0, 1_000_000).Select(index =>
            new S3ObjectEntry($"objects/{index}", index.ToString(), index, false, null, "STANDARD")));

        Assert.Equal(1000, cache.Count);
        Assert.Equal(1000, result.AddedCount);
        Assert.True(result.Truncated);
        Assert.True(cache.LimitReached);
    }

    [Fact]
    public void ObjectCacheDeduplicatesByKeyAndDirectoryFlag()
    {
        var cache = new BoundedObjectCache(10);
        var file = new S3ObjectEntry("same", "same", 1, false, null, "STANDARD");
        var directory = file with { IsDirectory = true };

        var result = cache.AddRange([file, file, directory]);

        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, cache.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void LocalPathPreservesUnicodeSpacesPlusAndPercent()
    {
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-path-tests", Guid.NewGuid().ToString("N"));
        var mapped = LocalObjectPath.MapRelativeKey(root, "中文/space + %.txt");

        Assert.EndsWith(Path.Combine("中文", "space + %.txt"), mapped);
    }

    [Fact]
    public void LocalPathSanitizesIllegalReservedAndTrailingCharacters()
    {
        Assert.Equal("_CON", LocalObjectPath.SanitizeSegment("CON"));
        Assert.Equal("bad_x003A_name_x003F_.txt", LocalObjectPath.SanitizeSegment("bad:name?.txt"));
        Assert.Equal("trailing_x002E_", LocalObjectPath.SanitizeSegment("trailing."));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    public void LocalPathRejectsTraversal(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "s3explorer-path-tests", Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidOperationException>(() => LocalObjectPath.MapRelativeKey(root, key));
    }

    [Fact]
    public void ExtendedLengthPathIsUsedOnWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), new string('a', 120), new string('b', 120));
        var mapped = LocalObjectPath.MapRelativeKey(root, $"{new string('c', 120)}/file.txt");

        if (OperatingSystem.IsWindows())
            Assert.StartsWith(@"\\?\", mapped);
        else
            Assert.True(Path.IsPathFullyQualified(mapped));
    }
}
