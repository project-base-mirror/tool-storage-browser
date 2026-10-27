using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ResumableCopyTests
{
    private const long PartSize = 5L * 1024 * 1024;
    private static readonly ConnectionProfile Profile = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);

    private static TransferOperationContext Context(Action<MultipartUploadCheckpoint> save) => new(
        new TransferExecutionOptions { MultipartThresholdBytes = PartSize, PartSizeBytes = PartSize, MultipartConcurrency = 2 },
        new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, checkpoint, _) =>
        {
            save(checkpoint!);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task ParallelPartsRespectSharedBudgetAndPersistCompletion()
    {
        using var client = new Client();
        MultipartUploadCheckpoint? saved = null;
        var progress = new List<long>();
        var context = Context(value => saved = value) with
        {
            RequestBudget = new SharedTransferRequestBudget(2),
            ReportProgress = value => progress.Add(value.TransferredBytes)
        };
        await Run(client, context);
        Assert.Equal(2, client.MaximumActive);
        Assert.Equal(4, client.Requested.Count);
        Assert.True(saved!.CopyCompleted);
        Assert.Equal("completed", saved.CopyDestinationETag);
        Assert.Equal("destination-version", saved.CopyDestinationVersionId);
        Assert.Equal("version1", saved.CopySourceVersionId);
        Assert.Equal(4, saved.CompletedParts.Count);
        Assert.Equal(PartSize * 4, progress.Last());
        Assert.Equal([1, 2, 3, 4], client.CompletedParts);
    }

    [Fact]
    public async Task PausedCopyReconcilesRemotePartsAndOnlyCopiesMissingParts()
    {
        using var client = new Client();
        MultipartUploadCheckpoint? saved = null;
        using var pause = new CancellationTokenSource();
        var context = Context(value =>
        {
            saved = value;
            if (value.CompletedParts.Count > 0) pause.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(client, context, pause.Token));
        Assert.NotNull(saved);
        Assert.False(client.Aborted);
        var remoteBeforeResume = client.Remote.Keys.ToArray();
        client.Requested.Clear();
        await Run(client, Context(value => saved = value) with { MultipartCheckpoint = saved });
        Assert.DoesNotContain(client.Requested, number => remoteBeforeResume.Contains(number));
        Assert.Equal(1, client.Initiates);
        Assert.True(saved!.CopyCompleted);
        Assert.Equal("version1", client.HeadVersionId);
    }

    [Fact]
    public async Task ChangedSourceDoesNotMixExistingPartsOrStartANewUpload()
    {
        using var client = new Client { ETag = "changed" };
        var checkpoint = Saved();
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(client,
            Context(_ => { }) with { MultipartCheckpoint = checkpoint }));
        Assert.Equal(TransferFailureCategory.Conflict, error.Failure.Category);
        Assert.False(error.Failure.Retryable);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Requested);
    }

    [Fact]
    public async Task MissingUploadAfterAmbiguousCompletionIsNotBlindlyRestarted()
    {
        using var client = new Client { MissingUpload = true };
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(client,
            Context(_ => { }) with { MultipartCheckpoint = Saved() }));
        Assert.False(error.Failure.Retryable);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Requested);
    }

    [Fact]
    public async Task InitialCheckpointFailureAbortsNewCopyUpload()
    {
        using var client = new Client();
        await Assert.ThrowsAsync<TransferExecutionException>(() => Run(client,
            Context(_ => throw new IOException("checkpoint disk failed"))));
        Assert.True(client.Aborted);
        Assert.Empty(client.Requested);
    }

    [Fact]
    public async Task CompletedCheckpointDoesNotRepeatPartsOrCompletion()
    {
        using var client = new Client();
        await Run(client, Context(_ => { }) with { MultipartCheckpoint = Saved() with
        {
            CopyCompleted = true, CopyDestinationETag = "completed", CopyDestinationVersionId = "destination-version"
        } });
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Requested);
        Assert.Empty(client.CompletedParts);
    }

    [Fact]
    public async Task UnversionedR2LargeCopyRejectsUnsupportedSourceConditionBeforeInitiate()
    {
        using var client = new Client { Length = 6L * 1024 * 1024 * 1024, VersionId = null };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => S3StorageService.CopyOrMoveObjectAsync(
            client, ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2), "source", "key", "destination", "copy",
            false, TestContext.Current.CancellationToken, Context(_ => { })));
        Assert.False(failure.Failure.Retryable);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Requested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedMovePreservesSourceWhenCompletedDestinationChangedOrDisappeared(bool missing)
    {
        using var client = new Client { DestinationMissing = missing, DestinationETag = "replacement" };
        var saved = Saved() with
        {
            CopyCompleted = true, CopyDestinationETag = "completed", CopyDestinationVersionId = "destination-version"
        };
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() => S3StorageService.CopyOrMoveObjectAsync(
            client, Profile, "source", "key", "destination", "copy", true,
            TestContext.Current.CancellationToken, Context(_ => { }) with { MultipartCheckpoint = saved }));
        Assert.Equal(TransferFailureCategory.Conflict, error.Failure.Category);
        Assert.False(error.Failure.Retryable);
        Assert.Equal(0, client.Deletes);
        Assert.Empty(client.Requested);
    }

    private static MultipartUploadCheckpoint Saved() => new("upload", PartSize, [], Bucket: "destination",
        ObjectKey: "copy", SourceLength: PartSize * 4)
    {
        CopySourceBucket = "source", CopySourceKey = "key", CopySourceETag = "etag1", CopySourceVersionId = "version1"
    };

    private static Task Run(Client client, TransferOperationContext context, CancellationToken? token = null) =>
        S3StorageService.CopyOrMoveObjectAsync(client, Profile, "source", "key", "destination", "copy", false,
            token ?? TestContext.Current.CancellationToken, context);

    private sealed class Client() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        private int _active;
        public int MaximumActive;
        public int Initiates;
        public bool Aborted;
        public bool MissingUpload;
        public bool DestinationMissing;
        public string DestinationETag = "completed";
        public int Deletes;
        public string ETag = "etag1";
        public long Length = PartSize * 4;
        public string? VersionId = "version1";
        public string? HeadVersionId;
        public ConcurrentDictionary<int, long> Remote { get; } = new();
        public ConcurrentBag<int> Requested { get; } = [];
        public int[] CompletedParts { get; private set; } = [];
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            if (request.BucketName == "destination")
            {
                if (DestinationMissing) throw new AmazonS3Exception("missing destination") { StatusCode = HttpStatusCode.NotFound };
                return Task.FromResult(new GetObjectMetadataResponse
                {
                    ContentLength = Length, ETag = DestinationETag, VersionId = "destination-version"
                });
            }
            HeadVersionId = request.VersionId;
            return Task.FromResult(new GetObjectMetadataResponse { ContentLength = Length, ETag = ETag, VersionId = VersionId });
        }
        public override Task<GetObjectTaggingResponse> GetObjectTaggingAsync(GetObjectTaggingRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetObjectTaggingResponse { Tagging = [] });
        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Initiates++;
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload" });
        }
        public override async Task<CopyPartResponse> CopyPartAsync(CopyPartRequest request, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensionsMax(active);
            try
            {
                Assert.Equal("version1", request.SourceVersionId);
                Assert.Equal("etag1", Assert.Single(request.ETagToMatch));
                Requested.Add(request.PartNumber!.Value);
                await Task.Delay(20, cancellationToken);
                Remote[request.PartNumber.Value] = request.LastByte!.Value - request.FirstByte!.Value + 1;
                return new CopyPartResponse { ETag = "part" + request.PartNumber };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        private void InterlockedExtensionsMax(int value)
        {
            int current;
            do { current = MaximumActive; if (current >= value) return; }
            while (Interlocked.CompareExchange(ref MaximumActive, value, current) != current);
        }
        public override Task<ListPartsResponse> ListPartsAsync(ListPartsRequest request, CancellationToken cancellationToken = default)
        {
            if (MissingUpload) throw new AmazonS3Exception("missing") { ErrorCode = "NoSuchUpload", StatusCode = HttpStatusCode.NotFound };
            return Task.FromResult(new ListPartsResponse
            {
                Parts = Remote.OrderBy(item => item.Key).Select(item => new PartDetail
                {
                    PartNumber = item.Key, Size = item.Value, ETag = "part" + item.Key
                }).ToList()
            });
        }
        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            CompletedParts = request.PartETags.Select(part => part.PartNumber!.Value).ToArray();
            return Task.FromResult(new CompleteMultipartUploadResponse { ETag = "completed", VersionId = "destination-version" });
        }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            Deletes++;
            return Task.FromResult(new DeleteObjectResponse());
        }
        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Aborted = true;
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
    }
}
