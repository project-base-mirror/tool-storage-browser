using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class EmptyDirectoryUploadDiscoveryTests
{
    [Fact]
    public void ReportsEmptyRootWithoutInventingFiles()
    {
        var root = CreateRoot();
        try
        {
            var empty = new List<string>();
            var errors = new List<Exception>();
            Assert.Empty(SafeLocalFileTraversal.EnumerateFiles(root, (_, exception) => errors.Add(exception), empty.Add));
            Assert.Equal(root, Assert.Single(empty));
            Assert.Empty(errors);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReportsNestedEmptyLeavesAlongsideNormalFiles()
    {
        var root = CreateRoot();
        try
        {
            var leaf = Directory.CreateDirectory(Path.Combine(root, "parent", "empty")).FullName;
            var sibling = Directory.CreateDirectory(Path.Combine(root, "empty-sibling")).FullName;
            var file = Path.Combine(root, "parent", "file.txt");
            File.WriteAllText(file, "test");
            var empty = new List<string>();
            var errors = new List<Exception>();
            Assert.Equal(file, Assert.Single(SafeLocalFileTraversal.EnumerateFiles(root, (_, exception) => errors.Add(exception), empty.Add)));
            Assert.Equal(new[] { sibling, leaf }.OrderBy(path => path), empty.OrderBy(path => path));
            Assert.DoesNotContain(root, empty);
            Assert.DoesNotContain(Path.GetDirectoryName(file)!, empty);
            Assert.Empty(errors);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DirectorySymlinkIsReportedAndNotTraversedOrCalledEmpty()
    {
        // Unix permits unprivileged symlinks. Windows symlink privileges are runner-dependent.
        if (OperatingSystem.IsWindows()) Assert.Skip("Windows symlink creation requires runner-specific privileges; exercised on Unix runners.");
        var root = CreateRoot();
        var outside = CreateRoot();
        try
        {
            var link = Path.Combine(root, "linked-empty-directory");
            Directory.CreateSymbolicLink(link, outside);
            var empty = new List<string>();
            var errors = new List<(string Path, Exception Error)>();
            Assert.Empty(SafeLocalFileTraversal.EnumerateFiles(root, (path, error) => errors.Add((path, error)), empty.Add));
            Assert.Empty(empty);
            Assert.Equal(link, Assert.Single(errors).Path);
            Assert.IsType<IOException>(errors[0].Error);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    private static string CreateRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "s3-empty-discovery-" + Guid.NewGuid().ToString("N"))).FullName;
}
