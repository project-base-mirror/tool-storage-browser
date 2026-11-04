using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class ObjectDeletionListingTests
{
    [Fact]
    public async Task RawListingKeepsRootAndNestedEmptyFolderMarkersAcrossPages()
    {
        var pages = new[]
        {
            new PagedObjectResult([Entry("root/"), Entry("root/empty/")], "next", true),
            new PagedObjectResult([Entry("root/nested/empty/"), Entry("root/file.txt"), Entry("root/")], null, false)
        };
        var calls = 0;
        var keys = await ObjectDeletionListing.ListKeysAsync("root/", 100,
            (token, _) =>
            {
                Assert.Equal(calls == 0 ? null : "next", token);
                return Task.FromResult(pages[calls++]);
            }, TestContext.Current.CancellationToken);
        Assert.Equal(["root/", "root/empty/", "root/file.txt", "root/nested/empty/"], keys);
    }

    [Fact]
    public async Task InvalidPagesCannotProducePartialDeleteList()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObjectDeletionListing.ListKeysAsync("root/", 1,
            (_, _) => Task.FromResult(new PagedObjectResult([Entry("outside/file")], null, false)), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObjectDeletionListing.ListKeysAsync("root/", 1,
            (_, _) => Task.FromResult(new PagedObjectResult([Entry("root/"), Entry("root/empty/")], null, false)), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ObjectDeletionListing.ListKeysAsync("root/", 10,
            (_, _) => Task.FromResult(new PagedObjectResult([], "same", true)), TestContext.Current.CancellationToken));
    }

    private static S3ObjectEntry Entry(string key) => new(key, key, 0, key.EndsWith('/'), null, "STANDARD");
}
