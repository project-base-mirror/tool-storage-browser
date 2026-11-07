using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Model.Internal.MarshallTransformations;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class CompatibleMetadataRewriteTests
{
    private static readonly ObjectWriteHeaders Edits = new("application/json", "max-age=120", "gzip",
        Metadata: new Dictionary<string, string> { ["padded"] = " value " });
    private static Task Save(Client client, S3ServiceType provider) =>
        S3StorageService.ReplaceObjectMetadataAsync(client, ConnectionProfile.CreatePreset(provider),
            S3StorageService.CreateObjectProperties("bucket", "key", client.Head()), Edits,
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(S3ServiceType.BackblazeB2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public async Task OrdinaryObjectsUseCurrentSelfCopyAndReadBackTheRequestedMetadata(S3ServiceType provider)
    {
        using var client = new Client();
        await Save(client, provider);
        var copy = Assert.IsType<CopyObjectRequest>(client.Copy);
        Assert.Equal(copy.SourceKey, copy.DestinationKey);
        Assert.Null(copy.SourceVersionId);
        Assert.Null(copy.IfMatch);
        Assert.Null(copy.CannedACL);
        Assert.Empty(copy.Grants ?? []);
        var wire = CopyObjectRequestMarshaller.Instance.Marshall(copy);
        Assert.Equal("REPLACE", wire.Headers["x-amz-metadata-directive"]);
        Assert.Equal("\"etag\"", wire.Headers["x-amz-copy-source-if-match"]);
        Assert.DoesNotContain("x-amz-tagging", wire.Headers.Keys);
        Assert.DoesNotContain("x-amz-tagging-directive", wire.Headers.Keys);
        Assert.Equal(3, client.HeadCalls);
        Assert.Equal(provider == S3ServiceType.BackblazeB2 ? 1 : 0, client.LockCalls);
    }

    [Fact]
    public async Task B2PreservesExplicitSseB2Encryption()
    {
        using var client = new Client { Encryption = ServerSideEncryptionMethod.AES256 };
        await Save(client, S3ServiceType.BackblazeB2);
        Assert.Equal(ServerSideEncryptionMethod.AES256, client.Copy!.ServerSideEncryptionMethod);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B2UnknownOrEnabledLockStopsBeforeWriting(bool enabled)
    {
        using var client = new Client { LockEnabled = enabled, LockUnknown = !enabled };
        await Assert.ThrowsAnyAsync<Exception>(() => Save(client, S3ServiceType.BackblazeB2));
        Assert.Null(client.Copy);
    }

    [Theory]
    [InlineData(S3ServiceType.BackblazeB2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public async Task CompatibleServersThatIgnoreEditsCannotReportSuccess(S3ServiceType provider)
    {
        using var client = new Client { IgnoreEdits = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Save(client, provider));
        Assert.Contains("已提交", error.Message);
        Assert.Equal(1, client.CopyCalls);
    }

    [Theory]
    [InlineData(S3ServiceType.BackblazeB2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public async Task LastMomentConflictNeverFallsBackToUnguardedCopy(S3ServiceType provider)
    {
        using var client = new Client { ChangedHead = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(client, provider));
        Assert.Null(client.Copy);
    }

    [Theory]
    [InlineData(S3ServiceType.BackblazeB2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public async Task ReadbackFailureIsClearlyAnUnconfirmedSubmittedWrite(S3ServiceType provider)
    {
        using var client = new Client { RejectReadback = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Save(client, provider));
        Assert.Contains("已提交", error.Message);
        Assert.Contains("不要直接重试", error.Message);
        Assert.Equal(1, client.CopyCalls);
    }

    private sealed class Client() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public ServerSideEncryptionMethod? Encryption { get; init; }
        public bool LockEnabled { get; init; }
        public bool LockUnknown { get; init; }
        public bool IgnoreEdits { get; init; }
        public bool RejectReadback { get; init; }
        public int ChangedHead { get; init; }
        public int HeadCalls { get; private set; }
        public int LockCalls { get; private set; }
        public int CopyCalls { get; private set; }
        public CopyObjectRequest? Copy { get; private set; }
        public GetObjectMetadataResponse Head()
        {
            var result = new GetObjectMetadataResponse { ETag = "\"etag\"", ContentLength = 42, ServerSideEncryptionMethod = Encryption };
            result.Headers.ContentType = Copy is not null && !IgnoreEdits ? Edits.ContentType : "text/plain";
            if (Copy is not null && !IgnoreEdits)
            {
                result.Headers.CacheControl = Edits.CacheControl;
                result.Headers.ContentEncoding = Edits.ContentEncoding;
                foreach (var pair in Edits.Metadata!) result.Metadata.Add(pair.Key, pair.Value);
            }
            if (HeadCalls == ChangedHead && ChangedHead != 0) result.Headers.CacheControl = "concurrent";
            return result;
        }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Null(request.VersionId);
            HeadCalls++;
            if (RejectReadback && Copy is not null)
                throw new AmazonS3Exception("Access denied on readback") { StatusCode = HttpStatusCode.Forbidden };
            return Task.FromResult(Head());
        }
        public override Task<GetObjectLockConfigurationResponse> GetObjectLockConfigurationAsync(GetObjectLockConfigurationRequest request, CancellationToken cancellationToken = default)
        {
            LockCalls++;
            return LockEnabled ? Task.FromResult(new GetObjectLockConfigurationResponse()) :
                Task.FromException<GetObjectLockConfigurationResponse>(new AmazonS3Exception("lock")
                { StatusCode = LockUnknown ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, ErrorCode = "ObjectLockConfigurationNotFoundError" });
        }
        public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken = default)
        {
            Copy = request;
            CopyCalls++;
            return Task.FromResult(new CopyObjectResponse());
        }
    }
}
