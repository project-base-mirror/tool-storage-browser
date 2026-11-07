using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class CrossConnectionCopyTests
{
    private const long PartSize = 5L * 1024 * 1024;
    private static readonly ConnectionProfile Source = ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);
    private static readonly ConnectionProfile Destination = ConnectionProfile.CreatePreset(S3ServiceType.MinIO);

    private static TransferOperationContext Context(Action<MultipartUploadCheckpoint>? save = null) => new(
        new TransferExecutionOptions { MultipartThresholdBytes = PartSize, PartSizeBytes = PartSize, MultipartConcurrency = 1 },
        new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, checkpoint, _) =>
        {
            if (checkpoint is not null) save?.Invoke(checkpoint);
            return Task.CompletedTask;
        });

    private static Task Run(Client source, Client destination, TransferOperationContext context,
        ConnectionProfile? destinationProfile = null, CancellationToken? token = null) =>
        S3StorageService.CopyObjectAcrossConnectionsAsync(source, Source, "source", "key", destination,
            destinationProfile ?? Destination, "destination", "copy", context,
            token ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task RegionalAliasSelfCopyFailsBeforeAnySourceOrDestinationRequest()
    {
        using var source = new Client(true);
        using var destination = new Client(false);
        var other = Source with { Id = Guid.NewGuid(), Endpoint = "https://s3.eu-west-1.amazonaws.com" };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            S3StorageService.CopyObjectAcrossConnectionsAsync(source, Source, "bucket", "key",
                destination, other, "bucket", "key", Context(), TestContext.Current.CancellationToken));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.Equal(0, source.HeadReads + destination.HeadReads);
    }

    [Fact]
    public async Task DistinctHostsOnSameProxyAllowSameBucketAndKey()
    {
        using var source = new Client(true);
        using var destination = new Client(false) { ExpectedBucket = "source" };
        var first = Source with { Endpoint = "https://proxy.test", CustomHostHeader = "one.test" };
        var second = Destination with { Endpoint = "https://proxy.test", CustomHostHeader = "two.test" };
        await S3StorageService.CopyObjectAcrossConnectionsAsync(source, first, "source", "key",
            destination, second, "source", "key", Context(), TestContext.Current.CancellationToken);
        Assert.Equal(source.Length, destination.UploadedBytes);
        Assert.Single(destination.Puts);
    }

    [Fact]
    public async Task DistinctClientsPreserveBytesMetadataAndTagsWithoutServerSideCopyOrDelete()
    {
        using var source = new Client(true) { Length = 37 };
        using var destination = new Client(false);
        await Run(source, destination, Context());
        Assert.Equal(37, destination.UploadedBytes);
        var put = Assert.Single(destination.Puts);
        Assert.Equal("destination", put.BucketName);
        Assert.Equal("copy", put.Key);
        Assert.Equal("text/plain", put.Headers.ContentType);
        Assert.Equal("private, max-age=60", put.Headers.CacheControl);
        Assert.Equal("attachment; filename=test.txt", put.Headers.ContentDisposition);
        Assert.Equal("en", put.Headers.ContentLanguage);
        Assert.Equal("value", put.Metadata["x-amz-meta-custom"]);
        Assert.Equal("/new-location", put.WebsiteRedirectLocation);
        Assert.Equal("Wed, 01 Jan 2031 00:00:00 GMT", put.Headers["Expires"]);
        Assert.Equal("project", Assert.Single(put.TagSet).Key);
        Assert.Equal(1, source.TagReads);
        Assert.Equal(0, destination.TagReads);
        Assert.Equal(0, source.Deletes + destination.Deletes);
        var get = Assert.Single(source.Gets);
        Assert.Equal("source", get.BucketName);
        Assert.Equal("key", get.Key);
        Assert.Equal("source-version", get.VersionId);
        Assert.Equal("source-etag", get.EtagToMatch?.Trim('"'));
    }

    [Theory]
    [InlineData(S3ServiceType.AmazonS3)]
    [InlineData(S3ServiceType.MinIO)]
    [InlineData(S3ServiceType.CloudflareR2)]
    [InlineData(S3ServiceType.BackblazeB2)]
    [InlineData(S3ServiceType.AliyunOss)]
    [InlineData(S3ServiceType.TencentCos)]
    [InlineData(S3ServiceType.GoogleCloudStorage)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    [InlineData(S3ServiceType.Custom)]
    public async Task AllDestinationProvidersSupportStreamingCopy(S3ServiceType type)
    {
        using var source = new Client(true) { Length = 23 };
        using var destination = new Client(false);
        await Run(source, destination, Context(), ConnectionProfile.CreatePreset(type) with { Endpoint = "https://destination.example.test" });
        Assert.Equal(23, destination.UploadedBytes);
        Assert.Single(destination.Puts);
    }

    [Fact]
    public async Task EmptyFolderMarkerIsUploadedAsZeroBytes()
    {
        using var source = new Client(true) { Length = 0 };
        using var destination = new Client(false);
        await S3StorageService.CopyObjectAcrossConnectionsAsync(source, Source, "source", "folder/", destination,
            Destination, "destination", "folder/", Context(), TestContext.Current.CancellationToken);
        Assert.Equal("folder/", Assert.Single(destination.Puts).Key);
        Assert.Equal(0, destination.UploadedBytes);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(10485760)]
    public async Task BothBandwidthLanesWorkWithOneRequestPermit(long length)
    {
        using var source = new Client(true) { Length = length };
        using var destination = new Client(false);
        var limiter = new CountingLimiter();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await Run(source, destination, Context() with
        {
            RequestBudget = new SharedTransferRequestBudget(1), BandwidthLimiter = limiter
        }, token: timeout.Token);
        Assert.Equal(length, limiter.Download);
        Assert.Equal(length, limiter.Upload);
        Assert.Equal(length, destination.UploadedBytes);
    }

    [Fact]
    public async Task CancellationRetainsCheckpointAndResumesOnlyMissingParts()
    {
        using var source = new Client(true) { Length = PartSize * 3 };
        using var destination = new Client(false);
        using var pause = new CancellationTokenSource();
        MultipartUploadCheckpoint? saved = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(source, destination, Context(value =>
        {
            saved = value;
            if (value.CompletedParts.Count > 0) pause.Cancel();
        }), token: pause.Token));
        Assert.NotNull(saved);
        Assert.False(destination.Aborted);
        Assert.Equal(Source.Id, saved.CopySourceProfileId);
        Assert.Equal(Destination.Id, saved.CopyDestinationProfileId);
        var existing = destination.Remote.Keys.ToArray();
        destination.Parts.Clear();
        await Run(source, destination, Context(value => saved = value) with { MultipartCheckpoint = saved });
        Assert.DoesNotContain(destination.Parts, existing.Contains);
        Assert.Equal(1, destination.Initiates);
        Assert.True(saved!.CopyCompleted);
        Assert.Equal(0, source.Deletes + destination.Deletes);
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("source-profile")]
    [InlineData("destination-profile")]
    [InlineData("length")]
    [InlineData("key")]
    public async Task ChangedIdentityRejectsResumeBeforeAnyUpload(string changed)
    {
        using var source = new Client(true) { Length = PartSize * 3 };
        using var destination = new Client(false);
        var saved = Saved();
        saved = changed switch
        {
            "etag" => saved with { CopySourceETag = "changed" },
            "source-profile" => saved with { CopySourceProfileId = Guid.NewGuid() },
            "destination-profile" => saved with { CopyDestinationProfileId = Guid.NewGuid() },
            "length" => saved with { SourceLength = 1 },
            _ => saved with { ObjectKey = "other" }
        };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination,
            Context() with { MultipartCheckpoint = saved }));
        Assert.False(failure.Failure.Retryable);
        Assert.Equal(0, destination.Initiates);
        Assert.Empty(destination.Parts);
        Assert.Empty(source.Gets);
    }

    [Fact]
    public async Task InitialCheckpointFailureAbortsNewUpload()
    {
        using var source = new Client(true) { Length = PartSize * 3 };
        using var destination = new Client(false);
        await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination,
            Context(_ => throw new IOException("checkpoint disk failed"))));
        Assert.True(destination.Aborted);
        Assert.Empty(destination.Parts);
        Assert.Equal(0, source.Deletes);
    }

    [Fact]
    public async Task NonRetainedDestinationFailureAbortsUploadAndNeverDeletesSource()
    {
        using var source = new Client(true) { Length = PartSize * 3 };
        using var destination = new Client(false) { FailUpload = true };
        var context = Context();
        await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination, context with
        {
            Options = context.Options with { RetainFailedTransferState = false }
        }));
        Assert.True(destination.Aborted);
        Assert.Equal(0, source.Deletes + destination.Deletes);
    }

    [Fact]
    public async Task MissingSourceIdentityIsRejectedWithoutDestinationWrite()
    {
        using var source = new Client(true) { ETag = null };
        using var destination = new Client(false);
        await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination, Context()));
        Assert.Empty(destination.Puts);
        Assert.Equal(0, destination.Initiates);
    }

    [Fact]
    public async Task MissingRemoteUploadDoesNotSilentlyStartOver()
    {
        using var source = new Client(true) { Length = PartSize * 3 };
        using var destination = new Client(false) { MissingUpload = true };
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination,
            Context() with { MultipartCheckpoint = Saved() }));
        Assert.False(error.Failure.Retryable);
        Assert.Equal(0, destination.Initiates);
        Assert.Empty(source.Gets);
    }

    [Fact]
    public async Task CompletedCheckpointValidatesDestinationWithoutRepeatingUpload()
    {
        using var source = new Client(true) { Length = PartSize * 3 };
        using var destination = new Client(false) { ExistingDestination = true, Length = PartSize * 3 };
        await Run(source, destination, Context() with
        {
            MultipartCheckpoint = Saved() with { CopyCompleted = true, CopyDestinationETag = "destination-etag", CopyDestinationVersionId = "destination-version" }
        });
        Assert.Equal(0, destination.Initiates);
        Assert.Empty(source.Gets);
        Assert.Empty(destination.Puts);
        Assert.Empty(destination.Parts);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(10485760)]
    public async Task CustomProviderCreateOnlyPerformsPreflightInsteadOfBlindlyBlocking(long length)
    {
        using var source = new Client(true) { Length = length };
        using var destination = new Client(false);
        var context = Context();
        await Run(source, destination, context with { Options = context.Options with { AllowDestinationOverwrite = false } },
            ConnectionProfile.CreatePreset(S3ServiceType.Custom));
        Assert.True(destination.HeadReads >= 2);
        Assert.Equal(length, destination.UploadedBytes);
    }

    [Fact]
    public async Task MissingMetadataRejectsCopyBeforeReadingOrWritingBytes()
    {
        using var source = new Client(true) { MissingMetadata = true };
        using var destination = new Client(false);
        await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination, Context()));
        Assert.Empty(source.Gets);
        Assert.Empty(destination.Puts);
    }

    [Fact]
    public async Task FallbackDestinationRaceBeforeCompletionDoesNotCompleteOrDeleteSource()
    {
        using var source = new Client(true) { Length = PartSize * 2 };
        using var destination = new Client(false) { DestinationAppearsAfterParts = true };
        var context = Context();
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination,
            context with { Options = context.Options with { AllowDestinationOverwrite = false, RetainFailedTransferState = false } },
            ConnectionProfile.CreatePreset(S3ServiceType.Custom)));
        Assert.Equal(TransferFailureCategory.Conflict, error.Failure.Category);
        Assert.Equal(0, destination.Completions);
        Assert.True(destination.Aborted);
        Assert.Equal(0, source.Deletes);
    }

    [Fact]
    public async Task ChangedGetIdentityIsRejectedBeforeDestinationWrite()
    {
        using var source = new Client(true) { ChangedGetIdentity = true };
        using var destination = new Client(false);
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(source, destination, Context()));
        Assert.Equal(TransferFailureCategory.Conflict, error.Failure.Category);
        Assert.Empty(destination.Puts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargePartTemporaryFilesAreRemovedAfterSuccessOrFailure(bool fail)
    {
        using var source = new Client(true) { Length = 17L * 1024 * 1024 };
        using var destination = new Client(false) { FailUpload = fail, ObserveTemporaryFiles = true, UploadPartSize = source.Length };
        var context = Context();
        var operation = Run(source, destination, context with { Options = context.Options with
        {
            PartSizeBytes = source.Length, RetainFailedTransferState = false
        } });
        if (fail) await Assert.ThrowsAsync<TransferExecutionException>(() => operation);
        else await operation;
        Assert.NotEmpty(destination.TemporaryFiles);
        Assert.All(destination.TemporaryFiles, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task ProviderIgnoringRangesStillCopiesTheCorrectBytesForEachPart()
    {
        using var source = new Client(true) { Length = PartSize * 3, IgnoreRange = true };
        using var destination = new Client(false);
        await Run(source, destination, Context());
        Assert.Equal(source.Length, destination.UploadedBytes);
        Assert.Equal(3, destination.Parts.Count);
    }

    [Fact]
    public async Task PublicApiSignsEachEndpointWithItsOwnCredentialsAndDestinationCondition()
    {
        using var transport = new ProtocolTransport();
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var source = Source with
        {
            Name = "source", Endpoint = "https://source.example.test", AccessKey = "source-access", SecretKey = "source-secret",
            AddressingStyle = AddressingStyle.PathStyle
        };
        var destination = Destination with
        {
            Name = "destination", Endpoint = "https://destination.example.test", AccessKey = "destination-access", SecretKey = "destination-secret",
            AddressingStyle = AddressingStyle.PathStyle
        };
        var context = Context();
        await service.CopyObjectAcrossConnectionsAsync(source, "source", "key", destination, "destination", "copy",
            context with { Options = context.Options with { AllowDestinationOverwrite = false } }, TestContext.Current.CancellationToken);
        Assert.Contains(transport.Requests, r => r.Method == HttpMethod.Get && r.Uri.Host == "source.example.test");
        var put = Assert.Single(transport.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal("destination.example.test", put.Uri.Host);
        Assert.Equal("/destination/copy", put.Uri.AbsolutePath);
        Assert.Equal("*", put.Headers["If-None-Match"]);
        Assert.DoesNotContain("x-amz-copy-source", put.Headers.Keys);
        foreach (var request in transport.Requests)
        {
            var access = request.Uri.Host == "source.example.test" ? "source-access" : "destination-access";
            Assert.Contains("Credential=" + access + "/", request.Headers["Authorization"], StringComparison.Ordinal);
        }
        var get = Assert.Single(transport.Requests, r => r.Method == HttpMethod.Get && !r.Uri.Query.Contains("tagging"));
        Assert.Equal("\"source-etag\"", get.Headers["If-Match"]);
        Assert.Contains("versionId=source-version", get.Uri.Query, StringComparison.Ordinal);
    }

    private sealed record WireRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers);
    private sealed class ProtocolTransport : HttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public List<WireRequest> Requests { get; } = [];
        public override HttpClient CreateHttpClient(IClientConfig config) => _client ??= new HttpClient(new ProtocolHandler(this));
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public void Dispose() => _client?.Dispose();
        private sealed class ProtocolHandler(ProtocolTransport owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(new(request.Method, request.RequestUri!, request.Headers.ToDictionary(p => p.Key,
                    p => string.Join(",", p.Value), StringComparer.OrdinalIgnoreCase)));
                if (request.RequestUri!.Host == "destination.example.test")
                {
                    if (request.Method == HttpMethod.Head) return new(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
                    Assert.Equal(HttpMethod.Put, request.Method);
                    Assert.NotNull(request.Content);
                    var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    Assert.NotEmpty(body);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                    response.Headers.TryAddWithoutValidation("ETag", "\"destination-etag\"");
                    return response;
                }
                Assert.Equal("source.example.test", request.RequestUri.Host);
                if (request.RequestUri.Query.Contains("tagging"))
                    return new(HttpStatusCode.OK) { Content = new StringContent("<Tagging xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><TagSet /></Tagging>") };
                var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : new byte[17]) };
                result.Content.Headers.ContentLength = 17;
                result.Headers.TryAddWithoutValidation("ETag", "\"source-etag\"");
                result.Headers.TryAddWithoutValidation("x-amz-version-id", "source-version");
                return result;
            }
        }
    }

    private static MultipartUploadCheckpoint Saved() => new("upload", PartSize, [], Bucket: "destination",
        ObjectKey: "copy", SourceLength: PartSize * 3)
    {
        CopySourceBucket = "source", CopySourceKey = "key", CopySourceETag = "source-etag",
        CopySourceVersionId = "source-version", CopySourceProfileId = Source.Id, CopyDestinationProfileId = Destination.Id
    };

    private sealed class CountingLimiter : ITransferBandwidthLimiter
    {
        public long Upload;
        public long Download;
        public void Configure(long uploadBytesPerSecond, long downloadBytesPerSecond) { }
        public ValueTask WaitAsync(TransferDirection direction, int bytes, CancellationToken cancellationToken)
        {
            if (direction == TransferDirection.Upload) Interlocked.Add(ref Upload, bytes);
            else Interlocked.Add(ref Download, bytes);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Client(bool source) : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL = source ? "http://127.0.0.1:1" : "http://127.0.0.1:2" })
    {
        public string? ExpectedBucket;
        public long Length = 31;
        public string? ETag = "source-etag";
        public int TagReads;
        public int Deletes;
        public int Initiates;
        public bool Aborted;
        public bool FailUpload;
        public bool MissingUpload;
        public bool ExistingDestination;
        public bool MissingMetadata;
        public int HeadReads;
        public int Completions;
        public bool DestinationAppearsAfterParts;
        public bool ChangedGetIdentity;
        public bool ObserveTemporaryFiles;
        public bool IgnoreRange;
        public long UploadPartSize = PartSize;
        public string[] TemporaryFiles = [];
        public long UploadedBytes;
        public ConcurrentBag<GetObjectRequest> Gets { get; } = [];
        public ConcurrentBag<PutObjectRequest> Puts { get; } = [];
        public ConcurrentBag<int> Parts { get; } = [];
        public ConcurrentDictionary<int, long> Remote { get; } = new();

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ExpectedBucket ?? (source ? "source" : "destination"), request.BucketName);
            HeadReads++;
            if (!source && (ExistingDestination || DestinationAppearsAfterParts && !Remote.IsEmpty)) return Task.FromResult(new GetObjectMetadataResponse { ContentLength = Length, ETag = "destination-etag", VersionId = "destination-version" });
            if (!source) throw new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound };
            var result = new GetObjectMetadataResponse { MissingMeta = MissingMetadata ? 1 : 0, ContentLength = Length, ETag = ETag, VersionId = "source-version", WebsiteRedirectLocation = "/new-location" };
            result.Headers.ContentType = "text/plain";
            result.Headers.CacheControl = "private, max-age=60";
            result.Headers.ContentDisposition = "attachment; filename=test.txt";
            result.Headers.ContentLanguage = "en";
            result.ExpiresString = "Wed, 01 Jan 2031 00:00:00 GMT";
            result.Metadata.Add("custom", "value");
            return Task.FromResult(result);
        }
        public override Task<GetObjectTaggingResponse> GetObjectTaggingAsync(GetObjectTaggingRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(source);
            TagReads++;
            return Task.FromResult(new GetObjectTaggingResponse { Tagging = [new Tag { Key = "project", Value = "copy" }] });
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(source);
            Gets.Add(request);
            var length = request.ByteRange is null || IgnoreRange ? Length : Math.Min(Length - request.ByteRange.Start, request.ByteRange.End - request.ByteRange.Start + 1);
            return Task.FromResult(new GetObjectResponse
            {
                HttpStatusCode = request.ByteRange is null || IgnoreRange ? HttpStatusCode.OK : HttpStatusCode.PartialContent,
                ContentRange = request.ByteRange is null ? null : $"bytes {request.ByteRange.Start}-{request.ByteRange.Start + length - 1}/{Length}",
                ContentLength = length, ETag = ChangedGetIdentity ? "changed" : ETag, VersionId = "source-version", ResponseStream = new MemoryStream(Pattern(request.ByteRange is null || IgnoreRange ? 0 : request.ByteRange.Start, checked((int)length)))
            });
        }
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(source);
            Puts.Add(request);
            UploadedBytes += await Drain(request.InputStream, cancellationToken);
            return new PutObjectResponse { ETag = "destination-etag", VersionId = "destination-version" };
        }
        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(source);
            Initiates++;
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload" });
        }
        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(source);
            Assert.Equal("destination", request.BucketName);
            Assert.Equal("copy", request.Key);
            Assert.Equal("upload", request.UploadId);
            Assert.Equal(request.InputStream.Length, request.PartSize);
            if (ObserveTemporaryFiles) TemporaryFiles = Directory.GetFiles(Path.GetTempPath(), "s3explorer-copy-*.part");
            if (FailUpload) throw new AmazonS3Exception("denied") { StatusCode = HttpStatusCode.Forbidden };
            Parts.Add(request.PartNumber!.Value);
            var count = await Drain(request.InputStream, cancellationToken, (request.PartNumber.Value - 1) * UploadPartSize);
            Interlocked.Add(ref UploadedBytes, count);
            Remote[request.PartNumber.Value] = count;
            return new UploadPartResponse { ETag = "part" + request.PartNumber, PartNumber = request.PartNumber.Value };
        }
        public override Task<ListPartsResponse> ListPartsAsync(ListPartsRequest request, CancellationToken cancellationToken = default) =>
            MissingUpload ? throw new AmazonS3Exception("missing") { ErrorCode = "NoSuchUpload", StatusCode = HttpStatusCode.NotFound } : Task.FromResult(new ListPartsResponse { Parts = Remote.OrderBy(p => p.Key).Select(p => new PartDetail { PartNumber = p.Key, Size = p.Value, ETag = "part" + p.Key }).ToList() });
        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Completions++;
            return Task.FromResult(new CompleteMultipartUploadResponse { ETag = "destination-etag", VersionId = "destination-version" });
        }
        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.False(source);
            Assert.Equal("destination", request.BucketName);
            Assert.Equal("copy", request.Key);
            Assert.Equal("upload", request.UploadId);
            Aborted = true;
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            Deletes++;
            throw new InvalidOperationException("Cross-connection copy must never delete objects.");
        }
        public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cross-connection copy must not use CopyObject.");
        public override Task<CopyPartResponse> CopyPartAsync(CopyPartRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cross-connection copy must not use CopyPart.");
        private static byte[] Pattern(long offset, int length)
        {
            var data = new byte[length];
            for (var i = 0; i < length; i++) data[i] = (byte)((offset + i) % 251);
            return data;
        }
        private static async Task<long> Drain(Stream stream, CancellationToken token, long offset = 0)
        {
            var buffer = new byte[81920];
            long count = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                Assert.True(buffer.AsSpan(0, read).SequenceEqual(Pattern(offset + count, read)), "Payload differs from the selected source range.");
                count += read;
            }
            return count;
        }
    }
}
