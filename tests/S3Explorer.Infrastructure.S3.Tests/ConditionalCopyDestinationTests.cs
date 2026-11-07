using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Model.Internal.MarshallTransformations;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConditionalCopyDestinationTests
{
    private static TransferOperationContext Context(bool overwrite = false, string? expected = null) => new(
        new TransferExecutionOptions
        {
            AllowDestinationOverwrite = overwrite, ExpectedDestinationETag = expected,
            RetainFailedTransferState = false, MultipartThresholdBytes = 5L * 1024 * 1024,
            PartSizeBytes = 5L * 1024 * 1024
        }, new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);

    private static Task Run(Client client, S3ServiceType provider, TransferOperationContext context,
        bool move = false, bool multipartEnabled = true) =>
        S3StorageService.CopyOrMoveObjectAsync(client,
            ConnectionProfile.CreatePreset(provider) with { Endpoint = "https://copy.example.test", EnableMultipartCopy = multipartEnabled },
            "source", "key", "destination", "copy", move, TestContext.Current.CancellationToken, context);

    [Fact]
    public void PinnedSdkSerializesCopySourceAndDestinationConditionsSeparately()
    {
        var create = CopyObjectRequestMarshaller.Instance.Marshall(new CopyObjectRequest
        {
            SourceBucket = "source", SourceKey = "key", DestinationBucket = "target", DestinationKey = "key",
            ETagToMatch = "source-etag", IfNoneMatch = "*"
        });
        Assert.Equal("source-etag", create.Headers["x-amz-copy-source-if-match"]);
        Assert.Equal("*", create.Headers["If-None-Match"]);
        var replace = CopyObjectRequestMarshaller.Instance.Marshall(new CopyObjectRequest
        {
            SourceBucket = "source", SourceKey = "key", DestinationBucket = "target", DestinationKey = "key",
            ETagToMatch = "source-etag", IfMatch = "destination-etag"
        });
        Assert.Equal("source-etag", replace.Headers["x-amz-copy-source-if-match"]);
        Assert.Equal("destination-etag", replace.Headers["If-Match"]);
        var complete = CompleteMultipartUploadRequestMarshaller.Instance.Marshall(new CompleteMultipartUploadRequest
        {
            BucketName = "target", Key = "key", UploadId = "upload", PartETags = [], IfNoneMatch = "*"
        });
        Assert.Equal("*", complete.Headers["If-None-Match"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmazonCopyOrMoveRejectsConcurrentDestinationCreationWithoutDeletingSource(bool move)
    {
        using var client = new Client { ConcurrentDestination = true };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.AmazonS3, Context(), move));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.Equal("*", client.Copy!.IfNoneMatch);
        Assert.False(client.DestinationOverwritten);
        Assert.Equal(0, client.Deletes);
        Assert.Equal(0, client.Initiates);
        Assert.Equal(1, client.Heads); // No destination HEAD/check-then-copy race.
    }

    [Fact]
    public async Task AmazonConditionalReplacementIncludesDestinationEtag()
    {
        using var client = new Client();
        await Run(client, S3ServiceType.AmazonS3, Context(overwrite: true, expected: "target-etag"));
        Assert.Equal("target-etag", client.Copy!.IfMatch);
        Assert.Null(client.Copy.IfNoneMatch);
    }

    [Fact]
    public async Task MinioSmallCreateOnlyCopyUsesConditionalCompletionWithoutUnsupportedRange()
    {
        using var client = new Client();
        await Run(client, S3ServiceType.MinIO, Context());
        Assert.Null(client.Copy);
        Assert.Equal(1, client.Initiates);
        Assert.Equal("*", client.Complete!.IfNoneMatch);
        var part = Assert.Single(client.Parts);
        Assert.Null(part.FirstByte);
        Assert.Null(part.LastByte);
        Assert.Equal("source-version", part.SourceVersionId);
        Assert.Equal("source-etag", Assert.Single(part.ETagToMatch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipartDestinationConflictNeverFallsBackOrDeletesSource(bool matchExisting)
    {
        using var client = new Client { Length = 6L * 1024 * 1024, ConcurrentDestination = true };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.AmazonS3, Context(matchExisting, matchExisting ? "old-target" : null), move: true));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.Null(client.Copy);
        Assert.NotNull(client.Complete);
        Assert.Equal(matchExisting ? null : "*", client.Complete.IfNoneMatch);
        Assert.Equal(matchExisting ? "old-target" : null, client.Complete.IfMatch);
        Assert.False(client.DestinationOverwritten);
        Assert.Equal(0, client.Deletes);
        Assert.True(client.Aborted); // No durable owner for this transfer.
    }

    [Theory]
    [InlineData(S3ServiceType.SupabaseStorage)]
    [InlineData(S3ServiceType.Custom)]
    public async Task UnsupportedConditionalReplacementFailsBeforeAnyRequest(S3ServiceType provider)
    {
        using var client = new Client();
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(client, provider, Context(overwrite: true, expected: "old-target")));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Equal(0, client.Heads);
        Assert.Equal(0, client.Initiates);
        Assert.Null(client.Copy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinioEmptyCreateOnlyCopyUsesConditionalPutAndPreservesProperties(bool multipartEnabled)
    {
        using var client = new Client { Length = 0 };
        await Run(client, S3ServiceType.MinIO, Context(), multipartEnabled: multipartEnabled);
        Assert.Equal("*", client.Put!.IfNoneMatch);
        Assert.Null(client.Put.IfMatch);
        Assert.Equal(0, client.PutLength);
        Assert.Equal("source-etag", client.Get!.EtagToMatch);
        Assert.Equal("source-version", client.Get.VersionId);
        Assert.Null(client.Get.ByteRange);
        Assert.Equal("source-version", client.Tags!.VersionId);
        Assert.Equal("application/test", client.Put.Headers.ContentType);
        Assert.Equal("max-age=123", client.Put.Headers.CacheControl);
        Assert.Equal("gzip", client.Put.Headers.ContentEncoding);
        Assert.Equal("attachment", client.Put.Headers.ContentDisposition);
        Assert.Equal("en", client.Put.Headers.ContentLanguage);
        Assert.Equal(client.Expires, client.Put.Headers.Expires);
        Assert.Equal("verified-value", client.Put.Metadata["custom"]);
        Assert.Equal("tag-value", Assert.Single(client.Put.TagSet).Value);
        Assert.Equal(0, client.Initiates);
        Assert.Null(client.Copy);
        Assert.Null(client.Complete);
        Assert.Equal(0, client.Deletes);
    }

    [Fact]
    public async Task MinioEmptyCopyDoesNotInventAbsentExpiresHeader()
    {
        using var client = new Client { Length = 0, HasExpires = false };
        await Run(client, S3ServiceType.MinIO, Context());
        Assert.Null(client.Put!.Headers.Expires);
        Assert.Null(client.Put.Headers["Expires"]);
    }

    [Fact]
    public void MultipartCopyDoesNotInventAbsentExpiresHeader()
    {
        var request = S3StorageService.CreateMultipartCopyRequest(new GetObjectMetadataResponse(), "bucket", "key");
        Assert.Null(request.Headers.Expires);
        Assert.Null(request.Headers["Expires"]);
    }

    [Fact]
    public void MultipartCopyRetainsRawExpiresWithoutParsingIt()
    {
        var source = new GetObjectMetadataResponse();
        source.ExpiresString = "0";
        var request = S3StorageService.CreateMultipartCopyRequest(source, "bucket", "key");
        Assert.Equal("0", request.Headers["Expires"]);
    }

    [Fact]
    public async Task MinioEmptyConditionalReplacementUsesExpectedDestinationEtag()
    {
        using var client = new Client { Length = 0 };
        await Run(client, S3ServiceType.MinIO, Context(overwrite: true, expected: "old-target"));
        Assert.Equal("old-target", client.Put!.IfMatch);
        Assert.Null(client.Put.IfNoneMatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinioEmptyDestinationConflictNeverFallsBackToUnconditionalWrite(bool matchExisting)
    {
        using var client = new Client { Length = 0, ConcurrentDestination = true };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.MinIO, Context(matchExisting, matchExisting ? "old-target" : null)));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.False(client.DestinationOverwritten);
        Assert.NotNull(client.Put);
        Assert.Equal(1, client.PutCalls);
        Assert.Null(client.Copy);
        Assert.Equal(0, client.Initiates);
        Assert.Equal(0, client.Deletes);
    }

    [Theory]
    [InlineData("precondition")]
    [InlineData("etag")]
    [InlineData("version")]
    [InlineData("length")]
    [InlineData("body")]
    public async Task MinioEmptyChangedOrInvalidSourceNeverWritesDestination(string change)
    {
        using var client = new Client { Length = 0, SourceChange = change };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.MinIO, Context()));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.Null(client.Put);
        Assert.Null(client.Copy);
        Assert.Null(client.Tags);
        Assert.Equal(0, client.Initiates);
    }

    [Fact]
    public async Task MinioEmptyCopyRejectsIncompleteMetadataBeforeWriting()
    {
        using var client = new Client { Length = 0, SourceChange = "metadata" };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.MinIO, Context()));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Null(client.Put);
        Assert.Null(client.Copy);
        Assert.Null(client.Tags);
    }

    [Fact]
    public async Task MinioProtectedCopyUsesRelayWhenServerSideMultipartIsDisabled()
    {
        using var client = new Client();
        await Run(client, S3ServiceType.MinIO, Context(), multipartEnabled: false);
        Assert.Null(client.Copy);
        Assert.Equal("*", client.Put!.IfNoneMatch);
        Assert.Equal(client.Length, client.PutLength);
        Assert.Equal(0, client.Initiates);
    }

    [Fact]
    public async Task AmazonEmptyCreateOnlyCopyRetainsAtomicDestinationCondition()
    {
        using var client = new Client { Length = 0 };
        await Run(client, S3ServiceType.AmazonS3, Context());
        Assert.Equal("*", client.Copy!.IfNoneMatch);
        Assert.Equal(0, client.Initiates);
    }

    [Fact]
    public async Task ExplicitOverwriteDoesNotPretendToProtectDestination()
    {
        using var client = new Client();
        await Run(client, S3ServiceType.AmazonS3, Context(overwrite: true));
        Assert.Null(client.Copy!.IfNoneMatch);
        Assert.Null(client.Copy.IfMatch);
    }

    [Theory]
    [InlineData(S3ServiceType.SupabaseStorage, true)]
    [InlineData(S3ServiceType.SupabaseStorage, false)]
    [InlineData(S3ServiceType.CloudflareR2, true)]
    [InlineData(S3ServiceType.CloudflareR2, false)]
    [InlineData(S3ServiceType.AmazonS3, false)]
    [InlineData(S3ServiceType.AliyunOss, false)]
    public async Task OversizedCopiesStartIdentityPinnedRelayInsteadOfUnsafeCopyParts(S3ServiceType provider, bool enabled)
    {
        using var client = new Client { Length = 6L * 1024 * 1024 * 1024, StopOnRelayGet = true };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, provider, Context(overwrite: true), multipartEnabled: enabled));
        Assert.Equal(TransferFailureCategory.Authorization, failure.Failure.Category);
        Assert.Equal(1, client.Initiates);
        Assert.NotNull(client.Get);
        Assert.Equal(provider is S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage ? null : "source-version", client.Get.VersionId);
        Assert.Equal("source-etag", client.Get.EtagToMatch);
        Assert.Empty(client.Parts);
        Assert.Null(client.Copy);
        Assert.True(client.Aborted);
    }

    [Theory]
    [InlineData(6L * 1024 * 1024, null)]
    [InlineData(5L * 1024 * 1024 * 1024, "null")]
    [InlineData(6L * 1024 * 1024, "unverified-version")]
    public async Task SupabaseCopyAboveOptionalThresholdUsesSingleConditionalSourceCopy(long length, string? versionId)
    {
        using var client = new Client { Length = length, VersionId = versionId };
        await Run(client, S3ServiceType.SupabaseStorage, Context(overwrite: true));
        Assert.Equal("source-etag", client.Copy!.ETagToMatch);
        Assert.Null(client.Copy.SourceVersionId);
        Assert.Null(client.Copy.IfMatch);
        Assert.Null(client.Copy.IfNoneMatch);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Parts);
        Assert.Equal(0, client.Deletes);
    }

    [Theory]
    [InlineData(S3ServiceType.CloudflareR2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public async Task ProviderSingleCopyLimitFallsBackToPinnedReadAndUpload(S3ServiceType provider)
    {
        using var client = new Client { Length = 6L * 1024 * 1024, CopyRequiresMultipart = true };
        await Run(client, provider, Context(overwrite: true));
        Assert.NotNull(client.Copy);
        Assert.Equal(1, client.Initiates);
        Assert.Empty(client.Parts);
        Assert.Equal(2, client.UploadParts);
        Assert.NotNull(client.Complete);
        Assert.Equal(0, client.Deletes);
    }

    [Theory]
    [InlineData(S3ServiceType.CloudflareR2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public async Task CompletedSameConnectionRelayResumesByVerifyingTargetWithoutCopyingAgain(S3ServiceType provider)
    {
        using var client = new Client { Length = 6L * 1024 * 1024, CopyRequiresMultipart = true };
        var profile = ConnectionProfile.CreatePreset(provider) with { Endpoint = "https://copy.example.test" };
        MultipartUploadCheckpoint? saved = null;
        var context = Context() with
        {
            UpdateCheckpointAsync = (_, _, checkpoint, _) => { saved = checkpoint; return Task.CompletedTask; }
        };
        await S3StorageService.CopyOrMoveObjectAsync(client, profile, "source", "key", "destination", "copy",
            false, TestContext.Current.CancellationToken, context);
        Assert.NotNull(saved);
        Assert.True(saved.CopyCompleted);
        Assert.Equal(profile.Id, saved.CopySourceProfileId);
        Assert.Equal(profile.Id, saved.CopyDestinationProfileId);
        Assert.Null(saved.CopySourceVersionId);
        var parts = client.UploadParts;
        await S3StorageService.CopyOrMoveObjectAsync(client, profile, "source", "key", "destination", "copy",
            false, TestContext.Current.CancellationToken, context with { MultipartCheckpoint = saved });
        Assert.Equal(1, client.Initiates);
        Assert.Equal(parts, client.UploadParts);
        Assert.Equal(0, client.Deletes);
    }

    [Fact]
    public async Task SupabaseSourceConflictNeverFallsBackToMultipart()
    {
        using var client = new Client { Length = 6L * 1024 * 1024, SourceChange = "precondition" };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.SupabaseStorage, Context(overwrite: true)));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.Equal("source-etag", client.Copy!.ETagToMatch);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Parts);
        Assert.Equal(0, client.Deletes);
    }

    [Fact]
    public async Task SupabaseOldMultipartCheckpointIsNotSilentlyConvertedToSingleCopy()
    {
        using var client = new Client { Length = 6L * 1024 * 1024 };
        var context = Context(overwrite: true) with
        {
            MultipartCheckpoint = new MultipartUploadCheckpoint("upload", 5L * 1024 * 1024, [],
                Bucket: "destination", ObjectKey: "copy", SourceLength: client.Length)
            {
                CopySourceBucket = "source", CopySourceKey = "key",
                CopySourceETag = "source-etag", CopySourceVersionId = "source-version"
            }
        };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.SupabaseStorage, context));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Null(client.Copy);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Parts);
        Assert.False(client.Aborted);
    }

    [Theory]
    [InlineData(S3ServiceType.AliyunOss)]
    [InlineData(S3ServiceType.TencentCos)]
    [InlineData(S3ServiceType.GoogleCloudStorage)]
    [InlineData(S3ServiceType.BackblazeB2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    [InlineData(S3ServiceType.Custom)]
    public async Task CompatibleCreateOnlyCopyChecksDestinationInsteadOfBlockingOrdinaryCopy(S3ServiceType provider)
    {
        using var client = new Client();
        await Run(client, provider, Context());
        Assert.NotNull(client.Copy);
        Assert.Null(client.Copy.IfNoneMatch);
        Assert.Null(client.Copy.IfMatch);
        Assert.Equal(2, client.DestinationHeads);
        Assert.Equal(0, client.Deletes);
    }

    [Fact]
    public async Task CompatibleCopyRechecksDestinationJustBeforeCommit()
    {
        using var client = new Client { DestinationAppearsOnSecondCheck = true };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.AliyunOss, Context()));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.Null(client.Copy);
        Assert.Equal(0, client.Deletes);
    }

    [Fact]
    public async Task CompatibleMultipartCopyRechecksBeforeCompletionWithoutUnsupportedHeaders()
    {
        using var client = new Client { Length = 6L * 1024 * 1024 };
        await Run(client, S3ServiceType.AliyunOss, Context());
        Assert.Equal(2, client.DestinationHeads);
        Assert.NotNull(client.Complete);
        Assert.Null(client.Complete.IfNoneMatch);
        Assert.Null(client.Complete.IfMatch);
    }

    [Theory]
    [InlineData(S3ServiceType.SupabaseStorage)]
    [InlineData(S3ServiceType.MinIO)]
    [InlineData(S3ServiceType.AliyunOss)]
    [InlineData(S3ServiceType.CloudflareR2)]
    public async Task CompatibleMoveCopiesThenRechecksSourceBeforeOrdinaryDelete(S3ServiceType provider)
    {
        using var client = new Client();
        await Run(client, provider, Context(overwrite: true), move: true);
        Assert.NotNull(client.Copy);
        Assert.Equal(2, client.Heads);
        Assert.Equal(1, client.Deletes);
        Assert.Null(client.Delete!.IfMatch);
        Assert.Null(client.Delete.VersionId);
    }

    [Fact]
    public async Task CompatibleMoveKeepsChangedSourceAndNeverDeletesAfterFailedRecheck()
    {
        using var client = new Client { SourceChange = "head-after-copy" };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.SupabaseStorage, Context(overwrite: true), move: true));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.NotNull(client.Copy);
        Assert.Equal(0, client.Deletes);
    }

    private sealed class Client() : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public long Length { get; init; } = 1024;
        public string? VersionId { get; init; } = "source-version";
        public bool ConcurrentDestination { get; init; }
        public bool DestinationAppearsOnSecondCheck { get; init; }
        public bool StopOnRelayGet { get; init; }
        public int DestinationHeads;
        public int UploadParts;
        public DeleteObjectRequest? Delete;
        public string? SourceChange { get; init; }
        public bool CopyRequiresMultipart { get; init; }
        public bool HasExpires { get; init; } = true;
        public DateTime Expires { get; } = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public GetObjectRequest? Get;
        public GetObjectTaggingRequest? Tags;
        public PutObjectRequest? Put;
        public long PutLength;
        public int PutCalls;
        public int Heads;
        public int Initiates;
        public int Deletes;
        public bool Aborted;
        public bool DestinationOverwritten;
        public CopyObjectRequest? Copy;
        public CompleteMultipartUploadRequest? Complete;
        public System.Collections.Concurrent.ConcurrentBag<CopyPartRequest> Parts { get; } = [];

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Heads++;
            if (request.BucketName == "destination")
            {
                DestinationHeads++;
                if (Complete is not null)
                    return Task.FromResult(new GetObjectMetadataResponse { ContentLength = Length, ETag = "completed" });
                if (!DestinationAppearsOnSecondCheck || DestinationHeads < 2)
                    throw new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound };
            }
            return Task.FromResult(new GetObjectMetadataResponse
            {
                ContentLength = Length, ETag = SourceChange == "head-after-copy" && Copy is not null ? "changed" : "source-etag", VersionId = VersionId
            });
        }

        private void Commit(string? noneMatch, string? match)
        {
            if (ConcurrentDestination && (noneMatch == "*" || match is not null))
                throw new AmazonS3Exception("destination changed") { StatusCode = HttpStatusCode.PreconditionFailed };
            DestinationOverwritten = ConcurrentDestination;
        }

        public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken = default)
        {
            Copy = request;
            if (SourceChange == "precondition")
                throw new AmazonS3Exception("source changed") { StatusCode = HttpStatusCode.PreconditionFailed };
            if (CopyRequiresMultipart)
                throw new AmazonS3Exception("copy source is larger than maximum allowable size for a copy source")
                    { ErrorCode = "EntityTooLarge", StatusCode = HttpStatusCode.BadRequest };
            Commit(request.IfNoneMatch, request.IfMatch);
            return Task.FromResult(new CopyObjectResponse());
        }

        public override Task<GetObjectTaggingResponse> GetObjectTaggingAsync(GetObjectTaggingRequest request, CancellationToken cancellationToken = default)
        {
            Tags = request;
            return Task.FromResult(new GetObjectTaggingResponse
                { Tagging = [new Tag { Key = "tag", Value = "tag-value" }] });
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            Get = request;
            if (SourceChange == "precondition")
                throw new AmazonS3Exception("source changed") { StatusCode = HttpStatusCode.PreconditionFailed };
            if (StopOnRelayGet)
                throw new AmazonS3Exception("intentional bounded relay probe") { StatusCode = HttpStatusCode.Forbidden };
            var length = request.ByteRange is null ? Length : request.ByteRange.End - request.ByteRange.Start + 1;
            var response = new GetObjectResponse
            {
                HttpStatusCode = request.ByteRange is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent,
                ContentRange = request.ByteRange is null ? null : $"bytes {request.ByteRange.Start}-{request.ByteRange.End}/{Length}",
                ContentLength = SourceChange == "length" ? 1 : length,
                MissingMeta = SourceChange == "metadata" ? 1 : 0,
                ETag = SourceChange == "etag" ? "different" : "source-etag",
                VersionId = SourceChange == "version" ? "different" : VersionId,
                ResponseStream = new MemoryStream(SourceChange == "body" ? new byte[] { 1 } : new byte[checked((int)length)])
            };
            response.Headers.ContentType = "application/test";
            response.Headers.CacheControl = "max-age=123";
            response.Headers.ContentEncoding = "gzip";
            response.Headers.ContentDisposition = "attachment";
            response.Headers.ContentLanguage = "en";
            if (HasExpires) response.ExpiresString = Expires.ToString("r", System.Globalization.CultureInfo.InvariantCulture);
            response.Metadata["custom"] = "verified-value";
            return Task.FromResult(response);
        }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            Put = request;
            PutCalls++;
            PutLength = request.InputStream.Length;
            Commit(request.IfNoneMatch, request.IfMatch);
            return Task.FromResult(new PutObjectResponse { ETag = "empty-copied" });
        }

        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Initiates++;
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload" });
        }

        public override Task<CopyPartResponse> CopyPartAsync(CopyPartRequest request, CancellationToken cancellationToken = default)
        {
            Parts.Add(request);
            return Task.FromResult(new CopyPartResponse { ETag = "part" });
        }

        public override Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref UploadParts);
            return Task.FromResult(new UploadPartResponse { ETag = "uploaded-part", PartNumber = request.PartNumber });
        }

        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Complete = request;
            Commit(request.IfNoneMatch, request.IfMatch);
            return Task.FromResult(new CompleteMultipartUploadResponse { ETag = "completed" });
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            Deletes++;
            Delete = request;
            return Task.FromResult(new DeleteObjectResponse());
        }

        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Aborted = true;
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
    }
}
