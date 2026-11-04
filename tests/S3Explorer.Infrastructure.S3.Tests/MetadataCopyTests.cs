using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Model.Internal.MarshallTransformations;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MetadataCopyTests
{
    private static ConnectionProfile Profile => ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);
    private static ObjectProperties Source => new("bucket", "/source//key", 42, null, "\"etag\"",
        "text/plain", "STANDARD_IA", "immutable-v1", new Dictionary<string, string>())
    {
        ServerSideEncryptionMethod = "AES256"
    };

    private static Task Save(Client client, ObjectProperties? source = null, string destination = "/copy//key") =>
        S3StorageService.SaveObjectMetadataCopyAsync(client, Profile, source ?? Source, destination,
            new ObjectWriteHeaders("application/json", "max-age=10", "gzip", "attachment",
                DateTimeOffset.Parse("2030-01-01T00:00:00Z"), new Dictionary<string, string> { ["edited"] = "yes" }),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task SavesOnlyNewKeyWithPinnedVersionAndPreservesHiddenAttributes()
    {
        using var client = new Client();
        await Save(client);
        Assert.Equal("immutable-v1", client.Head!.VersionId);
        var copy = Assert.IsType<CopyObjectRequest>(client.Copy);
        Assert.Equal("/source//key", copy.SourceKey);
        Assert.Equal("/copy//key", copy.DestinationKey);
        Assert.Equal("bucket", copy.SourceBucket);
        Assert.Equal(copy.SourceBucket, copy.DestinationBucket);
        Assert.Equal("immutable-v1", copy.SourceVersionId);
        Assert.Equal("\"etag\"", copy.ETagToMatch);
        Assert.Equal("*", copy.IfNoneMatch);
        Assert.Null(copy.IfMatch);
        Assert.Equal(TaggingDirective.COPY, copy.TaggingDirective);
        Assert.Equal(S3MetadataDirective.REPLACE, copy.MetadataDirective);
        Assert.Equal("AES256", copy.ServerSideEncryptionMethod.Value);
        Assert.Equal("STANDARD_IA", copy.StorageClass.Value);
        Assert.Equal("/unchanged-redirect", copy.WebsiteRedirectLocation);
        Assert.Equal("zh-CN", copy.Headers.ContentLanguage);
        Assert.Equal("application/json", copy.Headers.ContentType);
        Assert.Equal("max-age=10", copy.Headers.CacheControl);
        Assert.Equal("gzip", copy.Headers.ContentEncoding);
        Assert.Equal("attachment", copy.Headers.ContentDisposition);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), copy.Headers.Expires);
        Assert.Equal("yes", copy.Metadata["edited"]);
        Assert.Equal(S3CannedACL.BucketOwnerFullControl, copy.CannedACL);
        Assert.Equal(1, client.CopyCalls);
        Assert.Equal(0, client.DeleteCalls);

        var wire = CopyObjectRequestMarshaller.Instance.Marshall(copy);
        Assert.Equal("*", wire.Headers["If-None-Match"]);
        Assert.Equal("\"etag\"", wire.Headers["x-amz-copy-source-if-match"]);
        Assert.Contains("versionId=immutable-v1", wire.Headers["x-amz-copy-source"]);
        Assert.Equal("COPY", wire.Headers["x-amz-tagging-directive"]);
        Assert.Equal("REPLACE", wire.Headers["x-amz-metadata-directive"]);
        Assert.Equal("AES256", wire.Headers["x-amz-server-side-encryption"]);
        Assert.Equal("zh-CN", wire.Headers["Content-Language"]);
        Assert.Equal("/unchanged-redirect", wire.Headers["x-amz-website-redirect-location"]);
    }

    [Fact]
    public async Task SameKeyIsRefusedBeforeAnyNetwork()
    {
        using var client = new Client();
        await Assert.ThrowsAsync<ArgumentException>(() => Save(client, destination: Source.Key));
        Assert.Null(client.Head);
        Assert.Null(client.Copy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("")]
    public async Task MutableOrMissingVersionIsRefusedBeforeAnyNetwork(string? version)
    {
        using var client = new Client();
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client, Source with { VersionId = version }));
        Assert.Null(client.Head);
    }

    [Fact]
    public async Task SameEtagWithDifferentVersionIsNotAcceptedAsSnapshotIdentity()
    {
        using var client = new Client { ReturnedVersion = "different-v2" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(client));
        Assert.Null(client.Copy);
        Assert.Equal(0, client.DeleteCalls);
    }

    [Theory]
    [InlineData("BucketOwnerPreferred")]
    [InlineData("ObjectWriter")]
    [InlineData("unknown")]
    [InlineData(null)]
    public async Task AclEnabledOrUnknownBucketIsRefused(string? ownership)
    {
        using var client = new Client { Ownership = ownership };
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket")]
    [InlineData(HttpStatusCode.NotFound, "UnknownError")]
    [InlineData(HttpStatusCode.BadRequest, "ObjectLockConfigurationNotFoundError")]
    public async Task MissingLockPermissionsOrUnknownErrorsAreNotTreatedAsUnlocked(HttpStatusCode status, string code)
    {
        using var client = new Client { LockStatus = status, LockErrorCode = code };
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task EnabledOrUnknownLockConfigurationIsRefused()
    {
        using var client = new Client { HasLockConfiguration = true };
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Theory]
    [InlineData("aws:kms")]
    [InlineData("aws:kms:dsse")]
    [InlineData("unknown")]
    [InlineData(null)]
    public async Task ServerSnapshotEncryptionIsRevalidated(string? encryption)
    {
        using var client = new Client { ReturnedEncryption = encryption };
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task IncompleteMetadataFromServerIsRefused()
    {
        using var client = new Client { MissingMetadata = 1 };
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Fact]
    public async Task UnparseableExpiresIsNotSilentlyDiscarded()
    {
        using var client = new Client { Expires = "not-a-date" };
        await Assert.ThrowsAsync<NotSupportedException>(() => Save(client));
        Assert.Null(client.Copy);
    }

    [Fact]
    public void HeadExpiresStringIsParsedWithoutDependingOnUnusedSdkHeadersProperty()
    {
        var response = new GetObjectMetadataResponse { ExpiresString = "Tue, 01 Jan 2030 00:00:00 GMT" };
        Assert.Null(response.Headers.Expires);
        var properties = S3StorageService.CreateObjectProperties("bucket", "key", response);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), properties.ExpiresUtc);
        Assert.Equal(response.ExpiresString, properties.ExpiresHeader);
    }

    [Fact]
    public async Task ReadAndCopyPreserveUneditedMetadataAndHeaderValues()
    {
        using var client = new Client();
        var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest(),
            TestContext.Current.CancellationToken);
        response.Metadata.Add("padded", "  keep this value  ");
        response.Metadata.Add("blank", " ");
        response.Headers.CacheControl = " max-age=10 ";
        response.Headers.ContentEncoding = " gzip ";
        response.Headers.ContentDisposition = " inline ";
        var properties = S3StorageService.CreateObjectProperties(Source.Bucket, Source.Key, response);
        Assert.Equal("  keep this value  ", properties.Metadata["padded"]);
        Assert.Equal(" ", properties.Metadata["blank"]);
        Assert.Equal(response.Headers.CacheControl, properties.CacheControl);
        Assert.Equal(response.Headers.ContentEncoding, properties.ContentEncoding);
        Assert.Equal(response.Headers.ContentDisposition, properties.ContentDisposition);

        var headers = new ObjectWriteHeaders("text/plain", properties.CacheControl,
            properties.ContentEncoding, properties.ContentDisposition, Metadata: properties.Metadata);
        await S3StorageService.SaveObjectMetadataCopyAsync(client, Profile, properties, "copy", headers,
            TestContext.Current.CancellationToken);
        var wire = CopyObjectRequestMarshaller.Instance.Marshall(client.Copy!);
        Assert.Equal("  keep this value  ", wire.Headers["x-amz-meta-padded"]);
        Assert.Equal(" ", wire.Headers["x-amz-meta-blank"]);
        Assert.Equal(headers.CacheControl, wire.Headers["Cache-Control"]);
        Assert.Equal(headers.ContentEncoding, wire.Headers["Content-Encoding"]);
        Assert.Equal(headers.ContentDisposition, wire.Headers["Content-Disposition"]);
    }

    [Fact]
    public async Task AbsentExpiresDoesNotInjectSdkYearOneHeader()
    {
        using var client = new Client();
        await S3StorageService.SaveObjectMetadataCopyAsync(client, Profile, Source, "copy",
            new ObjectWriteHeaders(ContentType: "text/plain"), TestContext.Current.CancellationToken);
        var wire = CopyObjectRequestMarshaller.Instance.Marshall(client.Copy!);
        Assert.DoesNotContain(wire.Headers.Keys, key => string.Equals(key, "Expires", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task DestinationRaceNeverFallsBackOrDeletesSource(HttpStatusCode status)
    {
        using var client = new Client { CopyErrorStatus = status };
        var error = await Assert.ThrowsAsync<ObjectMetadataCopyException>(() => Save(client));
        Assert.Equal(ObjectMetadataCopyFailure.ConditionNotMet, error.Failure);
        Assert.Equal(status, Assert.IsType<AmazonS3Exception>(error.InnerException).StatusCode);
        Assert.Contains("检查目标", error.Message);
        Assert.Equal(1, client.CopyCalls);
        Assert.Equal(0, client.DeleteCalls);
        Assert.Equal("*", client.Copy!.IfNoneMatch);
    }

    [Fact]
    public async Task UnknownCopyOutcomeNeverDeletesOrTriesAnUnguardedWrite()
    {
        using var client = new Client { TimeoutAfterCopy = true };
        var error = await Assert.ThrowsAsync<ObjectMetadataCopyException>(() => Save(client));
        Assert.Equal(ObjectMetadataCopyFailure.OutcomeUnknown, error.Failure);
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Contains("可能已经创建", error.Message);
        Assert.Equal(1, client.CopyCalls);
        Assert.Equal(0, client.DeleteCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ServerFailureAfterDispatchIsReportedAsUncertain(HttpStatusCode status)
    {
        using var client = new Client { CopyErrorStatus = status };
        var error = await Assert.ThrowsAsync<ObjectMetadataCopyException>(() => Save(client));
        Assert.Equal(ObjectMetadataCopyFailure.OutcomeUnknown, error.Failure);
        Assert.Equal(1, client.CopyCalls);
        Assert.Equal(0, client.DeleteCalls);
    }

    [Fact]
    public async Task PermissionFailureIsNotMisreportedAsDestinationConflict()
    {
        using var client = new Client { CopyErrorStatus = HttpStatusCode.Forbidden };
        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => Save(client));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(1, client.CopyCalls);
    }

    [Fact]
    public async Task CancellationAfterDispatchDoesNotClaimTheCopyWasRolledBack()
    {
        using var client = new Client { CancelAfterCopy = true };
        var error = await Assert.ThrowsAsync<ObjectMetadataCopyException>(() => Save(client));
        Assert.Equal(ObjectMetadataCopyFailure.OutcomeUnknown, error.Failure);
        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Equal(1, client.CopyCalls);
        Assert.Equal(0, client.DeleteCalls);
    }

    [Fact]
    public async Task CancellationBeforeDispatchMakesNoRequests()
    {
        using var client = new Client();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            S3StorageService.SaveObjectMetadataCopyAsync(client, Profile, Source, "copy",
                new ObjectWriteHeaders(), cancellation.Token));
        Assert.Null(client.Head);
        Assert.Equal(0, client.CopyCalls);
    }

    [Fact]
    public async Task UnsupportedProviderRejectedBeforeClientCreation()
    {
        var service = new S3StorageService(new S3ClientFactory());
        foreach (var provider in Enum.GetValues<S3ServiceType>().Where(value => value != S3ServiceType.AmazonS3))
            await Assert.ThrowsAsync<NotSupportedException>(() => service.SaveObjectMetadataCopyAsync(
                ConnectionProfile.CreatePreset(provider) with { Endpoint = "not-an-endpoint" }, Source, "copy",
                new ObjectWriteHeaders(), TestContext.Current.CancellationToken));
    }

    private sealed class Client() : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public string? ReturnedVersion { get; init; } = "immutable-v1";
        public string? ReturnedEncryption { get; init; } = "AES256";
        public string? Ownership { get; init; } = "BucketOwnerEnforced";
        public int MissingMetadata { get; init; }
        public HttpStatusCode LockStatus { get; init; } = HttpStatusCode.NotFound;
        public string LockErrorCode { get; init; } = "ObjectLockConfigurationNotFoundError";
        public bool HasLockConfiguration { get; init; }
        public HttpStatusCode? CopyErrorStatus { get; init; }
        public bool TimeoutAfterCopy { get; init; }
        public bool CancelAfterCopy { get; init; }
        public string? Expires { get; init; }
        public GetObjectMetadataRequest? Head { get; private set; }
        public CopyObjectRequest? Copy { get; private set; }
        public int CopyCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request,
            CancellationToken cancellationToken = default)
        {
            Head = request;
            var response = new GetObjectMetadataResponse
            {
                VersionId = ReturnedVersion, ETag = Source.ETag, ContentLength = Source.Size,
                ServerSideEncryptionMethod = ReturnedEncryption is null ? null : new ServerSideEncryptionMethod(ReturnedEncryption),
                StorageClass = S3StorageClass.StandardInfrequentAccess,
                WebsiteRedirectLocation = "/unchanged-redirect", MissingMeta = MissingMetadata,
                ExpiresString = Expires
            };
            response.Headers.ContentLanguage = "zh-CN";
            return Task.FromResult(response);
        }

        public override Task<GetBucketOwnershipControlsResponse> GetBucketOwnershipControlsAsync(
            GetBucketOwnershipControlsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetBucketOwnershipControlsResponse
            {
                OwnershipControls = new OwnershipControls
                {
                    Rules = Ownership is null ? null : [new OwnershipControlsRule { ObjectOwnership = new ObjectOwnership(Ownership) }]
                }
            });

        public override Task<GetObjectLockConfigurationResponse> GetObjectLockConfigurationAsync(
            GetObjectLockConfigurationRequest request, CancellationToken cancellationToken = default) =>
            HasLockConfiguration
                ? Task.FromResult(new GetObjectLockConfigurationResponse { ObjectLockConfiguration = new ObjectLockConfiguration() })
                : Task.FromException<GetObjectLockConfigurationResponse>(new AmazonS3Exception("lock check")
                { StatusCode = LockStatus, ErrorCode = LockErrorCode });

        public override Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            Copy = request;
            CopyCalls++;
            if (TimeoutAfterCopy) throw new TimeoutException("outcome unknown");
            if (CancelAfterCopy) throw new OperationCanceledException("cancelled after dispatch");
            if (CopyErrorStatus is { } status) throw new AmazonS3Exception("copy race") { StatusCode = status };
            return Task.FromResult(new CopyObjectResponse { VersionId = "copy-v1" });
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            throw new InvalidOperationException("The metadata copy workflow must never delete any object.");
        }
    }
}
