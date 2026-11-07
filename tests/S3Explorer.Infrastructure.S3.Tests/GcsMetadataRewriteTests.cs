using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class GcsMetadataRewriteTests
{
    private static ConnectionProfile Profile => ConnectionProfile.CreatePreset(S3ServiceType.GoogleCloudStorage) with
    { Name = "gcs-test", Endpoint = "https://storage.googleapis.com", AccessKey = "test", SecretKey = "test", AddressingStyle = AddressingStyle.PathStyle };

    private static HttpResponseMessage Head(string generation = "123", string metageneration = "4")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = 42;
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain");
        response.Content.Headers.TryAddWithoutValidation("Content-Language", "zh-CN");
        response.Headers.TryAddWithoutValidation("ETag", "\"etag\"");
        response.Headers.TryAddWithoutValidation("x-goog-generation", generation);
        response.Headers.TryAddWithoutValidation("x-goog-metageneration", metageneration);
        response.Headers.TryAddWithoutValidation("x-goog-storage-class", "NEARLINE");
        response.Headers.TryAddWithoutValidation("x-goog-custom-time", "2026-01-01T00:00:00Z");
        response.Headers.TryAddWithoutValidation("x-goog-meta-preserved", "original");
        return response;
    }
    private static HttpResponseMessage SavedHead()
    {
        var response = Head(metageneration: "5");
        response.Headers.Remove("ETag");
        response.Headers.TryAddWithoutValidation("ETag", "\"updated-opaque-etag\"");
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        response.Headers.Remove("x-goog-meta-preserved");
        response.Headers.TryAddWithoutValidation("x-goog-meta-preserved", "updated");
        return response;
    }

    private static HttpResponseMessage Xml(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/xml") };
    private static HttpResponseMessage PrivateAcl() => Xml("<AccessControlPolicy><Owner><ID>owner</ID></Owner><AccessControlList><Grant><Grantee xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"CanonicalUser\"><ID>owner</ID></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSdkTransportCapturesGoogleHeadersAndSendsSignedAtomicSelfCopy(bool uniform)
    {
        var copyCount = 0;
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head) return copyCount == 0 ? Head() : SavedHead();
            if (request.Method == HttpMethod.Get) return uniform
                ? Xml("<Error><Code>InvalidArgument</Code><Message>Cannot get legacy ACL for an object when uniform bucket-level access is enabled.</Message></Error>", HttpStatusCode.BadRequest)
                : PrivateAcl();
            Assert.Equal(HttpMethod.Put, request.Method);
            copyCount++;
            Assert.Equal("/bucket/key", request.RequestUri!.AbsolutePath);
            Assert.Equal("REPLACE", Header(request, "x-amz-metadata-directive"));
            Assert.EndsWith("bucket/key", Uri.UnescapeDataString(Header(request, "x-amz-copy-source")));
            Assert.DoesNotContain("versionId", Header(request, "x-amz-copy-source"));
            foreach (var prefix in new[] { "x-goog-copy-source-if-", "x-goog-if-" })
            {
                Assert.Equal("123", Header(request, prefix + "generation-match"));
                Assert.Equal("4", Header(request, prefix + "metageneration-match"));
                Assert.Contains(prefix + "generation-match", Header(request, "Authorization"));
                Assert.Contains(prefix + "metageneration-match", Header(request, "Authorization"));
            }
            Assert.Equal("2026-01-01T00:00:00Z", Header(request, "x-goog-custom-time"));
            Assert.Equal("NEARLINE", Header(request, "x-amz-storage-class"));
            Assert.Equal("updated", Header(request, "x-amz-meta-preserved"));
            Assert.False(request.Headers.Contains("x-amz-tagging-directive"));
            Assert.False(request.Headers.Contains("x-amz-grant-full-control"));
            if (uniform) Assert.False(request.Headers.Contains("x-amz-acl"));
            else Assert.Equal("private", Header(request, "x-amz-acl"));
            return Xml("<CopyObjectResult><ETag>\"etag\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></CopyObjectResult>");
        });
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        var service = new S3StorageService(factory);
        var snapshot = await service.GetObjectPropertiesAsync(Profile, "bucket", "key", TestContext.Current.CancellationToken);
        Assert.Equal("123", snapshot.ProviderGeneration);
        Assert.Equal("4", snapshot.ProviderMetageneration);
        Assert.Equal("NEARLINE", snapshot.StorageClass);
        Assert.Equal("original", snapshot.Metadata["preserved"]);
        using var client = CreateWriteClient(factory);
        await S3StorageService.RewriteGcsMetadataAsync(client, Profile, snapshot,
            new ObjectWriteHeaders(ContentType: "application/json", Metadata: new Dictionary<string, string> { ["preserved"] = "updated" }), TestContext.Current.CancellationToken);
        Assert.Equal(1, copyCount);
    }

    [Theory]
    [InlineData("x-goog-encryption-kms-key-name", "projects/p/locations/l/keyRings/r/cryptoKeys/k")]
    [InlineData("x-goog-encryption-algorithm", "AES256")]
    [InlineData("x-goog-encryption-key-sha256", "keyhash")]
    [InlineData("x-goog-object-lock-mode", "COMPLIANCE")]
    [InlineData("x-goog-object-lock-retain-until-date", "malformed-retention")]
    public async Task RawProtectionHeadersCannotDisappearInSdkModel(string name, string value)
    {
        var calls = 0;
        using var transport = new Transport(_ =>
        {
            calls++;
            var response = Head();
            response.Headers.TryAddWithoutValidation(name, value);
            return response;
        });
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        var snapshot = await new S3StorageService(factory).GetObjectPropertiesAsync(Profile, "bucket", "key", TestContext.Current.CancellationToken);
        using var client = CreateWriteClient(factory);
        await Assert.ThrowsAsync<NotSupportedException>(() => S3StorageService.RewriteGcsMetadataAsync(client, Profile, snapshot, new ObjectWriteHeaders(), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("", "4")]
    [InlineData("123", "")]
    [InlineData("0", "4")]
    [InlineData("1.2", "4")]
    public async Task MissingOrInvalidRevisionsBlockBeforeWrites(string generation, string metageneration)
    {
        using var transport = new Transport(_ => Head(generation, metageneration));
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        var snapshot = await new S3StorageService(factory).GetObjectPropertiesAsync(Profile, "bucket", "key", TestContext.Current.CancellationToken);
        using var client = CreateWriteClient(factory);
        await Assert.ThrowsAsync<NotSupportedException>(() => S3StorageService.RewriteGcsMetadataAsync(client, Profile, snapshot, new ObjectWriteHeaders(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "InvalidArgument", "uniform bucket-level access is enabled")]
    [InlineData(HttpStatusCode.BadRequest, "AccessDenied", "uniform bucket-level access is enabled")]
    [InlineData(HttpStatusCode.BadRequest, "InvalidArgument", "invalid argument")]
    [InlineData(HttpStatusCode.BadRequest, "InvalidArgument", "uniform bucket-level access is disabled")]
    public void GenericAclErrorsDoNotProveUniformAccess(HttpStatusCode status, string code, string message) =>
        Assert.False(S3StorageService.IsGcsUniformBucketAccess(new AmazonS3Exception(message) { StatusCode = status, ErrorCode = code }));

    [Theory]
    [InlineData(null, "private")]
    [InlineData("http://acs.amazonaws.com/groups/global/AllUsers", "public-read")]
    [InlineData("http://acs.amazonaws.com/groups/global/AuthenticatedUsers", "authenticated-read")]
    public void ExactCannedAclShapesArePreserved(string? group, string value)
    {
        var acl = Acl();
        if (group is not null) acl.Grants.Add(new S3Grant { Grantee = new S3Grantee { URI = group }, Permission = S3Permission.READ });
        Assert.Equal(value, S3StorageService.GetGcsPreservedCannedAcl(acl).Value);
    }

    [Fact]
    public void CustomGrantsCannotBeSilentlyReducedToCannedAcl()
    {
        var acl = Acl();
        acl.Grants.Add(new S3Grant { Grantee = new S3Grantee { CanonicalUser = "another-user" }, Permission = S3Permission.READ });
        Assert.Throws<NotSupportedException>(() => S3StorageService.GetGcsPreservedCannedAcl(acl));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictDoesNotRetryOrFallBackToUpload(bool serverConflict)
    {
        var heads = 0;
        var copies = 0;
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head) return Head("123", ++heads == 1 || serverConflict ? "4" : "5");
            if (request.Method == HttpMethod.Get) return PrivateAcl();
            copies++;
            return Xml("<Error><Code>PreconditionFailed</Code><Message>generation changed</Message></Error>", HttpStatusCode.PreconditionFailed);
        });
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        var snapshot = await new S3StorageService(factory).GetObjectPropertiesAsync(Profile, "bucket", "key", TestContext.Current.CancellationToken);
        using var client = CreateWriteClient(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => S3StorageService.RewriteGcsMetadataAsync(client, Profile, snapshot, new ObjectWriteHeaders(), TestContext.Current.CancellationToken));
        Assert.Equal(serverConflict ? 1 : 0, copies);
    }

    [Theory]
    [InlineData("ignored-metadata")]
    [InlineData("ignored-content-type")]
    [InlineData("generation")]
    [InlineData("unchanged-metageneration")]
    [InlineData("invalid-metageneration")]
    [InlineData("size")]
    [InlineData("missing-etag")]
    [InlineData("language")]
    [InlineData("storage-class")]
    [InlineData("custom-time")]
    [InlineData("new-encryption")]
    [InlineData("new-retention")]
    [InlineData("read-forbidden")]
    [InlineData("read-cancelled")]
    public async Task CommittedWriteReadbackFailureNeverReportsOrdinaryFailureOrRetries(string failure)
    {
        var copies = 0;
        var readbacks = 0;
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                if (copies == 0) return Head();
                readbacks++;
                if (failure == "read-forbidden") return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (failure == "read-cancelled") throw new OperationCanceledException("cancelled after commit");
                var response = SavedHead();
                var changes = failure switch
                {
                    "ignored-metadata" => ("x-goog-meta-preserved", "original"),
                    "generation" => ("x-goog-generation", "124"),
                    "unchanged-metageneration" => ("x-goog-metageneration", "4"),
                    "invalid-metageneration" => ("x-goog-metageneration", "bad"),
                    "missing-etag" => ("ETag", ""),
                    "storage-class" => ("x-goog-storage-class", "STANDARD"),
                    "custom-time" => ("x-goog-custom-time", "2026-02-01T00:00:00Z"),
                    "new-encryption" => ("x-goog-encryption-kms-key-name", "projects/p/keys/new-key"),
                    "new-retention" => ("x-goog-object-lock-retain-until-date", "2028-01-01T00:00:00Z"),
                    _ => ((string?)null, (string?)null)
                };
                if (changes.Item1 is not null)
                {
                    response.Headers.Remove(changes.Item1);
                    response.Headers.TryAddWithoutValidation(changes.Item1, changes.Item2);
                }
                if (failure == "ignored-content-type")
                {
                    response.Content.Headers.Remove("Content-Type");
                    response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain");
                }
                if (failure == "size") response.Content.Headers.ContentLength = 43;
                if (failure == "language")
                {
                    response.Content.Headers.Remove("Content-Language");
                    response.Content.Headers.TryAddWithoutValidation("Content-Language", "en-US");
                }
                return response;
            }
            if (request.Method == HttpMethod.Get) return PrivateAcl();
            Assert.Equal(HttpMethod.Put, request.Method);
            copies++;
            return Xml("<CopyObjectResult><ETag>\"etag\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></CopyObjectResult>");
        });
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        var snapshot = await new S3StorageService(factory).GetObjectPropertiesAsync(Profile, "bucket", "key", TestContext.Current.CancellationToken);
        using var client = CreateWriteClient(factory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => S3StorageService.RewriteGcsMetadataAsync(client, Profile, snapshot,
            new ObjectWriteHeaders(ContentType: "application/json", Metadata: new Dictionary<string, string> { ["preserved"] = "updated" }), TestContext.Current.CancellationToken));
        Assert.Contains("写请求已提交但结果未确认", error.Message);
        Assert.Contains("不要直接重试", error.Message);
        Assert.Equal(1, copies);
        Assert.Equal(1, readbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicMetadataWriteDisablesRetriesBeforeClientConstruction(bool transportFailure)
    {
        var copies = 0;
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head) return Head();
            if (request.Method == HttpMethod.Get) return PrivateAcl();
            copies++;
            if (transportFailure) throw new OperationCanceledException("Response lost after dispatch");
            return Xml("<Error><Code>InternalError</Code><Message>uncertain</Message></Error>", HttpStatusCode.InternalServerError);
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var snapshot = await service.GetObjectPropertiesAsync(Profile, "bucket", "key", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceObjectMetadataAsync(Profile, snapshot,
            new ObjectWriteHeaders(ContentType: "application/json", Metadata: snapshot.Metadata), TestContext.Current.CancellationToken));
        Assert.Equal(1, copies);
    }

    private static IAmazonS3 CreateWriteClient(S3ClientFactory factory) =>
        factory.CreateResolved(Profile, maxErrorRetry: 0).Client;

    private static GetObjectAclResponse Acl() => new()
    {
        Owner = new Owner { Id = "owner" },
        Grants = [new S3Grant { Grantee = new S3Grantee { CanonicalUser = "owner" }, Permission = S3Permission.FULL_CONTROL }]
    };
    private static string Header(HttpRequestMessage request, string name) => Assert.Single(request.Headers.GetValues(name));
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public override HttpClient CreateHttpClient(IClientConfig config) => _client ??= new HttpClient(new Handler(respond));
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public void Dispose() => _client?.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
