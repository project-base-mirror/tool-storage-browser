using System.Net;
using System.Text;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ExactObjectKeyTransportTests
{
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
        Assert.True(await service.ObjectExistsAsync(Profile, "bucket", key, TestContext.Current.CancellationToken));
        using var client = factory.Create(Profile);
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
        public override HttpClient CreateHttpClient(IClientConfig config) => _client ??= new HttpClient(new Handler(request =>
        {
            Requests.Add(request.Method);
            return respond(request);
        }));
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
