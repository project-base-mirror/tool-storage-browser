using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed partial class ExactObjectKeyTransportTests
{
    public static IEnumerable<object[]> LeadingDownloadKeys()
    {
        foreach (var style in new[] { AddressingStyle.PathStyle, AddressingStyle.VirtualHosted })
        foreach (var key in new[] { "/", "//", "///leading", "/a//file", "/中文 + #?.txt", "/literal%2F/%2e/%252F", "/back\\slash/" })
        foreach (var session in new[] { false, true })
            yield return [style, key, session];
    }

    [Theory]
    [MemberData(nameof(LeadingDownloadKeys))]
    public async Task LeadingDownloadLinksHaveExactUsablePathAndIndependentSigV4(
        AddressingStyle style, string key, bool session)
    {
        using var transport = new CaptureTransport(_ => throw new InvalidOperationException("Signing must not send a request."));
        var profile = Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, AddressingStyle = style,
            SessionToken = session ? "session/+% token=中文" : ""
        };
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        using var client = factory.Create(profile);
        foreach (var verb in new[] { HttpVerb.GET, HttpVerb.HEAD })
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = "bucket", Key = key, Verb = verb, Expires = DateTime.UtcNow.AddHours(1),
                VersionId = "version/+ %中文", ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentDisposition = "attachment; filename=\"a + %.txt\"", ContentType = "text/plain; charset=utf-8"
                }
            };
            request.Parameters["custom space+"] = "中文/+%?#=";
            var urls = new[] { client.GetPreSignedURL(request), await client.GetPreSignedURLAsync(request) };
            foreach (var url in urls)
            {
                var uri = new Uri(url); // ordinary consumer parsing must retain the exact path
                Assert.Equal((style == AddressingStyle.PathStyle ? "/bucket/" : "/") + Encode(key, path: true), uri.AbsolutePath);
                Assert.Equal(style == AddressingStyle.PathStyle ? "s3.example.test" : "bucket.s3.example.test", uri.Host);
                Assert.Equal(key, request.Key);
                Assert.Equal("version/+ %中文", Query(uri)["versionId"]);
                Assert.Equal("中文/+%?#=", Query(uri)["custom space+"]);
                Assert.Equal("attachment; filename=\"a + %.txt\"", Query(uri)["response-content-disposition"]);
                if (session) Assert.Equal(profile.SessionToken, Query(uri)["X-Amz-Security-Token"]);
                else Assert.False(Query(uri).ContainsKey("X-Amz-Security-Token"));
                AssertValidPresignedSignature(uri, verb.ToString(), "us-east-1");
            }
        }
        var serviceUrl = new S3StorageService(factory).CreatePresignedUrl(profile, "bucket", key, TimeSpan.FromMinutes(10));
        AssertValidPresignedSignature(new Uri(serviceUrl), "GET", "us-east-1");
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("https://s3.example.test/api%20path/%25", "/api%20path/%25/bucket//file", 443)]
    [InlineData("http://s3.example.test/api", "/api/bucket//file", 443)]
    [InlineData("https://s3.example.test:9443/api%2Fpath", "/api%2Fpath/bucket//file", 9443)]
    public void LeadingDownloadPresignRetainsEndpointPathAndRequestedProtocol(string endpoint, string expectedPath, int port)
    {
        using var client = new S3ClientFactory().Create(Profile with { ServiceType = S3ServiceType.AmazonS3, Endpoint = endpoint });
        var uri = new Uri(client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = "bucket", Key = "/file", Expires = DateTime.UtcNow.AddMinutes(5), Protocol = Protocol.HTTPS
        }));
        Assert.Equal("https", uri.Scheme);
        Assert.Equal(port, uri.Port);
        Assert.Equal(expectedPath, uri.AbsolutePath);
        AssertValidPresignedSignature(uri, "GET", "us-east-1");
    }

    [Theory]
    [InlineData("/./file")]
    [InlineData("/a/../file")]
    [InlineData("a/./file")]
    [InlineData("../file")]
    public async Task BrowserNormalizedDotSegmentsAreRefusedBeforePresigning(string key)
    {
        using var client = new S3ClientFactory().Create(Profile with { ServiceType = S3ServiceType.AmazonS3 });
        var request = new GetPreSignedUrlRequest { BucketName = "bucket", Key = key, Expires = DateTime.UtcNow.AddHours(1) };
        Assert.Throws<NotSupportedException>(() => client.GetPreSignedURL(request));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetPreSignedURLAsync(request));
    }

    [Theory]
    [InlineData(HttpVerb.PUT)]
    [InlineData(HttpVerb.DELETE)]
    public void LeadingPresignDoesNotSilentlySignUnsupportedOperations(HttpVerb verb)
    {
        using var client = new S3ClientFactory().Create(Profile with { ServiceType = S3ServiceType.AmazonS3 });
        Assert.Throws<NotSupportedException>(() => client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = "bucket", Key = "/file", Verb = verb, Expires = DateTime.UtcNow.AddHours(1)
        }));
    }

    [Theory]
    [InlineData(AddressingStyle.PathStyle, false, "/leading//file")]
    [InlineData(AddressingStyle.PathStyle, true, "/leading//file")]
    [InlineData(AddressingStyle.VirtualHosted, true, "/literal%2F/中文 + #?.txt")]
    [InlineData(AddressingStyle.VirtualHosted, false, "/literal%2F/中文 + #?.txt")]
    [InlineData(AddressingStyle.PathStyle, false, "/a/./../file")]
    [InlineData(AddressingStyle.PathStyle, true, "/")]
    public async Task AwsRegionalRedirectPreservesExactPathQueryAndFreshSignature(
        AddressingStyle originalStyle, bool destinationPathStyle, string key)
    {
        var count = 0;
        using var firstBody = new MemoryStream([]);
        using var transport = new CaptureTransport(request =>
        {
            var region = ++count == 1 ? "us-east-1" : "us-west-2";
            AssertValidSignature(request, region);
            Assert.Equal("session-token", Assert.Single(request.Headers.GetValues("x-amz-security-token")));
            Assert.Equal("version /+%中文", Query(request.RequestUri!)["versionId"]);
            if (count == 1)
            {
                var host = destinationPathStyle ? "s3.us-west-2.amazonaws.com" : "bucket.s3.us-west-2.amazonaws.com";
                var path = (destinationPathStyle ? "/bucket/" : "/") + Encode(key, path: true);
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new StreamContent(firstBody) };
                response.Headers.TryAddWithoutValidation("Location", "https://" + host + path + request.RequestUri!.Query);
                return response;
            }
            Assert.Equal((destinationPathStyle ? "/bucket/" : "/") + Encode(key, path: true), request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        using var client = new S3ClientFactory { HttpClientFactoryOverride = transport }.Create(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.us-east-1.amazonaws.com",
            AddressingStyle = originalStyle, SessionToken = "session-token"
        });
        await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = "bucket", Key = key, VersionId = "version /+%中文"
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
        Assert.Throws<ObjectDisposedException>(() => firstBody.ReadByte());
    }

    [Theory]
    [InlineData("https://bucket.s3.us-west-2.attacker.test//file")]
    [InlineData("https://bucket.s3.us-west-2.amazonaws.com.attacker.test//file")]
    [InlineData("https://other.s3.us-west-2.amazonaws.com//file")]
    [InlineData("https://bucket.s3.us-west-2.amazonaws.com/file")]
    [InlineData("https://bucket.s3.us-west-2.amazonaws.com//file?versionId=evil")]
    [InlineData("https://bucket.s3.us-west-2.amazonaws.com//file#fragment")]
    [InlineData("https://user@bucket.s3.us-west-2.amazonaws.com//file")]
    [InlineData("http://bucket.s3.us-west-2.amazonaws.com//file")]
    [InlineData("https://bucket.s3.us-west-2.amazonaws.com:9443//file")]
    [InlineData("https://bucket.s3.us-west-2.amazonaws.com//%ZZ")]
    [InlineData("https://bucket.s3.cn-north-1.amazonaws.com.cn//file")]
    [InlineData("//bucket.s3.us-west-2.amazonaws.com//file")]
    public async Task UnsafeRedirectNeverForwardsSessionCredentials(string target)
    {
        using var body = new MemoryStream([]);
        using var transport = new CaptureTransport(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new StreamContent(body) };
            response.Headers.TryAddWithoutValidation("Location", target);
            return response;
        });
        using var client = new S3ClientFactory { HttpClientFactoryOverride = transport }.Create(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.us-east-1.amazonaws.com", SessionToken = "fake-session-token"
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = "bucket", Key = "/file"
        }, TestContext.Current.CancellationToken));
        Assert.Single(transport.Requests);
        Assert.Throws<ObjectDisposedException>(() => body.ReadByte());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibleEndpointOnlyRedirectsToSameAuthorityAndExactRequest(bool sameAuthority)
    {
        var count = 0;
        using var transport = new CaptureTransport(request =>
        {
            AssertValidSignature(request, "us-east-1");
            if (++count == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
                response.Headers.TryAddWithoutValidation("Location", sameAuthority ? request.RequestUri!.AbsoluteUri :
                    "https://other.s3.example.test/api/bucket/file");
                return response;
            }
            Assert.Equal("/api/bucket/file", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var task = service.ObjectExistsAsync(Profile with { Endpoint = "https://s3.example.test/api" },
            "bucket", "file", TestContext.Current.CancellationToken);
        if (sameAuthority) Assert.True(await task);
        else await Assert.ThrowsAsync<NotSupportedException>(() => task);
        Assert.Equal(sameAuthority ? 2 : 1, count);
    }

    [Fact]
    public async Task RepeatedRedirectsAreBoundedAndDisposeEveryResponse()
    {
        var bodies = new List<MemoryStream>();
        using var transport = new CaptureTransport(request =>
        {
            var body = new MemoryStream([]);
            bodies.Add(body);
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new StreamContent(body) };
            response.Headers.TryAddWithoutValidation("Location", request.RequestUri!.AbsoluteUri);
            return response;
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ObjectExistsAsync(Profile, "bucket", "file", TestContext.Current.CancellationToken));
        Assert.Equal(4, bodies.Count);
        foreach (var body in bodies) Assert.Throws<ObjectDisposedException>(() => body.ReadByte());
    }

    [Fact]
    public async Task LeadingKeyRetryAfterTransientFailureKeepsSignatureAndBucketExactlyOnce()
    {
        var count = 0;
        using var transport = new CaptureTransport(request =>
        {
            Assert.Equal("/api/bucket//file%252F", request.RequestUri!.AbsolutePath);
            AssertValidSignature(request, "us-east-1");
            return new HttpResponseMessage(++count == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
                { Content = new ByteArrayContent([]) };
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        Assert.True(await service.ObjectExistsAsync(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.example.test/api"
        }, "bucket", "/file%2F", TestContext.Current.CancellationToken));
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(AddressingStyle.PathStyle)]
    [InlineData(AddressingStyle.VirtualHosted)]
    public async Task AwsRegionCorrectionRetryRetainsExactLeadingKeyAndSignsNewRegion(AddressingStyle style)
    {
        var bucket = "region-retry-" + Guid.NewGuid().ToString("N");
        var count = 0;
        using var transport = new CaptureTransport(request =>
        {
            AssertValidSignature(request, ++count == 1 ? "us-east-1" : "us-west-2");
            Assert.Equal((style == AddressingStyle.PathStyle ? "/" + bucket + "/" : "/") + "/file%252F", request.RequestUri!.AbsolutePath);
            if (count == 1)
            {
                var response = Xml("<Error><Code>AuthorizationHeaderMalformed</Code><Message>Wrong region</Message><Region>us-west-2</Region></Error>");
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Headers.Add("x-amz-bucket-region", "us-west-2");
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        using var client = new S3ClientFactory { HttpClientFactoryOverride = transport }.Create(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.us-east-1.amazonaws.com", AddressingStyle = style
        });
        using var result = await client.GetObjectAsync(bucket, "/file%2F", TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryKeyRedirectRefusesMaliciousAuthorityOrCustomHost(bool customHost)
    {
        using var transport = new CaptureTransport(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("Location", customHost ? request.RequestUri!.AbsoluteUri :
                "https://bucket.s3.us-west-2.attacker.test/file");
            return response;
        });
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ObjectExistsAsync(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.us-east-1.amazonaws.com",
            SessionToken = "fake-session-token", CustomHostHeader = customHost ? "custom.example.test" : ""
        }, "bucket", "file", TestContext.Current.CancellationToken));
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("COPY")]
    [InlineData("DELETE")]
    public async Task LeadingKeyMutationRedirectRetainsBodyAndOperation(string operation)
    {
        var count = 0;
        using var transport = new CaptureTransport(request =>
        {
            AssertValidSignature(request, ++count == 1 ? "us-east-1" : "us-west-2");
            Assert.Equal(count == 1 ? "/bucket//file" : "//file", request.RequestUri!.AbsolutePath);
            Assert.Equal(operation == "DELETE" ? HttpMethod.Delete : HttpMethod.Put, request.Method);
            if (operation == "PUT") Assert.Equal("body that must be replayed", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if (operation == "COPY") Assert.Equal("source//original", Uri.UnescapeDataString(Assert.Single(request.Headers.GetValues("x-amz-copy-source"))).TrimStart('/'));
            if (count == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
                response.Headers.TryAddWithoutValidation("Location", "https://bucket.s3.us-west-2.amazonaws.com//file");
                return response;
            }
            return operation == "COPY" ? Xml("<CopyObjectResult><ETag>\"copied\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></CopyObjectResult>") :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        using var client = new S3ClientFactory { HttpClientFactoryOverride = transport }.Create(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.us-east-1.amazonaws.com"
        });
        if (operation == "PUT") await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "bucket", Key = "/file", ContentBody = "body that must be replayed",
            UseChunkEncoding = false, DisablePayloadSigning = true, DisableDefaultChecksumValidation = true
        }, TestContext.Current.CancellationToken);
        else if (operation == "COPY") await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = "source", SourceKey = "/original", DestinationBucket = "bucket", DestinationKey = "/file"
        }, TestContext.Current.CancellationToken);
        else await client.DeleteObjectAsync("bucket", "/file", TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RedirectCannotRemoveVersionSelection()
    {
        using var transport = new CaptureTransport(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("Location", "https://bucket.s3.us-west-2.amazonaws.com//file");
            return response;
        });
        using var client = new S3ClientFactory { HttpClientFactoryOverride = transport }.Create(Profile with
        {
            ServiceType = S3ServiceType.AmazonS3, Endpoint = "https://s3.us-east-1.amazonaws.com"
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = "bucket", Key = "/file", VersionId = "keep-this-version"
        }, TestContext.Current.CancellationToken));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public void R2LeadingDownloadUsesAutoSigningRegion()
    {
        using var client = new S3ClientFactory().Create(Profile with
        {
            ServiceType = S3ServiceType.CloudflareR2, Endpoint = "https://test.r2.cloudflarestorage.com", Region = "auto"
        });
        var uri = new Uri(client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = "bucket", Key = "/file", Expires = DateTime.UtcNow.AddMinutes(5)
        }));
        Assert.Equal("/bucket//file", uri.AbsolutePath);
        AssertValidPresignedSignature(uri, "GET", "auto");
    }

    private static string CanonicalQuery(Uri uri, bool presigned = false) => string.Join('&',
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .Where(value => !presigned || Uri.UnescapeDataString(value[0]) != "X-Amz-Signature")
            .Select(value => (Key: Encode(Uri.UnescapeDataString(value[0])),
                Value: Encode(value.Length == 2 ? Uri.UnescapeDataString(value[1]) : "")))
            .OrderBy(value => value.Key, StringComparer.Ordinal).ThenBy(value => value.Value, StringComparer.Ordinal)
            .Select(value => value.Key + "=" + value.Value));

    private static string Encode(string value, bool path = false) => string.Concat(Encoding.UTF8.GetBytes(value).Select(octet =>
        octet is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or
            (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~' || path && octet == (byte)'/'
            ? ((char)octet).ToString() : "%" + octet.ToString("X2")));

    private static void AssertValidPresignedSignature(Uri uri, string method, string expectedRegion)
    {
        var query = Query(uri);
        Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
        Assert.Equal("host", query["X-Amz-SignedHeaders"]);
        Assert.InRange(int.Parse(query["X-Amz-Expires"]), 1, 604800);
        var credential = query["X-Amz-Credential"].Split('/');
        Assert.Equal("test", credential[0]);
        Assert.Equal(expectedRegion, credential[2]);
        Assert.Equal("s3", credential[3]);
        Assert.Equal("aws4_request", credential[4]);
        var canonicalRequest = string.Join('\n', method, uri.AbsolutePath, CanonicalQuery(uri, presigned: true),
            "host:" + uri.Authority + "\n", "host", "UNSIGNED-PAYLOAD");
        var stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", query["X-Amz-Date"], string.Join('/', credential[1..]),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());
        var signingKey = Encoding.UTF8.GetBytes("AWS4test");
        foreach (var part in credential[1..]) signingKey = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(part));
        Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant(), query["X-Amz-Signature"]);
    }
}
