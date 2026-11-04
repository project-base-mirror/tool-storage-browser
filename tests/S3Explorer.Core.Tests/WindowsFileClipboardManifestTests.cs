using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class WindowsFileClipboardManifestTests
{
    [Fact]
    public void Uses_exact_keys_and_preserves_snapshot_metadata_and_empty_directories()
    {
        var date = DateTimeOffset.Parse("2026-11-04T10:00:00Z");
        var file = File("root/目录/actual.txt") with { Name = "wrong label", Size = 123, LastModified = date, ETag = "etag", VersionId = "v1" };
        var manifest = WindowsFileClipboardManifest.Build("root/", [file, Dir("root/empty/")]);
        Assert.Equal(["empty", "目录", "目录\\actual.txt"], manifest.Select(x => x.RelativePath));
        Assert.Equal(new WindowsFileClipboardEntry("目录\\actual.txt", file.Key, false, 123, date, "etag", "v1"), manifest[2]);
        Assert.True(manifest[0].IsDirectory);
        Assert.Equal("root/目录/", manifest[1].Key);
    }

    [Theory]
    [InlineData("../file")]
    [InlineData("./file")]
    [InlineData("/file")]
    [InlineData("a//file")]
    [InlineData("a\\file")]
    [InlineData("C:file")]
    [InlineData("x?")]
    [InlineData("x*")]
    [InlineData("x|")]
    [InlineData("x\"")]
    [InlineData("x<")]
    [InlineData("x>")]
    [InlineData("x\0")]
    [InlineData("x\n")]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("NUL.txt")]
    [InlineData("con")]
    [InlineData("PRN.any")]
    [InlineData("aux")]
    [InlineData("COM1.txt")]
    [InlineData("lpt9")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT²")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("NUL .txt")]
    public void Rejects_windows_unsafe_names_on_every_operating_system(string relative) =>
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("root/", [File("root/" + relative)]));

    [Fact]
    public void Does_not_normalize_prefix_or_percent_encoded_keys_and_returns_read_only_snapshot()
    {
        var inputs = new List<S3ObjectEntry> { File(" /prefix//%2e%2e.txt") };
        var manifest = WindowsFileClipboardManifest.Build(" /prefix//", inputs);
        inputs.Clear();
        Assert.Equal("%2e%2e.txt", Assert.Single(manifest).RelativePath);
        Assert.Throws<NotSupportedException>(() => ((IList<WindowsFileClipboardEntry>)manifest).Clear());
    }

    [Fact]
    public async Task Counts_discovered_folders_and_duplicate_markers_toward_limit()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsFileClipboardManifest.BuildAsync("", [Dir("a/")],
            (_, _, _) => Task.FromResult(Page([Dir("a/b/"), File("a/")])), TestContext.Current.CancellationToken, itemLimit: 2));
    }

    [Fact]
    public void Rejects_case_collisions_in_files_and_implicit_parent_directories()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a"), File("A")]));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a/one"), File("A/two")]));
    }

    [Fact]
    public void Rejects_file_directory_clashes_in_both_orders()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a"), File("a/b")]));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a/b"), File("a")]));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [Dir("a/"), File("a")]));
    }

    [Fact]
    public void Dedupe_exact_entries_but_reject_changed_file_snapshots()
    {
        Assert.Single(WindowsFileClipboardManifest.Build("", [File("a"), File("a")]));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a"), File("a") with { Size = 5 }]));
    }

    [Fact]
    public void Enforces_utf16_path_limit_and_component_limit()
    {
        var valid = "abc/" + new string('x', 255);
        Assert.Equal(259, WindowsFileClipboardManifest.Build("", [File(valid)]).Last().RelativePath.Length);
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("abcd/" + new string('x', 255))]));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File(new string('x', 256))]));
        var unicode = "abc/" + string.Concat(Enumerable.Repeat("😀", 127)) + "x";
        Assert.Equal(259, WindowsFileClipboardManifest.Build("", [File(unicode)]).Last().RelativePath.Length);
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("bad" + '\ud800')]));
    }

    [Fact]
    public void Rejects_outside_prefix_and_malformed_directory_and_nonempty_marker()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("root/", [File("root2/file")]));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [Dir("a")]));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a/") with { Size = 1 }]));
        Assert.Throws<ArgumentException>(() => WindowsFileClipboardManifest.Build("root", [File("root/file")]));
    }

    [Fact]
    public void Caps_inputs_and_generated_parent_descriptors()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a/b/c")], itemLimit: 2));
        Assert.Throws<InvalidOperationException>(() => WindowsFileClipboardManifest.Build("", [File("a"), File("a")], itemLimit: 1));
    }

    [Fact]
    public async Task Recurses_all_pages_and_keeps_empty_folders_and_zero_byte_files()
    {
        var calls = new List<(string, string?)>();
        var result = await WindowsFileClipboardManifest.BuildAsync("root/", [Dir("root/selected/")], (prefix, token, _) =>
        {
            calls.Add((prefix, token));
            return Task.FromResult((prefix, token) switch
            {
                ("root/selected/", null) => Page([File(prefix), Dir(prefix + "empty/")], "next", true),
                ("root/selected/", "next") => Page([File(prefix + "zero")]),
                ("root/selected/empty/", null) => Page([]),
                _ => throw new InvalidOperationException()
            });
        }, TestContext.Current.CancellationToken);
        Assert.Equal(["selected", "selected\\empty", "selected\\zero"], result.Select(x => x.RelativePath));
        Assert.Equal(3, calls.Count);
        Assert.False(result.Last().IsDirectory);
        Assert.Equal(0, result.Last().Size);
    }

    [Fact]
    public async Task Rejects_outside_selected_prefix_even_when_inside_current_prefix()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsFileClipboardManifest.BuildAsync("root/", [Dir("root/a/")],
            (_, _, _) => Task.FromResult(Page([File("root/b/secret")])), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_bad_selection_before_reading_and_cancellation_before_reading()
    {
        var calls = 0;
        Task<PagedObjectResult> Load(string prefix, string? token, CancellationToken ct) { calls++; return Task.FromResult(Page([])); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsFileClipboardManifest.BuildAsync("", [Dir("../")], Load, TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsFileClipboardManifest.BuildAsync("", [Dir("a/")], Load, cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Bounds_pages_even_when_service_returns_unlimited_empty_pages()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsFileClipboardManifest.BuildAsync("", [Dir("a/")],
            (_, _, _) => Task.FromResult(Page([], (++calls).ToString(), true)), TestContext.Current.CancellationToken, itemLimit: 3));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Rejects_repeated_or_missing_continuation_tokens()
    {
        foreach (var token in new string?[] { null, "", "same" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsFileClipboardManifest.BuildAsync("", [Dir("a/")],
                (_, _, _) => Task.FromResult(Page([], token, true)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Flat_listing_preserves_root_nested_markers_and_empty_folders_without_recursive_requests()
    {
        var calls = new List<(string Prefix, string? Token)>();
        var manifest = await WindowsFileClipboardManifest.BuildAsync("root/", [Dir("root/selected/")],
            (prefix, token, _) =>
            {
                calls.Add((prefix, token));
                return Task.FromResult(Page([
                    File("root/selected/"),
                    File("root/selected/empty/"),
                    File("root/selected/nested/deep-empty/"),
                    File("root/selected/nested/file.txt") with { Size = 7 }
                ]));
            }, TestContext.Current.CancellationToken, flatListing: true);

        Assert.Equal(("root/selected/", (string?)null), Assert.Single(calls));
        Assert.Equal([
            "selected", "selected\\empty", "selected\\nested",
            "selected\\nested\\deep-empty", "selected\\nested\\file.txt"
        ], manifest.Select(item => item.RelativePath));
        Assert.Equal([true, true, true, true, false], manifest.Select(item => item.IsDirectory));
        Assert.Equal(7, manifest.Last().Size);
        Assert.Equal("root/selected/nested/", manifest[2].Key);
    }

    [Theory]
    [InlineData("root/selected/")]
    [InlineData("root/selected/nested/")]
    public async Task Flat_listing_rejects_nonzero_root_or_nested_directory_marker(string markerKey)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsFileClipboardManifest.BuildAsync(
            "root/", [Dir("root/selected/")],
            (_, _, _) => Task.FromResult(Page([File(markerKey) with { Size = 1 }])),
            TestContext.Current.CancellationToken, flatListing: true));
    }

    [Fact]
    public async Task Flat_listing_paginates_only_selected_root_and_keeps_later_empty_directory()
    {
        var calls = new List<(string Prefix, string? Token)>();
        var manifest = await WindowsFileClipboardManifest.BuildAsync("root/", [Dir("root/selected/")],
            (prefix, token, _) =>
            {
                calls.Add((prefix, token));
                return Task.FromResult(token is null
                    ? Page([File("root/selected/nested/file.txt")], "next", true)
                    : Page([File("root/selected/empty/")]));
            }, TestContext.Current.CancellationToken, flatListing: true);

        Assert.Equal([("root/selected/", (string?)null), ("root/selected/", "next")], calls);
        Assert.Equal(["selected", "selected\\empty", "selected\\nested", "selected\\nested\\file.txt"],
            manifest.Select(item => item.RelativePath));
    }

    private static S3ObjectEntry File(string key) => new(key, "display label", 0, false, null, "STANDARD");
    private static S3ObjectEntry Dir(string key) => File(key) with { IsDirectory = true };
    private static PagedObjectResult Page(IReadOnlyList<S3ObjectEntry> entries, string? token = null, bool more = false) => new(entries, token, more);
}
