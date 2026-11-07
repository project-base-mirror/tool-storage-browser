using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Aliyun.OSS;
using Amazon.Runtime;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class OssNativeTransportTests
{
    [Theory]
    [InlineData("GET", null, "versioning", false)]
    [InlineData("GET", null, "worm", true)]
    [InlineData("GET", "a space/中文%key", "acl", true)]
    [InlineData("HEAD", "a//b", "", false)]
    [InlineData("PUT", "a/中文 #?%+", "", true)]
    public void V4AuthorizationMatchesPinnedOfficialSignerWithoutConstructingItsTransport(string method, string? key, string query, bool session)
    {
        var time = DateTimeOffset.Parse("2026-11-07T12:34:56Z", CultureInfo.InvariantCulture);
        var credentials = new ImmutableCredentials("test-access", "test-secret", session ? "test-session-token" : null);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = time.UtcDateTime.ToString("R", CultureInfo.InvariantCulture),
            ["host"] = "bucket.oss-cn-hangzhou.aliyuncs.com",
            ["content-type"] = "text/plain",
            ["cache-control"] = " max-age=10 ",
            ["x-oss-meta-item"] = " unchanged ",
            ["x-oss-copy-source"] = "/bucket/a%20key",
            ["x-oss-copy-source-if-match"] = "\"etag\""
        };
        var officialHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        OssV4MetadataSigner.Sign(method, "bucket", key, query, "cn-hangzhou", credentials, headers, time);

        // Reflection is test-only: exercise the dependency's authoritative signer,
        // never OssClient or either of its HTTP transport implementations.
        var assembly = typeof(OssClient).Assembly;
        var requestType = assembly.GetType("Aliyun.OSS.Common.Communication.ServiceRequest", true)!;
        var signerType = assembly.GetType("Aliyun.OSS.Util.OssRequestSignerV4", true)!;
        var request = Activator.CreateInstance(requestType, nonPublic: true)!;
        requestType.GetProperty("Method")!.SetValue(request, Enum.Parse(assembly.GetType("Aliyun.OSS.Common.Communication.HttpMethod", true)!, method, true));
        var wireHeaders = (IDictionary<string, string>)requestType.GetProperty("Headers")!.GetValue(request)!;
        foreach (var pair in officialHeaders) wireHeaders.Add(pair.Key, pair.Value);
        if (query.Length > 0)
            ((IDictionary<string, string>)requestType.GetProperty("Parameters")!.GetValue(request)!).Add(query, "");
        var signer = Activator.CreateInstance(signerType, nonPublic: true)!;
        foreach (var pair in new Dictionary<string, object?>
        {
            ["Bucket"] = "bucket", ["Key"] = key, ["Region"] = "cn-hangzhou", ["Product"] = "oss",
            ["AdditionalHeaders"] = new List<string> { "host", "date", "cache-control" }
        }) signerType.GetProperty(pair.Key)!.SetValue(signer, pair.Value);
        var credentialType = assembly.GetType("Aliyun.OSS.Common.Authentication.DefaultCredentials", true)!;
        var officialCredentials = Activator.CreateInstance(credentialType, [credentials.AccessKey, credentials.SecretKey, credentials.Token])!;
        signerType.GetMethod("Sign")!.Invoke(signer, [request, officialCredentials]);
        Assert.Equal(wireHeaders["Authorization"], headers["Authorization"]);
        Assert.Equal(wireHeaders["x-oss-date"], headers["x-oss-date"]);
    }

    [Fact]
    public void NativeWorkflowReadsExactAclAndSignsOneCurrentSourceCopy()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OssNativeMetadataTransport(http, "https://oss-cn-hangzhou.aliyuncs.com", "cn-hangzhou",
            new ImmutableCredentials("test-access", "test-secret", null), TestContext.Current.CancellationToken);
        var expected = new ObjectProperties("bucket", "folder/a b", 42, null, "\"etag\"", "text/plain", "STANDARD", null,
            new Dictionary<string, string> { ["existing"] = "value" }) { ProviderObjectType = "Normal" };
        S3StorageService.RewriteOssMetadata(client, expected, new ObjectWriteHeaders("application/json", "max-age=20", Metadata:
            new Dictionary<string, string> { ["new"] = "value" }), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.CopyCalls);
        Assert.Equal("default", handler.CopyHeaders!["x-oss-object-acl"]);
        Assert.Equal("Standard", handler.CopyHeaders["x-oss-storage-class"]);
        Assert.Equal("COPY", handler.CopyHeaders["x-oss-tagging-directive"]);
        Assert.Equal("/bucket/folder/a%20b", handler.CopyHeaders["x-oss-copy-source"]);
        Assert.Equal("\"etag\"", handler.CopyHeaders["x-oss-copy-source-if-match"]);
        Assert.StartsWith("OSS4-HMAC-SHA256 ", handler.CopyHeaders["Authorization"]);
        Assert.DoesNotContain("versionId", handler.CopyUri!);
    }

    [Fact]
    public void UnknownLockResponseDoesNotReachPut()
    {
        using var handler = new Handler { LockStatus = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler);
        var client = new OssNativeMetadataTransport(http, "https://oss-cn-hangzhou.aliyuncs.com", "cn-hangzhou",
            new ImmutableCredentials("test-access", "test-secret", null), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => client.RequireNoBucketWorm("bucket"));
        Assert.Equal(0, handler.CopyCalls);
    }

    [Theory]
    [InlineData("bad@evil.example")]
    [InlineData("bucket/path")]
    [InlineData("bucket?query")]
    [InlineData("bucket#fragment")]
    [InlineData("bucket.example")]
    [InlineData("UPPERCASE")]
    [InlineData("-bucket")]
    [InlineData("bucket-")]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("中文bucket")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MaliciousOrInvalidBucketNeverDispatchesSignedRequest(string bucket)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OssNativeMetadataTransport(http, "https://oss-cn-hangzhou.aliyuncs.com", "cn-hangzhou",
            new ImmutableCredentials("test-access", "test-secret", "test-session-token"), TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => client.Versioning(bucket));
        Assert.Equal(0, handler.SendCalls);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode LockStatus { get; init; } = HttpStatusCode.NotFound;
        public int CopyCalls { get; private set; }
        public int SendCalls { get; private set; }
        public Dictionary<string, string>? CopyHeaders { get; private set; }
        public string? CopyUri { get; private set; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCalls++;
            if (request.Method == HttpMethod.Head)
            {
                var head = Xml("");
                head.Content.Headers.ContentLength = 42;
                head.Content.Headers.ContentType = new("text/plain");
                head.Headers.ETag = new("\"etag\"");
                head.Headers.Add("x-oss-object-type", "Normal");
                head.Headers.Add("x-oss-storage-class", "Standard");
                head.Headers.Add("x-oss-meta-existing", "value");
                return head;
            }
            if (request.RequestUri!.Query == "?versioning") return Xml("<VersioningConfiguration/>");
            if (request.RequestUri.Query == "?worm") return Xml("<Error><Code>NoSuchWORMConfiguration</Code></Error>", LockStatus);
            if (request.RequestUri.Query == "?acl") return Xml("<AccessControlPolicy><Owner><ID>owner</ID></Owner><AccessControlList><Grant>default</Grant></AccessControlList></AccessControlPolicy>");
            Assert.Equal(HttpMethod.Put, request.Method);
            CopyCalls++;
            CopyUri = request.RequestUri.OriginalString;
            CopyHeaders = request.Headers.Concat(request.Content!.Headers).ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            return Xml("<CopyObjectResult><ETag>etag</ETag><LastModified>2026-11-07T12:34:56.000Z</LastModified></CopyObjectResult>");
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
        private static HttpResponseMessage Xml(string content, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(content, Encoding.UTF8, "application/xml") };
    }
}
