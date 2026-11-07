using COSXML.Model;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class CosMetadataRewriteTests
{
    private const string Owner = "<Owner><ID>qcs::cam::uin/123:uin/123</ID></Owner>";
    private static string Acl(string grants) => "<AccessControlPolicy>" + Owner + "<AccessControlList>" + grants + "</AccessControlList></AccessControlPolicy>";
    private static string Grant(string target, string permission) => "<Grant><Grantee>" + target + "</Grantee><Permission>" + permission + "</Permission></Grant>";
    private static ConnectionProfile Profile => new() { ServiceType = S3ServiceType.TencentCos, Endpoint = "https://cos.ap-guangzhou.myqcloud.com", Region = "ap-guangzhou" };
    private static ObjectProperties Source => new("bucket-123", "a /雪.txt", 12, DateTimeOffset.Parse("2026-11-07T01:02:03Z"), "\"e\"", "text/plain", "STANDARD_IA", null, new Dictionary<string, string> { ["name"] = " original " })
        { ContentLanguage = "zh-CN", ServerSideEncryptionMethod = "AES256" };
    private static Dictionary<string, string> Head => new(StringComparer.OrdinalIgnoreCase)
    { ["last-modified"] = "Sat, 07 Nov 2026 01:02:03 GMT", ["content-type"] = "text/plain", ["etag"] = "\"e\"", ["content-length"] = "12", ["x-cos-storage-class"] = "STANDARD_IA", ["x-cos-meta-name"] = " original ", ["content-language"] = "zh-CN", ["x-cos-server-side-encryption"] = "AES256" };

    [Fact] public void DefaultAclIsNotPrivate()
    {
        Assert.Equal("default", S3StorageService.ParseCosAcl(Acl(""))["x-cos-acl"]);
        var acl = S3StorageService.ParseCosAcl(Acl(Grant("<ID>qcs::cam::uin/123:uin/123</ID>", "FULL_CONTROL")));
        Assert.False(acl.ContainsKey("x-cos-acl"));
        Assert.Equal("id=\"123\"", acl["x-cos-grant-full-control"]);
    }

    [Fact] public void ExplicitGrantsKeepSubaccountsAndPublicRead()
    {
        var acl = S3StorageService.ParseCosAcl(Acl(Grant("<ID>qcs::cam::uin/123:uin/456</ID>", "READ_ACP") +
            Grant("<URI>http://cam.qcloud.com/groups/global/AllUsers</URI>", "READ")));
        Assert.Equal("id=\"456\"", acl["x-cos-grant-read-acp"]);
        Assert.Equal("id=\"anyone\"", acl["x-cos-grant-read"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<AccessControlPolicy><AccessControlList/></AccessControlPolicy>")]
    [InlineData("<AccessControlPolicy><Owner><ID>x</ID></Owner></AccessControlPolicy>")]
    public void IncompleteAclFailsClosed(string xml) => Assert.Throws<NotSupportedException>(() => S3StorageService.ParseCosAcl(xml));

    [Fact] public void UnknownGrantFailsClosed() => Assert.Throws<NotSupportedException>(() => S3StorageService.ParseCosAcl(Acl(Grant("<URI>unknown</URI>", "READ"))));

    [Fact] public void TagsAreEncodedWithoutTrimming()
    {
        Assert.Equal("%20a%20=x%26%3D%20", S3StorageService.ParseCosTags("<Tagging><TagSet><Tag><Key> a </Key><Value>x&amp;= </Value></Tag></TagSet></Tagging>"));
        Assert.Equal("", S3StorageService.ParseCosTags("<Tagging><TagSet/></Tagging>"));
    }

    [Fact] public void CopyPreservesPropertiesAndUsesCurrentSourceCondition()
    {
        var request = S3StorageService.BuildCosMetadataCopy("ap-guangzhou", Source,
            new ObjectWriteHeaders("text/new", "max-age=30", Metadata: Source.Metadata), Head,
            S3StorageService.ParseCosAcl(Acl("")), "tag=value");
        var headers = request.GetRequestHeaders();
        Assert.Equal("Replaced", headers["x-cos-metadata-directive"]);
        Assert.Equal("\"e\"", headers.First(p => p.Key.Equals("x-cos-copy-source-if-match", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("STANDARD_IA", headers["x-cos-storage-class"]);
        Assert.Equal("AES256", headers["x-cos-server-side-encryption"]);
        Assert.Equal("zh-CN", headers["Content-Language"]);
        Assert.Equal(" original ", headers["x-cos-meta-name"]);
        Assert.Equal("Replaced", headers["x-cos-tagging-directive"]);
        Assert.Equal("tag=value", headers["x-cos-tagging"]);
        Assert.DoesNotContain("versionId", headers["x-cos-copy-source"]);
        Assert.Equal(0, request.MaxRetry);
    }

    [Theory]
    [InlineData("x-cos-object-type", "appendable")]
    [InlineData("x-cos-object-lock-mode", "COMPLIANCE")]
    [InlineData("x-cos-server-side-encryption", "cos/kms")]
    [InlineData("x-cos-website-redirect-location", "/other")]
    public void UnsafeNativeAttributesStopCopy(string key, string value)
    {
        var head = Head; head[key] = value;
        Assert.Throws<NotSupportedException>(() => S3StorageService.BuildCosMetadataCopy("ap-guangzhou", Source, ObjectWriteHeaders.Empty, head, new Dictionary<string, string>(), ""));
    }

    [Fact] public void EndpointValidationNeverSilentlySwitchesServers()
    {
        Assert.Equal("ap-guangzhou", S3StorageService.ValidateCosMetadataEndpoint(Profile, Source.Bucket));
        foreach (var profile in new[] { Profile with { Endpoint = "https://custom.example" }, Profile with { CustomHostHeader = "custom.example" }, Profile with { AddressingStyle = AddressingStyle.PathStyle }, Profile with { IgnoreCertificateErrors = true }, Profile with { Region = "ap-beijing" } })
            Assert.Throws<NotSupportedException>(() => S3StorageService.ValidateCosMetadataEndpoint(profile, Source.Bucket));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("acl")]
    [InlineData("tags")]
    [InlineData("head")]
    [InlineData("lock403")]
    [InlineData("copyTimeout")]
    public async Task NativeFlowChecksCurrentStateAndNeverRetries(string failure)
    {
        using var s3 = new FakeS3();
        var expected = S3StorageService.CreateObjectProperties(Source.Bucket, Source.Key, FakeS3.Response());
        var heads = 0; var acls = 0; var tags = 0; var copies = 0;
        Task<CosResult> Run(CosRequest request, CancellationToken token)
        {
            Assert.Equal(0, request.MaxRetry);
            var result = new CosResult { httpCode = 200 };
            switch (request)
            {
                case COSXML.Model.Object.HeadObjectRequest:
                    heads++;
                    var h = Head;
                    if (failure == "head" && heads == 2) h["cache-control"] = "changed";
                    result.responseHeaders = h.ToDictionary(p => p.Key, p => new List<string> { p.Value });
                    break;
                case COSXML.Model.Object.GetObjectACLRequest:
                    acls++;
                    result.RawContentBodyString = Acl(failure == "acl" && acls == 2 ? Grant("<ID>qcs::cam::uin/123:uin/456</ID>", "READ") : "");
                    break;
                case COSXML.Model.Object.GetObjectTaggingRequest:
                    tags++;
                    result.RawContentBodyString = failure == "tags" && tags == 2 ? "<Tagging><TagSet><Tag><Key>x</Key><Value>y</Value></Tag></TagSet></Tagging>" : "<Tagging><TagSet/></Tagging>";
                    break;
                case COSXML.Model.Object.CopyObjectRequest:
                    copies++;
                    if (failure == "copyTimeout") throw new TimeoutException();
                    result.RawContentBodyString = "<CopyObjectResult><ETag>new</ETag></CopyObjectResult>";
                    break;
                default:
                    Assert.Equal("GET", request.Method);
                    Assert.True(request.GetRequestParamters().ContainsKey("object-lock"));
                    throw new COSXML.CosException.CosServerException(failure == "lock403" ? 403 : 404, "lock") { errorCode = "NoSuchObjectLockConfiguration" };
            }
            return Task.FromResult(result);
        }
        var task = S3StorageService.RewriteCosMetadataAsync(s3, Profile, expected,
            new ObjectWriteHeaders("text/new", Metadata: expected.Metadata), Run, TestContext.Current.CancellationToken);
        if (failure == "none") await task;
        else await Assert.ThrowsAnyAsync<Exception>(() => task);
        Assert.Equal(failure is "none" or "copyTimeout" ? 1 : 0, copies);
        if (failure == "none") { Assert.Equal(2, heads); Assert.Equal(2, acls); Assert.Equal(2, tags); Assert.Equal(2, s3.Heads); }
    }


    [Fact] public void OfflineSignerDoesNotChangeGlobalTlsValidation()
    {
#pragma warning disable SYSLIB0014
        var before = System.Net.ServicePointManager.ServerCertificateValidationCallback;
#pragma warning restore SYSLIB0014
        var request = S3StorageService.BuildCosMetadataCopy("ap-guangzhou", Source,
            new ObjectWriteHeaders("text/plain", Metadata: Source.Metadata), Head,
            S3StorageService.ParseCosAcl(Acl("")), "");
        using var message = S3StorageService.CreateSignedCosMetadataRequest("ap-guangzhou", Source.Bucket,
            new Amazon.Runtime.ImmutableCredentials("fake-id", "fake-secret", "fake-token"), request);
#pragma warning disable SYSLIB0014
        Assert.Same(before, System.Net.ServicePointManager.ServerCertificateValidationCallback);
#pragma warning restore SYSLIB0014
        Assert.Equal("https", message.RequestUri!.Scheme);
        Assert.Equal("bucket-123.cos.ap-guangzhou.myqcloud.com", message.RequestUri.Host);
        Assert.Contains("a%20/%E9%9B%AA.txt", message.RequestUri.AbsoluteUri);
        Assert.Contains("q-sign-algorithm=sha1", message.Headers.GetValues("Authorization").Single());
        Assert.Equal("fake-token", message.Headers.GetValues("x-cos-security-token").Single());
        Assert.Equal("text/plain", message.Content!.Headers.ContentType!.MediaType);
    }

    [Fact] public async Task NativeTransportReadsCompleteXmlAboveSdkCaptureLimit()
    {
        var xml = "<AccessControlPolicy>" + Owner + "<AccessControlList/>" + new string(' ', 12000) + "</AccessControlPolicy>";
        using var http = new HttpClient(new FakeHttp(xml));
        var result = await S3StorageService.ExecuteCosMetadataRequestAsync(http, "ap-guangzhou", Source.Bucket,
            new Amazon.Runtime.ImmutableCredentials("fake-id", "fake-secret", null),
            new COSXML.Model.Object.GetObjectACLRequest(Source.Bucket, Source.Key), TestContext.Current.CancellationToken);
        Assert.Equal("default", S3StorageService.ParseCosAcl(result.RawContentBodyString)["x-cos-acl"]);
    }

    [Theory]
    [InlineData("content-type", "application/json")]
    [InlineData("cache-control", "max-age=7")]
    [InlineData("content-encoding", "gzip")]
    [InlineData("content-disposition", "attachment")]
    [InlineData("content-language", "en-US")]
    [InlineData("expires", "Sat, 07 Nov 2026 02:02:03 GMT")]
    [InlineData("last-modified", "Sat, 07 Nov 2026 01:02:04 GMT")]
    [InlineData("x-cos-storage-class", "STANDARD")]
    [InlineData("x-cos-server-side-encryption", null)]
    public void NativeHeaderChangesWithSameEtagRejectStaleSnapshot(string name, string? value)
    {
        var native = Head;
        if (value is null) native.Remove(name); else native[name] = value;
        Assert.Throws<InvalidOperationException>(() => S3StorageService.BuildCosMetadataCopy("ap-guangzhou", Source,
            new ObjectWriteHeaders("text/plain", Metadata: Source.Metadata), native, S3StorageService.ParseCosAcl(Acl("")), ""));
    }

    [Theory]
    [InlineData("literal%2Fkey", "/literal%252Fkey")]
    [InlineData("a?b#c", "/a%3Fb%23c")]
    [InlineData("a b", "/a%20b")]
    [InlineData("a//b", "/a//b")]
    [InlineData("雪/目录", "/%E9%9B%AA/%E7%9B%AE%E5%BD%95")]
    public async Task SourceAndDestinationWirePathsPreserveExactKey(string key, string escaped)
    {
        var source = Source with { Key = key };
        var request = S3StorageService.BuildCosMetadataCopy("ap-guangzhou", source,
            new ObjectWriteHeaders("text/plain", Metadata: source.Metadata), Head, S3StorageService.ParseCosAcl(Acl("")), "");
        using var http = new HttpClient(new WireCheckHttp(key, escaped));
        await S3StorageService.ExecuteCosMetadataRequestAsync(http, "ap-guangzhou", source.Bucket,
            new Amazon.Runtime.ImmutableCredentials("fake-id", "fake-secret", null), request,
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("/leading")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a\\b")]
    public void NormalizingKeysFailBeforeSigning(string key)
    {
        Assert.Throws<NotSupportedException>(() => S3StorageService.CreateSignedCosMetadataRequest("ap-guangzhou", Source.Bucket,
            new Amazon.Runtime.ImmutableCredentials("fake-id", "fake-secret", null),
            new COSXML.Model.Object.HeadObjectRequest(Source.Bucket, key)));
    }

    private sealed class WireCheckHttp(string key, string escaped) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(escaped, request.RequestUri!.PathAndQuery);
            Assert.Equal("/" + key, Uri.UnescapeDataString(request.RequestUri.PathAndQuery));
            var source = request.Headers.GetValues("x-cos-copy-source").Single();
            var sourcePath = source[source.IndexOf('/')..];
            Assert.Equal(escaped, sourcePath);
            Assert.Equal("/" + key, Uri.UnescapeDataString(sourcePath));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("<CopyObjectResult><ETag>new</ETag></CopyObjectResult>") });
        }
    }

    private sealed class FakeHttp(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(xml) });
    }

    private sealed class FakeS3() : Amazon.S3.AmazonS3Client(new Amazon.Runtime.AnonymousAWSCredentials(), new Amazon.S3.AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public int Heads { get; private set; }
        public static Amazon.S3.Model.GetObjectMetadataResponse Response()
        {
            var r = new Amazon.S3.Model.GetObjectMetadataResponse
            {
                ContentLength = 12, ETag = "\"e\"", LastModified = Source.LastModified?.UtcDateTime,
                StorageClass = Amazon.S3.S3StorageClass.StandardInfrequentAccess,
                ServerSideEncryptionMethod = Amazon.S3.ServerSideEncryptionMethod.AES256
            };
            r.Headers.ContentType = "text/plain";
            r.Headers.ContentLanguage = "zh-CN";
            r.Metadata.Add("name", " original ");
            return r;
        }
        public override Task<Amazon.S3.Model.GetObjectMetadataResponse> GetObjectMetadataAsync(Amazon.S3.Model.GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Heads++;
            Assert.Null(request.VersionId);
            return Task.FromResult(Response());
        }
    }
}
