using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class CopyConsistencyTests
{
    private static ConnectionProfile Profile => ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
    {
        Name = "isolated", AccessKey = "test", SecretKey = "test", EnableMultipartCopy = true
    };

    [Fact]
    public async Task CopyPinsVersionAndEtag()
    {
        using var client = new CopyClient();
        await S3StorageService.CopyOrMoveObjectAsync(client, Profile, "source", "key", "target", "copy", false, TestContext.Current.CancellationToken);
        Assert.Equal("v1", client.Copy!.SourceVersionId);
        Assert.Equal("\"etag1\"", client.Copy.ETagToMatch);
        Assert.Null(client.Delete);
    }

    [Fact]
    public async Task MoveUsesConditionalDeleteWithoutPermanentlyDeletingVersion()
    {
        using var client = new CopyClient();
        await S3StorageService.CopyOrMoveObjectAsync(client, Profile, "source", "key", "target", "copy", true, TestContext.Current.CancellationToken);
        Assert.Equal("\"etag1\"", client.Delete!.IfMatch);
        Assert.Null(client.Delete.VersionId);
    }

    [Fact]
    public async Task MoveConflictKeepsSourceAndDoesNotRetryUnconditionalDelete()
    {
        using var client = new CopyClient { ReplaceAfterCopy = true };
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            S3StorageService.CopyOrMoveObjectAsync(client, Profile, "source", "key", "target", "copy", true, TestContext.Current.CancellationToken));
        Assert.Equal(TransferFailureCategory.Conflict, error.Failure.Category);
        Assert.False(error.Failure.Retryable);
        Assert.Equal(1, client.DeleteCalls);
        Assert.True(client.SourceExists);
    }

    [Fact]
    public async Task AmbiguousDeleteTimeoutNeverRetriesWholeMove()
    {
        using var client = new CopyClient { TimeoutAfterDelete = true };
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            S3StorageService.CopyOrMoveObjectAsync(client, Profile, "source", "key", "target", "copy", true, TestContext.Current.CancellationToken));
        Assert.False(error.Failure.Retryable);
        Assert.Equal(1, client.DeleteCalls);
        Assert.False(client.SourceExists);
        Assert.Contains("未确认", error.Message);
    }

    [Fact]
    public async Task MultipartCopyPreservesHeadersTagsAndSourceIdentity()
    {
        using var client = new CopyClient { Large = true };
        await S3StorageService.CopyOrMoveObjectAsync(client, Profile, "source", "key", "target", "copy", false, TestContext.Current.CancellationToken);
        Assert.NotEmpty(client.Parts);
        Assert.All(client.Parts, part =>
        {
            Assert.Equal("v1", part.SourceVersionId);
            Assert.Equal("\"etag1\"", Assert.Single(part.ETagToMatch));
        });
        Assert.Equal("application/test", client.Initiate!.Headers.ContentType);
        Assert.Equal("max-age=123", client.Initiate.Headers.CacheControl);
        Assert.Equal("gzip", client.Initiate.Headers.ContentEncoding);
        Assert.Equal("attachment", client.Initiate.Headers.ContentDisposition);
        Assert.Equal("en", client.Initiate.Headers.ContentLanguage);
        Assert.Equal("value", client.Initiate.Metadata["custom"]);
        Assert.Equal("tag-value", Assert.Single(client.Initiate.TagSet).Value);
        Assert.Equal("v1", client.Tags!.VersionId);
        Assert.True(client.Completed);
    }

    [Fact]
    public async Task MultipartSourceChangeAbortsInsteadOfCompletingMixedContent()
    {
        using var client = new CopyClient { Large = true, FailPart = true };
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            S3StorageService.CopyOrMoveObjectAsync(client, Profile, "source", "key", "target", "copy", false, TestContext.Current.CancellationToken));
        Assert.Equal(TransferFailureCategory.Conflict, error.Failure.Category);
        Assert.True(client.Aborted);
        Assert.False(client.Completed);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, TransferFailureCategory.Authorization, false)]
    [InlineData(HttpStatusCode.BadRequest, TransferFailureCategory.Validation, false)]
    [InlineData(HttpStatusCode.TooManyRequests, TransferFailureCategory.Service, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TransferFailureCategory.Service, true)]
    public void S3ErrorClassificationIsConsistent(HttpStatusCode status, TransferFailureCategory category, bool retry)
    {
        var error = S3StorageService.ToTransferException(new AmazonS3Exception("failure") { StatusCode = status });
        Assert.Equal(category, error.Failure.Category);
        Assert.Equal(retry, error.Failure.Retryable);
    }

    [Fact]
    public async Task SameSourceAndDestinationNeverDelete()
    {
        using var client = new CopyClient();
        await Assert.ThrowsAsync<TransferExecutionException>(() =>
            S3StorageService.CopyOrMoveObjectAsync(client, Profile, "same", "key", "same", "key", true, TestContext.Current.CancellationToken));
        Assert.Null(client.Copy);
        Assert.Null(client.Delete);
    }

    private sealed class CopyClient() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public bool Large { get; init; }
        public bool ReplaceAfterCopy { get; init; }
        public bool FailPart { get; init; }
        public bool TimeoutAfterDelete { get; init; }
        public bool SourceExists { get; private set; } = true;
        public int DeleteCalls { get; private set; }
        public CopyObjectRequest? Copy { get; private set; }
        public DeleteObjectRequest? Delete { get; private set; }
        public InitiateMultipartUploadRequest? Initiate { get; private set; }
        public GetObjectTaggingRequest? Tags { get; private set; }
        public List<CopyPartRequest> Parts { get; } = [];
        public bool Completed { get; private set; }
        public bool Aborted { get; private set; }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            var response = new GetObjectMetadataResponse
            {
                ContentLength = Large ? 5L * 1024 * 1024 * 1024 + 1 : 1024,
                ETag = "\"etag1\"", VersionId = "v1"
            };
            response.Headers.ContentType = "application/test";
            response.Headers.CacheControl = "max-age=123";
            response.Headers.ContentEncoding = "gzip";
            response.Headers.ContentDisposition = "attachment";
            response.Headers.ContentLanguage = "en";
            response.Metadata["custom"] = "value";
            return Task.FromResult(response);
        }
        public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken = default)
        {
            Copy = request;
            return Task.FromResult(new CopyObjectResponse());
        }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            Delete = request;
            DeleteCalls++;
            if (ReplaceAfterCopy)
                throw new AmazonS3Exception("source changed") { StatusCode = HttpStatusCode.PreconditionFailed };
            SourceExists = false;
            if (TimeoutAfterDelete) throw new TaskCanceledException("HTTP response timeout");
            return Task.FromResult(new DeleteObjectResponse());
        }
        public override Task<GetObjectTaggingResponse> GetObjectTaggingAsync(GetObjectTaggingRequest request, CancellationToken cancellationToken = default)
        {
            Tags = request;
            return Task.FromResult(new GetObjectTaggingResponse { Tagging = [new Tag { Key = "tag", Value = "tag-value" }] });
        }
        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Initiate = request;
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload" });
        }
        public override Task<CopyPartResponse> CopyPartAsync(CopyPartRequest request, CancellationToken cancellationToken = default)
        {
            Parts.Add(request);
            if (FailPart) throw new AmazonS3Exception("source changed") { StatusCode = HttpStatusCode.PreconditionFailed };
            return Task.FromResult(new CopyPartResponse { ETag = "part" });
        }
        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.FromResult(new CompleteMultipartUploadResponse());
        }
        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Aborted = true;
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
    }
}
