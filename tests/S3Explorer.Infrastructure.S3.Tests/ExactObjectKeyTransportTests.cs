using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3.Model.Internal.MarshallTransformations;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed partial class ExactObjectKeyTransportTests
{
    [Theory]
    [InlineData("https://s3.example.test/bucket", "/bucket//leading//file")]
    [InlineData("https://bucket.s3.example.test/", "//leading//file")]
    [InlineData("https://s3.example.test/api%20path/%7BKey%2B%7D/%E4%B8%AD/bucket", "/api path/{Key+}/中/bucket//leading//file")]
    [InlineData("https://s3.example.test/api%2Fpath/%25/bucket", "/api/path/%/bucket//leading//file")]
    public void PreSigningFixPreservesEndpointSegmentsAndIsIdempotent(string endpoint, string expectedPath)
    {
        var original = new GetObjectMetadataRequest { BucketName = "bucket", Key = "/leading//file" };
        var request = GetObjectMetadataRequestMarshaller.Instance.Marshall(original);
        request.Endpoint = new Uri(endpoint);
        var expectedWirePath = request.Endpoint.AbsolutePath.TrimEnd('/') + "/" + original.Key;
        ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);
        var template = request.ResourcePath;
        ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);
        Assert.Equal(template, request.ResourcePath);
        Assert.Equal(expectedWirePath, AmazonServiceClient.ComposeUrl(request).AbsolutePath);
        Assert.Equal(expectedPath, Uri.UnescapeDataString(AmazonServiceClient.ComposeUrl(request).AbsolutePath));
        Assert.Equal(original.Key, request.PathResources["{Key+}"]);
        Assert.Equal("/leading//file", original.Key);
        Assert.Equal(AmazonServiceClient.ComposeUrl(request).AbsolutePath,
            Amazon.Util.AWSSDKUtils.CanonicalizeResourcePathV2(request.Endpoint, request.ResourcePath, false, request.PathResources));
    }

    [Fact]
    public void RegionRetryRebuildsFromOriginalTemplateAndNeverDuplicatesBucket()
    {
        var request = GetObjectMetadataRequestMarshaller.Instance.Marshall(new GetObjectMetadataRequest
        {
            BucketName = "bucket", Key = "///leading"
        });
        request.Endpoint = new Uri("https://old.example.test/bucket");
        ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);
        request.Endpoint = new Uri("https://new.example.test/bucket");
        ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);
        Assert.Equal("/bucket////leading", AmazonServiceClient.ComposeUrl(request).AbsolutePath);
        Assert.Equal("new.example.test", request.Endpoint.Host);
        request.Endpoint = new Uri("https://bucket.virtual.example.test/");
        ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);
        Assert.Equal("/{Key+}", request.ResourcePath);
        Assert.Single(request.PathResources);
        Assert.Equal("////leading", AmazonServiceClient.ComposeUrl(request).AbsolutePath);
    }

    [Fact]
    public void NormalObjectRequestIsNotChanged()
    {
        var request = GetObjectMetadataRequestMarshaller.Instance.Marshall(new GetObjectMetadataRequest
        {
            BucketName = "bucket", Key = "normal//file"
        });
        var endpoint = new Uri("https://s3.example.test/bucket");
        request.Endpoint = endpoint;
        ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);
        Assert.Same(endpoint, request.Endpoint);
        Assert.Equal("/{Key+}", request.ResourcePath);
        Assert.Single(request.PathResources);
    }

    [Theory]
    [InlineData(AddressingStyle.PathStyle, "", "s3.example.test", "/bucket//leading")]
    [InlineData(AddressingStyle.PathStyle, "/api/s3", "s3.example.test", "/api/s3/bucket//leading")]
    [InlineData(AddressingStyle.VirtualHosted, "", "bucket.s3.example.test", "//leading")]
    public async Task ProductionFactoryPreservesLeadingKeyForBothAddressingStyles(
        AddressingStyle style, string endpointPath, string expectedHost, string expectedPath)
    {
        using var transport = new CaptureTransport(request =>
        {
            AssertValidSignature(request, "us-east-1");
            Assert.Equal(expectedHost, request.RequestUri!.Host);
            Assert.Equal(expectedPath, request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        Assert.True(await service.ObjectExistsAsync(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3,
            AddressingStyle = style, Endpoint = "https://s3.example.test" + endpointPath
        }, "bucket", "/leading", TestContext.Current.CancellationToken));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task LeadingKeyTemporaryRedirectFailsBeforeReissuingRequest()
    {
        using var body = new MemoryStream([]);
        using var transport = new CaptureTransport(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new StreamContent(body) };
            response.Headers.Location = new Uri("https://other.s3.example.test/bucket/leading");
            return response;
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ObjectExistsAsync(
            Profile with { ServiceType = S3ServiceType.AmazonS3 }, "bucket", "/leading", TestContext.Current.CancellationToken));
        Assert.Single(transport.Requests);
        Assert.Throws<ObjectDisposedException>(() => body.ReadByte());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryKeyTemporaryRedirectHonorsProfileSetting(bool follow)
    {
        var count = 0;
        string? originalAuthorization = null;
        using var transport = new CaptureTransport(request =>
        {
            if (++count == 1)
            {
                originalAuthorization = AssertValidSignature(request, "us-east-1");
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
                response.Headers.Location = new Uri("https://bucket.s3.us-west-2.amazonaws.com/file");
                return response;
            }
            Assert.Equal("bucket.s3.us-west-2.amazonaws.com", request.RequestUri!.Host);
            Assert.Equal("/file", request.RequestUri.AbsolutePath);
            Assert.NotEqual(originalAuthorization, AssertValidSignature(request, "us-west-2"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var operation = service.ObjectExistsAsync(Profile with { ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.us-east-1.amazonaws.com", FollowTemporaryRedirects = follow },
            "bucket", "file", TestContext.Current.CancellationToken);
        if (follow) Assert.True(await operation);
        else await Assert.ThrowsAsync<NotSupportedException>(() => operation);
        Assert.Equal(follow ? 2 : 1, transport.Requests.Count);
    }

    private static string AssertValidSignature(HttpRequestMessage request, string expectedRegion)
    {
        var uri = Assert.IsType<Uri>(request.RequestUri);
        Assert.True(uri.IsAbsoluteUri);
        // The SDK adds this raw header without validation. Its semicolon-separated
        // SignedHeaders is not a generic HTTP auth-param token, so .NET's typed
        // Headers.Authorization parser can return null even when it is present.
        Assert.True(request.Headers.NonValidated.TryGetValues("Authorization", out var values));
        var authorization = Assert.Single(values);
        const string algorithm = "AWS4-HMAC-SHA256";
        Assert.StartsWith(algorithm + " ", authorization);
        var parameters = authorization[(algorithm.Length + 1)..].Split(',')
            .Select(value => value.Trim().Split('=', 2))
            .ToDictionary(value => value[0], value => value[1], StringComparer.Ordinal);
        var credential = parameters["Credential"].Split('/');
        Assert.Equal(5, credential.Length);
        Assert.Equal("test", credential[0]);
        Assert.Equal(expectedRegion, credential[2]);
        Assert.Equal("s3", credential[3]);
        Assert.Equal("aws4_request", credential[4]);

        string Header(string name)
        {
            if (request.Headers.NonValidated.TryGetValues(name, out var header)) return header.ToString();
            if (request.Content is not null && request.Content.Headers.NonValidated.TryGetValues(name, out header))
                return header.ToString();
            if (name == "host") return uri.Authority;
            throw new InvalidOperationException("Signed header is missing: " + name);
        }

        var signedHeaders = parameters["SignedHeaders"];
        var headerNames = signedHeaders.Split(';');
        Assert.Contains("host", headerNames);
        Assert.Contains("x-amz-date", headerNames);

        Assert.Equal(uri.Authority, Header("host"));
        Assert.Equal(credential[1], Header("x-amz-date")[..8]);
        Assert.Equal(headerNames.Order(StringComparer.Ordinal), headerNames);
        var canonicalHeaders = string.Concat(headerNames.Select(name =>
            name + ":" + string.Join(' ', Header(name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + "\n"));
        var canonicalRequest = string.Join("\n", request.Method.Method, uri.AbsolutePath,
            CanonicalQuery(uri), canonicalHeaders, signedHeaders, Header("x-amz-content-sha256"));
        var scope = string.Join('/', credential[1..]);
        var stringToSign = string.Join("\n", algorithm, Header("x-amz-date"), scope,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());
        var signingKey = Encoding.UTF8.GetBytes("AWS4test");
        foreach (var component in credential[1..])
            signingKey = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(component));
        var expectedSignature = Convert.ToHexString(HMACSHA256.HashData(signingKey,
            Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        Assert.Equal(expectedSignature, parameters["Signature"]);
        return authorization;
    }

    [Theory]
    [InlineData(S3ServiceType.MinIO, "/leading")]
    [InlineData(S3ServiceType.MinIO, "a//file")]
    [InlineData(S3ServiceType.Custom, "/leading")]
    [InlineData(S3ServiceType.Custom, "a//file")]
    public async Task FactoryInterfacePresignRefusesUnsupportedKeyBeforeSigning(S3ServiceType provider, string key)
    {
        using var transport = new CaptureTransport(_ => throw new InvalidOperationException("No network call expected."));
        using var client = new S3ClientFactory { HttpClientFactoryOverride = transport }
            .Create(Profile with { ServiceType = provider });
        var request = new GetPreSignedUrlRequest { BucketName = "bucket", Key = key, Expires = DateTime.UtcNow.AddHours(1) };
        Assert.Throws<TransferExecutionException>(() => client.GetPreSignedURL(request));
        await Assert.ThrowsAsync<TransferExecutionException>(() => client.GetPreSignedURLAsync(request));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(S3ServiceType.MinIO, "folder/")]
    [InlineData(S3ServiceType.Custom, "folder/")]
    [InlineData(S3ServiceType.AmazonS3, "folder//file")]
    public void FactoryInterfacePresignPreservesSupportedKeys(S3ServiceType provider, string key)
    {
        using var transport = new CaptureTransport(_ => throw new InvalidOperationException("No network call expected."));
        using var client = new S3ClientFactory { HttpClientFactoryOverride = transport }
            .Create(Profile with { ServiceType = provider });
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = "bucket", Key = key, Expires = DateTime.UtcNow.AddHours(1)
        });
        var uri = new Uri(url);
        Assert.Equal("/bucket/" + key, Uri.UnescapeDataString(uri.AbsolutePath));
        Assert.True(Query(uri).ContainsKey("X-Amz-Signature"));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("file", 1)]
    [InlineData(" ", 1)]
    [InlineData("folder/", 0)]
    [InlineData("folder//", 0)]
    public async Task OnlyCurrentDirectoryMarkerIsHiddenFromBrowsing(string prefix, int expectedCount)
    {
        using var transport = new CaptureTransport(_ => Xml(new XElement("ListBucketResult",
            new XElement("IsTruncated", false),
            new XElement("Contents", new XElement("Key", prefix), new XElement("Size", 0))).ToString()));
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var page = await service.ListObjectsAsync(Profile, "bucket", prefix, null, 100, TestContext.Current.CancellationToken);
        Assert.Equal(expectedCount, page.Items.Count);
    }

    [Theory]
    [InlineData("/leading//back\\slash/%2F/./../ ")]
    [InlineData("//")]
    [InlineData(" ")]
    public async Task AllListingsSendExactPrefixesAndRetainReturnedKeys(string prefix)
    {
        using var transport = new CaptureTransport(request =>
        {
            var query = Query(request.RequestUri!);
            Assert.Equal(prefix, query["prefix"]);
            if (query.ContainsKey("versions"))
            {
                Assert.Equal(" ", query["key-marker"]);
                return Xml("<ListVersionsResult><IsTruncated>false</IsTruncated></ListVersionsResult>");
            }
            if (query.ContainsKey("uploads"))
                return Xml("<ListMultipartUploadsResult><IsTruncated>false</IsTruncated></ListMultipartUploadsResult>");
            var result = new XElement("ListBucketResult",
                new XElement("IsTruncated", false),
                new XElement("CommonPrefixes", new XElement("Prefix", prefix + "/")),
                new XElement("Contents", new XElement("Key", prefix + "file%20.txt"), new XElement("Size", 1)));
            return Xml(result.ToString());
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var page = await service.ListObjectsAsync(Profile, "bucket", prefix, null, 100, TestContext.Current.CancellationToken);
        Assert.Equal(prefix + "/", Assert.Single(page.Items, item => item.IsDirectory).Key);
        Assert.Equal(prefix + "file%20.txt", Assert.Single(page.Items, item => !item.IsDirectory).Key);
        await service.ListObjectsForDeletionAsync(Profile, "bucket", prefix, null, 100, TestContext.Current.CancellationToken);
        await service.ListObjectVersionsAsync(Profile with { ServiceType = S3ServiceType.AmazonS3 }, "bucket", prefix,
            " ", null, 100, TestContext.Current.CancellationToken);
        await service.ListIncompleteMultipartUploadsAsync(Profile, "bucket", prefix, null, TestContext.Current.CancellationToken);
        Assert.Equal(4, transport.Requests.Count);
    }

    [Theory]
    [InlineData("/leading//file")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("///leading")]
    [InlineData("a/./file")]
    [InlineData("a/../file")]
    [InlineData("../file")]
    [InlineData("back\\slash.txt")]
    [InlineData("literal%2F%20%252F.txt")]
    [InlineData(" ")]
    [InlineData("a/中文 + #?.txt")]
    public async Task SdkReadPutCopyAndDeletePreserveActualRequestPath(string key)
    {
        using var transport = new CaptureTransport(request =>
        {
            // Inspect the URI delivered to the HTTP handler, not the SDK input
            // model: System.Uri must not resolve dots, backslashes or slashes.
            Assert.Equal("/bucket/" + key, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            if (request.Headers.TryGetValues("x-amz-copy-source", out var sources))
            {
                var source = Uri.UnescapeDataString(Assert.Single(sources));
                Assert.True(source == "source/" + key || source == "/source/" + key, source);
                return Xml("<CopyObjectResult><ETag>\"copy-etag\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></CopyObjectResult>");
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = 0;
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test-etag\"");
            return response;
        });
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        var service = new S3StorageService(factory);
        // These exact paths are valid on AWS. MinIO and unverified custom servers
        // have a separate fail-closed policy rather than changing the key.
        var profile = Profile with { ServiceType = S3ServiceType.AmazonS3 };
        Assert.True(await service.ObjectExistsAsync(profile, "bucket", key, TestContext.Current.CancellationToken));
        using var client = factory.Create(profile);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = "bucket", Key = key, ContentBody = "test" },
            TestContext.Current.CancellationToken);
        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = "source", SourceKey = key, DestinationBucket = "bucket", DestinationKey = key
        }, TestContext.Current.CancellationToken);
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = "bucket", Key = key },
            TestContext.Current.CancellationToken);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Put, HttpMethod.Put, HttpMethod.Delete }, transport.Requests);
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(value => value.Split('=', 2))
        .ToDictionary(value => Uri.UnescapeDataString(value[0]),
            value => value.Length == 1 ? string.Empty : Uri.UnescapeDataString(value[1]), StringComparer.Ordinal);

    private static ConnectionProfile Profile => new()
    {
        Name = "isolated", ServiceType = S3ServiceType.Custom, Endpoint = "https://s3.example.test",
        Region = "us-east-1", AccessKey = "test", SecretKey = "test", AddressingStyle = AddressingStyle.PathStyle
    };

    private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml")
    };

    private sealed class CaptureTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public List<HttpMethod> Requests { get; } = [];
        public override HttpClient CreateHttpClient(IClientConfig config)
        {
            Assert.False(config.AllowAutoRedirect); // HTTP must not bypass the SDK's redirect policy.
            return _client ??= new HttpClient(new Handler(request =>
            {
                Requests.Add(request.Method);
                return respond(request);
            }));
        }
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public void Dispose() => _client?.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
