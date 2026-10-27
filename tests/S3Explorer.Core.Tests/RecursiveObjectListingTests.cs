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
            }, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["root/a.txt", "root/first/b.txt", "root/first/deep/c.txt", "root/second/d.txt"],
            files.Select(item => item.Key).Order(StringComparer.Ordinal));
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
                (_, _, _) => Task.FromResult(Page([], "same", true)), TestContext.Current.CancellationToken);
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
                (_, _, _) => Task.FromResult(Page([Directory("a/"), Directory("b/")])), TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task IndependentPrefixesRunConcurrentlyWithinBoundAndPruneBeforeListing()
    {
        var entered = 0;
        var active = 0;
        var maximum = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var files = await RecursiveObjectListing.ListFilesAsync("root/", 100, 100,
            async (prefix, _, token) =>
            {
                calls.Add(prefix);
                if (prefix == "root/") return Page([Directory("root/a/"), Directory("root/b/"), Directory("root/ignored/")]);
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                if (Interlocked.Increment(ref entered) == 2) barrier.SetResult();
                await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Interlocked.Decrement(ref active);
                return Page([File(prefix + "file"), File(prefix + "skip.tmp")]);
            }, TestContext.Current.CancellationToken, maxConcurrency: 2,
            excludePrefix: prefix => prefix == "root/ignored/",
            excludeKey: key => key.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal(2, maximum);
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain("root/ignored/", calls);
    }

    [Fact]
    public async Task BreakingEnumerationCancelsAndObservesSiblingRequests()
    {
        var siblingStopped = false;
        await foreach (var item in RecursiveObjectListing.EnumerateFilesAsync("", 100, 100,
            async (prefix, _, token) =>
            {
                if (prefix == "") return Page([Directory("a/"), Directory("b/")]);
                if (prefix == "a/") return Page([File("a/file")]);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { siblingStopped = true; }
                return Page([]);
            }, TestContext.Current.CancellationToken, maxConcurrency: 2))
        {
            Assert.Equal("a/file", item.Key);
            break;
        }
        Assert.True(siblingStopped);
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
