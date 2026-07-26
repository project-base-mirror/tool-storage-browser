using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class RecursiveObjectListingTests
{
    [Fact]
    public async Task Traverses_nested_common_prefixes_and_pages()
    {
        var calls = new List<(string Prefix, string? Token)>();
        var pages = new Dictionary<(string, string?), PagedObjectResult>
        {
            [("root/", null)] = Page([
                Directory("root/first/"),
                File("root/a.txt")
            ], "next", true),
            [("root/", "next")] = Page([Directory("root/second/")]),
            [("root/first/", null)] = Page([
                Directory("root/first/deep/"),
                File("root/first/b.txt")
            ]),
            [("root/first/deep/", null)] = Page([File("root/first/deep/c.txt")]),
            [("root/second/", null)] = Page([File("root/second/d.txt")])
        };

        var files = await RecursiveObjectListing.ListFilesAsync(
            "root/", 1000, 100,
            (prefix, token, _) =>
            {
                calls.Add((prefix, token));
                return Task.FromResult(pages[(prefix, token)]);
            });

        Assert.Equal(
            ["root/a.txt", "root/second/d.txt", "root/first/b.txt", "root/first/deep/c.txt"],
            files.Select(item => item.Key));
        Assert.Contains(("root/", "next"), calls);
        Assert.Contains(calls, call => call.Prefix == "root/first/deep/");
    }

    [Fact]
    public async Task Rejects_repeated_pagination_token()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await RecursiveObjectListing.ListFilesAsync(
                string.Empty, 10, 100,
                (_, _, _) => Task.FromResult(Page([], "same", true)));
        });

        Assert.Contains("分页令牌", exception.Message);
    }

    [Fact]
    public async Task Enforces_global_item_limit_including_directories()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await RecursiveObjectListing.ListFilesAsync(
                string.Empty, 10, 1,
                (_, _, _) => Task.FromResult(Page([Directory("a/"), Directory("b/")])));
        });
    }

    private static PagedObjectResult Page(
        IReadOnlyList<S3ObjectEntry> items,
        string? token = null,
        bool hasMore = false) => new(items, token, hasMore);

    private static S3ObjectEntry Directory(string key) =>
        new(key, S3Path.DisplayName(key, true), 0, true, null, string.Empty);

    private static S3ObjectEntry File(string key) =>
        new(key, S3Path.DisplayName(key, false), 1, false, DateTimeOffset.UtcNow, "STANDARD");
}
