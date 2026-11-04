using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class AuditTransportSafetyTests
{
    [Fact]
    public async Task CompatibilityTransportPreservesGzipHeadLengthAndRawObjectBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "s3-gzip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var gzipBuffer = new MemoryStream();
        using (var gzip = new GZipStream(gzipBuffer, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes("Object storage must preserve these compressed bytes."));
        var bytes = gzipBuffer.ToArray();
        var etag = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var methods = new ConcurrentQueue<string>();
        var server = ServeAsync();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var profile = Profile with { Endpoint = $"http://127.0.0.1:{endpoint.Port}", IgnoreCertificateErrors = true };
            var destination = Path.Combine(root, "object.gz");
            await new S3StorageService(new S3ClientFactory()).DownloadFileAsync(
                profile, "bucket", "object.gz", destination, Context(), timeout.Token);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, timeout.Token));
            Assert.Equal(new[] { "HEAD", "GET" }, methods.ToArray());
            await server;
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
            Directory.Delete(root, recursive: true);
        }

        async Task ServeAsync()
        {
            for (var index = 0; index < 2; index++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(timeout.Token);
                var method = request!.Split(' ')[0];
                methods.Enqueue(method);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nContent-Encoding: gzip\r\nETag: \"{etag}\"\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, timeout.Token);
                if (method == "GET") await stream.WriteAsync(bytes, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
        }
    }

    [Theory]
    [InlineData("SignatureDoesNotMatch", false, false)]
    [InlineData("InvalidAccessKeyId", false, false)]
    [InlineData("AccessDenied", false, false)]
    [InlineData("AccessDenied", true, true)]
    public async Task RestrictedConnectionRequiresSuccessfulConfiguredBucketRead(string error, bool configuredBucket, bool expectedSuccess)
    {
        using var transport = new TestHttpFactory(request =>
            request.RequestUri!.AbsolutePath.Trim('/') == "bucket"
                ? Xml("<ListBucketResult><Name>bucket</Name><IsTruncated>false</IsTruncated></ListBucketResult>")
                : Xml($"<Error><Code>{error}</Code><Message>denied</Message></Error>", HttpStatusCode.Forbidden));
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var result = await service.TestConnectionAsync(
            Profile with { DefaultBucket = configuredBucket ? "bucket" : "" }, TestContext.Current.CancellationToken);
        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(error == "AccessDenied" && configuredBucket ? 2 : 1, transport.Requests.Count);
    }

    [Fact]
    public async Task BucketOverviewKeepsOtherFeaturesWhenPolicyReadIsDenied()
    {
        using var transport = new TestHttpFactory(request =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("versioning", StringComparison.Ordinal))
                return Xml("<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>");
            if (query.Contains("tagging", StringComparison.Ordinal))
                return Xml("<Tagging><TagSet><Tag><Key>env</Key><Value>test</Value></Tag></TagSet></Tagging>");
            return Xml("<Error><Code>AccessDenied</Code><Message>denied</Message></Error>", HttpStatusCode.Forbidden);
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var profile = Profile with { ServiceType = S3ServiceType.AmazonS3 };
        var overview = await service.GetBucketPropertiesAsync(profile, "bucket", TestContext.Current.CancellationToken);
        Assert.Contains("Policy", overview.ReadErrors.Keys);
        Assert.Contains("Enabled", overview.VersioningStatus, StringComparison.Ordinal);
        Assert.True(overview.Capabilities.Tagging.Supported);
        var tags = await service.GetBucketTagsAsync(profile, "bucket", TestContext.Current.CancellationToken);
        Assert.Single(tags);
    }

    [Fact]
    public async Task NonpersistentMultipartFailureAbortsItsRemoteUpload()
    {
        var path = Path.GetTempFileName();
        using var transport = new TestHttpFactory(request => request.Method == HttpMethod.Post
            ? Xml("<InitiateMultipartUploadResult><Bucket>bucket</Bucket><Key>key</Key><UploadId>owned-upload</UploadId></InitiateMultipartUploadResult>")
            : request.Method == HttpMethod.Delete
                ? Xml("")
                : Xml("<Error><Code>InvalidRequest</Code><Message>isolated failure</Message></Error>", HttpStatusCode.BadRequest));
        try
        {
            await using (var file = File.OpenWrite(path)) file.SetLength(5L * 1024 * 1024 + 1);
            var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
            await Assert.ThrowsAsync<TransferExecutionException>(() => service.UploadFileAsync(
                Profile, "bucket", "key", path, "STANDARD",
                Context() with { Options = Context().Options with { RetainFailedTransferState = false } },
                TestContext.Current.CancellationToken));
            Assert.Single(transport.Requests, request => request.StartsWith("DELETE ", StringComparison.Ordinal) &&
                request.Contains("uploadId=owned-upload", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedDownloadDoesNotDeleteUnreservedScratchCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "s3-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "object");
        var unrelated = ResumableDownloadFile.TemporaryPath(destination);
        await File.WriteAllTextAsync(unrelated, "unrelated", TestContext.Current.CancellationToken);
        using var transport = new TestHttpFactory(_ => Xml("<Error><Code>AccessDenied</Code></Error>", HttpStatusCode.Forbidden));
        try
        {
            var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
            await Assert.ThrowsAsync<TransferExecutionException>(() => service.DownloadFileAsync(
                Profile, "bucket", "key", destination,
                Context() with { Options = Context().Options with { RetainFailedTransferState = false } },
                TestContext.Current.CancellationToken));
            Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelated, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SuccessfulDownloadDoesNotDeleteScratchNameReusedAfterCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "s3-committed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "object");
        var scratch = ResumableDownloadFile.TemporaryPath(destination);
        using var transport = new TestHttpFactory(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : new byte[] { 42 })
            };
            response.Content.Headers.ContentLength = 1;
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(
                "\"" + Convert.ToHexString(MD5.HashData(new byte[] { 42 })).ToLowerInvariant() + "\"");
            return response;
        });
        try
        {
            var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
            await service.DownloadFileAsync(Profile, "bucket", "key", destination,
                Context() with
                {
                    Options = Context().Options with { RetainFailedTransferState = false },
                    ReportProgress = _ =>
                    {
                        // Commit has moved the reserved file. A different transfer may now
                        // reserve the original candidate; it must not be cleaned up by us.
                        if (File.Exists(destination)) File.WriteAllText(scratch, "different-transfer");
                    }
                }, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal("different-transfer", await File.ReadAllTextAsync(scratch, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ConnectionProfile Profile => new()
    {
        Name = "isolated", ServiceType = S3ServiceType.Custom, Endpoint = "https://s3.example.test",
        Region = "us-east-1", AccessKey = "test", SecretKey = "test", AddressingStyle = AddressingStyle.PathStyle
    };
    private static TransferOperationContext Context() => new(
        new TransferExecutionOptions { MultipartThresholdBytes = 5L * 1024 * 1024, PartSizeBytes = 5L * 1024 * 1024, MultipartConcurrency = 1 },
        new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
    private static HttpResponseMessage Xml(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/xml")
    };
    private sealed class TestHttpFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpClientFactory, IDisposable
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        private HttpClient? _client;
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client ??= new HttpClient(new Handler(request =>
        {
            Requests.Enqueue(request.Method + " " + request.RequestUri);
            return responder(request);
        }));
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
        public void Dispose() => _client?.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
