using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ParallelDownloadTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ParallelRangesProduceExactFileAndRespectSharedRequestBudget()
    {
        using var files = new TestFiles();
        var fake = CreateFake();
        var checkpoints = new ConcurrentBag<DownloadCheckpoint>();
        var context = Context(checkpoints: checkpoints) with { RequestBudget = new SharedTransferRequestBudget(2) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await S3StorageService.DownloadParallelAsync(fake.Client, "bucket", "key", files.Target,
            Identity(fake.Bytes) with
            {
                FullObjectSha256 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(fake.Bytes))
            }, 64, context, TestContext.Current.CancellationToken);
        output.WriteLine($"Deterministic range fixture: requests={fake.Requests.Count}; maxActive={fake.MaximumActive}; sharedBudget=2; elapsedMs={watch.ElapsedMilliseconds}. This is not a network throughput benchmark.");
        Assert.Equal(fake.Bytes, await File.ReadAllBytesAsync(files.Target, TestContext.Current.CancellationToken));
        Assert.InRange(fake.MaximumActive, 2, 2);
        Assert.All(fake.Requests, request =>
        {
            Assert.Equal("version", request.VersionId);
            Assert.Equal("etag", request.EtagToMatch);
        });
        Assert.Contains(checkpoints, checkpoint => checkpoint.CompletedBytes == fake.Bytes.Length && checkpoint.CompletedSegments.Count == 4);
    }

    [Fact]
    public async Task SparseFileResumesOnlyConfirmedSegments()
    {
        using var files = new TestFiles();
        var fake = CreateFake();
        var temporary = ResumableDownloadFile.TemporaryPath(files.Target);
        var sparse = new byte[fake.Bytes.Length];
        Array.Copy(fake.Bytes, 64, sparse, 64, 64);
        await File.WriteAllBytesAsync(temporary, sparse, TestContext.Current.CancellationToken);
        var checkpoint = new DownloadCheckpoint(temporary, 64, sparse.Length, "etag", "version")
        {
            SegmentSizeBytes = 64,
            CompletedSegments = [1]
        };
        await S3StorageService.DownloadParallelAsync(fake.Client, "bucket", "key", files.Target,
            Identity(fake.Bytes), 64, Context(checkpoint), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fake.Requests, request => request.ByteRange.Start == 64);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(fake.Bytes, await File.ReadAllBytesAsync(files.Target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationAfterSegmentCheckpointRestartsOnlyMissingSegments()
    {
        using var files = new TestFiles();
        var fake = CreateFake();
        using var cancellation = new CancellationTokenSource();
        DownloadCheckpoint? saved = null;
        var context = Context() with
        {
            Options = new TransferExecutionOptions { MultipartConcurrency = 1 },
            UpdateCheckpointAsync = (_, checkpoint, _, _) =>
            {
                saved = checkpoint;
                if (checkpoint!.CompletedBytes > 0) cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => S3StorageService.DownloadParallelAsync(
            fake.Client, "bucket", "key", files.Target, Identity(fake.Bytes), 64, context, cancellation.Token));
        Assert.NotNull(saved);
        Assert.Equal(64, saved.CompletedBytes);
        Assert.Equal(256, new FileInfo(ResumableDownloadFile.TemporaryPath(files.Target)).Length);
        var retry = CreateFake();
        await S3StorageService.DownloadParallelAsync(retry.Client, "bucket", "key", files.Target,
            Identity(retry.Bytes), 64, Context(saved), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(retry.Requests, request => request.ByteRange.Start == 0);
        Assert.Equal(fake.Bytes, await File.ReadAllBytesAsync(files.Target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedResetCheckpointDoesNotDestroyExistingTemporaryData()
    {
        using var files = new TestFiles();
        var fake = CreateFake();
        var temporary = ResumableDownloadFile.TemporaryPath(files.Target);
        var original = new byte[] { 4, 5, 6 };
        await File.WriteAllBytesAsync(temporary, original, TestContext.Current.CancellationToken);
        var oldCheckpoint = new DownloadCheckpoint(temporary, 64, fake.Bytes.Length, "etag", "version")
        {
            SegmentSizeBytes = 64,
            CompletedSegments = [0]
        };
        var context = Context(oldCheckpoint) with
        {
            UpdateCheckpointAsync = (_, _, _, _) => throw new IOException("checkpoint write failed")
        };
        await Assert.ThrowsAsync<IOException>(() => S3StorageService.DownloadParallelAsync(fake.Client,
            "bucket", "key", files.Target, Identity(fake.Bytes), 64, context, TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(temporary, TestContext.Current.CancellationToken));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task FullObjectHashMismatchCannotCommitRangeDownload()
    {
        using var files = new TestFiles();
        await File.WriteAllBytesAsync(files.Target, [7, 8, 9], TestContext.Current.CancellationToken);
        var fake = CreateFake();
        var remote = Identity(fake.Bytes) with { FullObjectSha256 = Convert.ToBase64String(new byte[32]) };
        var checkpoints = new ConcurrentBag<DownloadCheckpoint>();
        await Assert.ThrowsAsync<TransferExecutionException>(() => S3StorageService.DownloadParallelAsync(fake.Client,
            "bucket", "key", files.Target, remote, 64, Context(checkpoints: checkpoints), TestContext.Current.CancellationToken));
        var invalidated = Assert.Single(checkpoints, item => item.IntegritySummary?.Contains("校验失败", StringComparison.Ordinal) == true);
        Assert.Equal(0, invalidated.CompletedBytes);
        Assert.Empty(invalidated.CompletedSegments);
        Assert.Contains("校验失败", invalidated.IntegritySummary);
        Assert.False(File.Exists(invalidated.TemporaryPath));
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(files.Target, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("range")]
    [InlineData("short")]
    [InlineData("identity")]
    public async Task InvalidRangeCannotReplaceExistingDestination(string fault)
    {
        using var files = new TestFiles();
        await File.WriteAllBytesAsync(files.Target, [7, 8, 9], TestContext.Current.CancellationToken);
        var fake = CreateFake();
        fake.Fault = fault;
        await Assert.ThrowsAnyAsync<Exception>(() => S3StorageService.DownloadParallelAsync(
            fake.Client, "bucket", "key", files.Target, Identity(fake.Bytes), 64, Context(), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(files.Target, TestContext.Current.CancellationToken));
    }

    private static RemoteObjectIdentity Identity(byte[] bytes) => new(bytes.Length, "etag", "version");

    private static TransferOperationContext Context(DownloadCheckpoint? checkpoint = null, ConcurrentBag<DownloadCheckpoint>? checkpoints = null) =>
        new(new TransferExecutionOptions { MultipartConcurrency = 4 }, new SharedTransferBandwidthLimiter(), checkpoint, null,
            _ => { }, (_, value, _, _) =>
            {
                value!.Validate();
                checkpoints?.Add(value);
                return Task.CompletedTask;
            });

    private static FakeS3 CreateFake()
    {
        var client = DispatchProxy.Create<IAmazonS3, FakeS3>();
        var fake = (FakeS3)(object)client;
        fake.Client = client;
        return fake;
    }

    public class FakeS3 : DispatchProxy
    {
        public IAmazonS3 Client { get; set; } = null!;
        public byte[] Bytes { get; } = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        public ConcurrentBag<GetObjectRequest> Requests { get; } = [];
        public string? Fault { get; set; }
        public int MaximumActive;
        private int _active;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAmazonS3.GetObjectAsync))
                return GetAsync((GetObjectRequest)args![0]!, (CancellationToken)args[1]!);
            if (targetMethod.Name == nameof(IDisposable.Dispose)) return null;
            throw new NotSupportedException(targetMethod.Name);
        }

        private async Task<GetObjectResponse> GetAsync(GetObjectRequest request, CancellationToken token)
        {
            Requests.Add(request);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = MaximumActive; }
            while (active > observed && Interlocked.CompareExchange(ref MaximumActive, active, observed) != observed);
            try
            {
                await Task.Delay(20, token);
                var first = (int)request.ByteRange.Start;
                var last = (int)request.ByteRange.End;
                var count = last - first + 1;
                var bytes = Bytes.AsSpan(first, Fault == "short" ? count - 1 : count).ToArray();
                return new GetObjectResponse
                {
                    HttpStatusCode = HttpStatusCode.PartialContent,
                    ContentRange = Fault == "range" ? "bytes 0-0/256" : $"bytes {first}-{last}/{Bytes.Length}",
                    ContentLength = count,
                    ETag = Fault == "identity" ? "different" : "etag",
                    VersionId = "version",
                    ResponseStream = new TrackedStream(bytes, () => Interlocked.Decrement(ref _active))
                };
            }
            catch
            {
                Interlocked.Decrement(ref _active);
                throw;
            }
        }
    }

    private sealed class TrackedStream(byte[] bytes, Action disposed) : MemoryStream(bytes)
    {
        private int _disposed;
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) disposed();
            base.Dispose(disposing);
        }
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "s3-parallel-tests", Guid.NewGuid().ToString("N"));
        public TestFiles() => Directory.CreateDirectory(_root);
        public string Target => Path.Combine(_root, "result.bin");
        public void Dispose() => Directory.Delete(_root, true);
    }
}
