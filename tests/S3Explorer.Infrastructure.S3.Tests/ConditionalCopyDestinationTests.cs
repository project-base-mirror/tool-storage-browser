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

    private static Task Run(Client client, S3ServiceType provider, TransferOperationContext context, bool move = false) =>
        S3StorageService.CopyOrMoveObjectAsync(client,
            ConnectionProfile.CreatePreset(provider) with { EnableMultipartCopy = true },
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
    [InlineData(S3ServiceType.CloudflareR2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    [InlineData(S3ServiceType.Custom)]
    public async Task UnsupportedDestinationConditionsFailBeforeAnyRequest(S3ServiceType provider)
    {
        using var client = new Client();
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(client, provider, Context()));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Equal(0, client.Heads);
        Assert.Equal(0, client.Initiates);
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task MinioEmptyCreateOnlyCopyFailsBeforeMutatingAnything()
    {
        using var client = new Client { Length = 0 };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(client, S3ServiceType.MinIO, Context()));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Equal(0, client.Initiates);
        Assert.Null(client.Copy);
        Assert.Null(client.Complete);
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
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("unverified-version")]
    public async Task SupabaseMultipartCopyFailsWithoutVerifiedVersionContractEvenForOverwrite(string? versionId)
    {
        using var client = new Client { Length = 6L * 1024 * 1024, VersionId = versionId };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.SupabaseStorage, Context(overwrite: true)));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Parts);
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task SupabaseMoveIsRejectedBeforeReadingOrWritingAnything()
    {
        using var client = new Client();
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.SupabaseStorage, Context(overwrite: true), move: true));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Equal(0, client.Heads);
        Assert.Equal(0, client.Deletes);
        Assert.Equal(0, client.Initiates);
        Assert.Null(client.Copy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task R2NullVersionIsNotAnImmutableMultipartSource(string? versionId)
    {
        using var client = new Client { Length = 6L * 1024 * 1024 * 1024, VersionId = versionId };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(client, S3ServiceType.CloudflareR2, Context(overwrite: true)));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Equal(0, client.Initiates);
        Assert.Empty(client.Parts);
        Assert.Null(client.Copy);
    }

    private sealed class Client() : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public long Length { get; init; } = 1024;
        public string? VersionId { get; init; } = "source-version";
        public bool ConcurrentDestination { get; init; }
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
            return Task.FromResult(new GetObjectMetadataResponse
            {
                ContentLength = Length, ETag = "source-etag", VersionId = VersionId
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
            Commit(request.IfNoneMatch, request.IfMatch);
            return Task.FromResult(new CopyObjectResponse());
        }

        public override Task<GetObjectTaggingResponse> GetObjectTaggingAsync(GetObjectTaggingRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetObjectTaggingResponse { Tagging = [] });

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

        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Complete = request;
            Commit(request.IfNoneMatch, request.IfMatch);
            return Task.FromResult(new CompleteMultipartUploadResponse { ETag = "completed" });
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
