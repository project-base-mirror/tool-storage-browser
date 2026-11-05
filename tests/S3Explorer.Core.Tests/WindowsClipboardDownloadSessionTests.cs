using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class WindowsClipboardDownloadSessionTests
{
    [Fact]
    public async Task Construction_and_invalid_indexes_do_not_create_cache_or_download()
    {
        using var scratch = new Scratch();
        var calls = 0;
        await using var session = new WindowsClipboardDownloadSession([Entry(), Entry() with { IsDirectory = true }],
            (_, _, _, _) => { calls++; return Task.CompletedTask; }, Template(), cacheParent: scratch.Cache);
        Assert.False(Directory.Exists(scratch.Cache));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.OpenRead(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.OpenRead(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.OpenRead(1));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(scratch.Cache));
    }

    [Fact]
    public async Task Concurrent_and_repeated_reads_share_download_but_have_independent_positions()
    {
        using var scratch = new Scratch();
        var started = Signal();
        var release = Signal();
        var calls = 0;
        await using var session = new WindowsClipboardDownloadSession([Entry()], async (entry, path, context, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            await Complete(entry, path, context, token);
        }, Template(), cacheParent: scratch.Cache);
        var firstOpen = Task.Run(() => session.OpenRead(0), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var secondOpen = Task.Run(() => session.OpenRead(0), TestContext.Current.CancellationToken);
        Assert.False(firstOpen.IsCompleted);
        release.TrySetResult();
        using var first = await firstOpen.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var second = await secondOpen.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(1, first.ReadByte());
        Assert.Equal(2, first.ReadByte());
        Assert.Equal(0, second.Position);
        Assert.Equal(1, second.ReadByte());
        using var third = session.OpenRead(0);
        Assert.Equal(0, third.Position);
        Assert.Equal(1, third.ReadByte());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("etag")]
    [InlineData("version")]
    [InlineData("missing")]
    public async Task Changed_or_missing_remote_identity_is_rejected_before_returning_a_stream(string mismatch)
    {
        using var scratch = new Scratch();
        string? target = null;
        await using var session = new WindowsClipboardDownloadSession([Entry()], async (entry, path, context, token) =>
        {
            target = path;
            await File.WriteAllBytesAsync(path, [1, 2, 3], token);
            var snapshot = Checkpoint(entry, path);
            DownloadCheckpoint? checkpoint = mismatch switch
            {
                "length" => snapshot with { RemoteLength = entry.Size + 1 },
                "etag" => snapshot with { ETag = "different-etag" },
                "version" => snapshot with { VersionId = "different-version" },
                _ => null
            };
            await context.UpdateCheckpointAsync(0, checkpoint, null, token);
        }, Template(), cacheParent: scratch.Cache);
        Assert.Throws<IOException>(() => session.OpenRead(0));
        Assert.NotNull(target);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Committed_file_length_must_match_snapshot(int actualLength)
    {
        using var scratch = new Scratch();
        await using var session = new WindowsClipboardDownloadSession([Entry()], async (entry, path, context, token) =>
        {
            await context.UpdateCheckpointAsync(0, Checkpoint(entry, path), null, token);
            await File.WriteAllBytesAsync(path, new byte[actualLength], token);
        }, Template(), cacheParent: scratch.Cache);
        Assert.Throws<IOException>(() => session.OpenRead(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_byte_file_still_requires_remote_identity_verification(bool verified)
    {
        using var scratch = new Scratch();
        await using var session = new WindowsClipboardDownloadSession([Entry() with { Size = 0 }], async (entry, path, context, token) =>
        {
            Assert.Equal(1, context.Options.MaximumDownloadBytes);
            if (verified) await context.UpdateCheckpointAsync(0, Checkpoint(entry, path), null, token);
            await File.WriteAllBytesAsync(path, [], token);
        }, Template(), cacheParent: scratch.Cache);
        if (!verified) Assert.Throws<IOException>(() => session.OpenRead(0));
        else
        {
            using var stream = session.OpenRead(0);
            Assert.Equal(0, stream.Length);
            Assert.Equal(-1, stream.ReadByte());
        }
    }

    [Fact]
    public async Task Sizes_above_four_gib_are_not_truncated_and_short_files_are_rejected_without_large_allocations()
    {
        using var scratch = new Scratch();
        var size = (1L << 32) + 123;
        await using var session = new WindowsClipboardDownloadSession([Entry() with { Size = size }], async (entry, path, context, token) =>
        {
            Assert.Equal(size, entry.Size);
            Assert.Equal(size, context.Options.MaximumDownloadBytes);
            await context.UpdateCheckpointAsync(0, Checkpoint(entry, path), null, token);
            await File.WriteAllBytesAsync(path, [1, 2, 3], token);
        }, Template(), cacheParent: scratch.Cache);
        Assert.Throws<IOException>(() => session.OpenRead(0));
    }

    [Fact]
    public async Task Download_failure_is_preserved_and_not_presented_as_eof_or_retried()
    {
        using var scratch = new Scratch();
        var failure = new IOException("simulated interrupted transfer");
        var calls = 0;
        await using var session = new WindowsClipboardDownloadSession([Entry()], (_, _, _, _) =>
        {
            calls++;
            throw failure;
        }, Template(), cacheParent: scratch.Cache);
        Assert.Same(failure, Assert.Throws<IOException>(() => session.OpenRead(0)));
        Assert.Same(failure, Assert.Throws<IOException>(() => session.OpenRead(0)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisposeAsync_waits_for_cancelled_download_then_removes_owned_cache_and_preserves_neighbors()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.Cache);
        var neighbor = Path.Combine(scratch.Cache, "unrelated.txt");
        await File.WriteAllTextAsync(neighbor, "keep", TestContext.Current.CancellationToken);
        var started = Signal();
        var cancellationObserved = Signal();
        var finishDownload = Signal();
        string? target = null;
        await using var session = new WindowsClipboardDownloadSession([Entry()], async (_, path, _, token) =>
        {
            target = path;
            await File.WriteAllBytesAsync(path, [1], token);
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                // Keep the provider unwinding after cancellation to verify disposal waits for it.
                await finishDownload.Task.WaitAsync(TestContext.Current.CancellationToken);
                throw;
            }
        }, Template(), cacheParent: scratch.Cache);
        var opening = Task.Run(() => session.OpenRead(0), TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var cleanup = session.DisposeAsync().AsTask();
            session.Dispose(); // Repeated synchronous disposal must share the same cleanup.
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(cleanup.IsCompleted);
            Assert.True(File.Exists(target));
            finishDownload.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(Path.GetDirectoryName(target!)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var unexpected = await opening.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            });
            Assert.Equal("keep", await File.ReadAllTextAsync(neighbor, TestContext.Current.CancellationToken));
            Assert.Throws<ObjectDisposedException>(() => session.OpenRead(0));
            await session.DisposeAsync(); // Async disposal is also idempotent after completion.
        }
        finally
        {
            finishDownload.TrySetResult();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Throwing_cancellation_callback_does_not_prevent_owned_cache_cleanup()
    {
        using var scratch = new Scratch();
        var started = Signal();
        string? target = null;
        await using var session = new WindowsClipboardDownloadSession([Entry()], async (_, path, _, token) =>
        {
            target = path;
            await File.WriteAllBytesAsync(path, [1], token);
            using var registration = token.Register(() => throw new IOException("faulty provider cancellation callback"));
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, Template(), cacheParent: scratch.Cache);
        var opening = Task.Run(() => session.OpenRead(0), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var unexpected = await opening.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        });
        await session.DisposeAsync();
        Assert.False(Directory.Exists(Path.GetDirectoryName(target!)));
    }

    [Fact]
    public async Task Each_session_uses_private_numeric_cache_names_and_clears_template_checkpoints()
    {
        using var scratch = new Scratch();
        var paths = new List<string>();
        var entries = new List<WindowsFileClipboardEntry> { Entry() };
        var template = Template() with { DownloadCheckpoint = new DownloadCheckpoint("must-not-reuse", 0, 3) };
        async Task Download(WindowsFileClipboardEntry entry, string path, TransferOperationContext context, CancellationToken token)
        {
            paths.Add(path);
            Assert.Equal("secret/remote-key.txt", entry.Key);
            Assert.Equal("00000000.bin", Path.GetFileName(path));
            Assert.Null(context.DownloadCheckpoint);
            Assert.Null(context.MultipartCheckpoint);
            Assert.False(context.Options.AllowDestinationOverwrite);
            Assert.False(context.Options.RetainFailedTransferState);
            Assert.Same(template.BandwidthLimiter, context.BandwidthLimiter);
            await Complete(entry, path, context, token);
        }
        await using var first = new WindowsClipboardDownloadSession(entries, Download, template, cacheParent: scratch.Cache);
        await using var second = new WindowsClipboardDownloadSession(entries, Download, template, cacheParent: scratch.Cache);
        entries.Clear(); // Construction must take its own immutable entry-list snapshot.
        using (first.OpenRead(0)) { }
        using (second.OpenRead(0)) { }
        Assert.NotEqual(Path.GetDirectoryName(paths[0]), Path.GetDirectoryName(paths[1]));
        await first.DisposeAsync();
        Assert.False(Directory.Exists(Path.GetDirectoryName(paths[0])));
        Assert.True(File.Exists(paths[1]));
        await second.DisposeAsync();
        Assert.False(Directory.Exists(Path.GetDirectoryName(paths[1])));
    }

    [Fact]
    public async Task Reparse_cache_parent_is_refused_before_download()
    {
        using var scratch = new Scratch();
        var outside = Path.Combine(scratch.Root, "outside");
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(scratch.Cache, outside); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating symbolic links requires Windows Developer Mode or privilege.");
        }
        var calls = 0;
        await using var session = new WindowsClipboardDownloadSession([Entry()], (_, _, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, Template(), cacheParent: scratch.Cache);
        Assert.Throws<IOException>(() => session.OpenRead(0));
        Assert.Equal(0, calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Reparse_download_target_is_refused_before_exposure()
    {
        using var scratch = new Scratch();
        var outside = Path.Combine(scratch.Root, "outside.txt");
        File.WriteAllBytes(outside, [1, 2, 3]);
        // Probe capability before entering a worker, so a missing Windows privilege skips cleanly.
        var probe = Path.Combine(scratch.Root, "probe");
        try { File.CreateSymbolicLink(probe, outside); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating symbolic links requires Windows Developer Mode or privilege.");
        }
        File.Delete(probe);
        await using var session = new WindowsClipboardDownloadSession([Entry()], async (entry, path, context, token) =>
        {
            await context.UpdateCheckpointAsync(0, Checkpoint(entry, path), null, token);
            File.CreateSymbolicLink(path, outside);
        }, Template(), cacheParent: scratch.Cache);
        Assert.Throws<IOException>(() => session.OpenRead(0));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(outside));
    }

    private static WindowsFileClipboardEntry Entry() => new("user-visible.txt", "secret/remote-key.txt", false, 3, null, "etag", "v1");
    private static DownloadCheckpoint Checkpoint(WindowsFileClipboardEntry entry, string path) =>
        new(path + ".part", entry.Size, entry.Size, entry.ETag, entry.VersionId);
    private static TransferOperationContext Template() => new(new TransferExecutionOptions(), new SharedTransferBandwidthLimiter(),
        null, null, _ => { }, (_, _, _, _) => throw new InvalidOperationException("Template callback must not be used."));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Complete(WindowsFileClipboardEntry entry, string path, TransferOperationContext context, CancellationToken token)
    {
        await context.UpdateCheckpointAsync(entry.Size, Checkpoint(entry, path), null, token);
        await File.WriteAllBytesAsync(path, [1, 2, 3], token);
    }
    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "clipboard-tests-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "cache");
        public Scratch() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
