using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Model.Internal.MarshallTransformations;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MetadataRewriteTests
{
    private static ObjectProperties Snapshot(string? version = null) => S3StorageService.CreateObjectProperties("bucket", "key", Head(version));
    private static GetObjectMetadataResponse Head(string? version = null)
    {
        var response = new GetObjectMetadataResponse
        {
            ContentLength = 42, ETag = "\"etag\"", VersionId = version,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            StorageClass = S3StorageClass.Standard,
            WebsiteRedirectLocation = "/unchanged"
        };
        response.Headers.ContentType = "text/plain";
        response.Headers.ContentLanguage = "zh-CN";
        response.Metadata.Add("existing", " value ");
        return response;
    }
    private static Task Save(Client client, S3ServiceType provider = S3ServiceType.AmazonS3, ObjectProperties? expected = null) =>
        S3StorageService.ReplaceObjectMetadataAsync(client, ConnectionProfile.CreatePreset(provider), expected ?? Snapshot(),
            new ObjectWriteHeaders(ContentType: "application/json", Metadata: new Dictionary<string, string> { ["new"] = " value " }),
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(S3ServiceType.AmazonS3, null)]
    [InlineData(S3ServiceType.AmazonS3, "v1")]
    [InlineData(S3ServiceType.MinIO, null)]
    [InlineData(S3ServiceType.CloudflareR2, null)]
    public async Task OrdinaryAndVersionedCurrentObjectsUseLatestSourceAndProviderConditions(S3ServiceType provider, string? version)
    {
        using var client = new Client { Version = version };
        await Save(client, provider, Snapshot(version));
        var copy = Assert.IsType<CopyObjectRequest>(client.Copy);
        Assert.All(client.Heads, head => Assert.Null(head.VersionId));
        Assert.Equal(2, client.Heads.Count);
        Assert.Null(copy.SourceVersionId);
        Assert.Equal(copy.SourceKey, copy.DestinationKey);
        var wire = CopyObjectRequestMarshaller.Instance.Marshall(copy);
        Assert.Equal("\"etag\"", wire.Headers["x-amz-copy-source-if-match"]);
        Assert.Equal("REPLACE", wire.Headers["x-amz-metadata-directive"]);
        Assert.Equal("zh-CN", wire.Headers["Content-Language"]);
        Assert.Equal("/unchanged", wire.Headers["x-amz-website-redirect-location"]);
        Assert.Equal(" value ", copy.Metadata["new"]);
        if (provider == S3ServiceType.AmazonS3) Assert.Equal("\"etag\"", wire.Headers["If-Match"]);
        else Assert.Null(copy.IfMatch);
        if (provider == S3ServiceType.CloudflareR2)
        {
            Assert.Equal("\"etag\"", wire.Headers["cf-copy-destination-if-match"]);
            Assert.DoesNotContain("x-amz-tagging-directive", wire.Headers.Keys);
        }
        else Assert.Equal("COPY", wire.Headers["x-amz-tagging-directive"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MetadataOnlyConflictWithSameEtagStopsBeforeCopy(int changedHead)
    {
        using var client = new Client { ChangedHead = changedHead };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task HistoricalVersionCannotReplaceLatest()
    {
        using var client = new Client { Version = "v2" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(client, expected: Snapshot("v1")));
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task ProtectedObjectsFailBeforeHead()
    {
        using var client = new Client();
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client, expected: Snapshot() with { ServerSideEncryptionMethod = "aws:kms" }));
        Assert.Empty(client.Heads);
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task DestinationConflictDoesNotRetryWithoutConditions()
    {
        using var client = new Client { Conflict = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Save(client));
        Assert.Contains("未确认", error.Message);
        Assert.Equal(1, client.CopyCalls);
    }

    [Theory]
    [InlineData(S3ServiceType.AmazonS3)]
    [InlineData(S3ServiceType.Custom)]
    public async Task AclEnabledBucketsPreserveExplicitGrants(S3ServiceType provider)
    {
        using var client = new Client { AclEnabled = true };
        await Save(client, provider);
        var grant = Assert.Single(client.Copy!.Grants);
        Assert.Equal("owner-id", grant.Grantee.CanonicalUser);
        Assert.Equal(S3Permission.FULL_CONTROL, grant.Permission);
    }

    [Fact]
    public async Task MissingAclStopsInsteadOfWritingPrivateDefault()
    {
        using var client = new Client { AclEnabled = true, EmptyAcl = true };
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task UnknownLockStateStopsBeforeCopy()
    {
        using var client = new Client { LockUnknown = true };
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Save(client));
        Assert.Null(client.Copy);
    }

    private sealed class Client() : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public string? Version { get; init; }
        public int ChangedHead { get; init; }
        public bool Conflict { get; init; }
        public bool AclEnabled { get; init; }
        public bool EmptyAcl { get; init; }
        public bool LockUnknown { get; init; }
        public List<GetObjectMetadataRequest> Heads { get; } = [];
        public CopyObjectRequest? Copy { get; private set; }
        public int CopyCalls { get; private set; }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Heads.Add(request);
            var response = Head(Version);
            if (Heads.Count == ChangedHead) response.Headers.CacheControl = "concurrently-edited";
            return Task.FromResult(response);
        }
        public override Task<GetBucketOwnershipControlsResponse> GetBucketOwnershipControlsAsync(GetBucketOwnershipControlsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetBucketOwnershipControlsResponse { OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = AclEnabled ? ObjectOwnership.ObjectWriter : ObjectOwnership.BucketOwnerEnforced }] } });
        public override Task<GetObjectLockConfigurationResponse> GetObjectLockConfigurationAsync(GetObjectLockConfigurationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<GetObjectLockConfigurationResponse>(new AmazonS3Exception("No lock") { StatusCode = LockUnknown ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, ErrorCode = "ObjectLockConfigurationNotFoundError" });
        public override Task<GetObjectAclResponse> GetObjectAclAsync(GetObjectAclRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetObjectAclResponse
            {
                Grants = EmptyAcl ? [] : [new S3Grant { Grantee = new S3Grantee { CanonicalUser = "owner-id" }, Permission = S3Permission.FULL_CONTROL }]
            });
        public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken = default)
        {
            Copy = request;
            CopyCalls++;
            return Conflict ? Task.FromException<CopyObjectResponse>(new AmazonS3Exception("Conflict") { StatusCode = HttpStatusCode.PreconditionFailed }) : Task.FromResult(new CopyObjectResponse());
        }
    }
}
