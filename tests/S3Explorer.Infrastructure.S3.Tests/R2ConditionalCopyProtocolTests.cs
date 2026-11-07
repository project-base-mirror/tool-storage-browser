using System.Net;
using System.Text;
using Amazon.Runtime;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

// These run the production SDK serialization/signing/transport against an isolated
// protocol model. They do not claim to test the hosted R2 service or its atomicity.
public sealed class R2ConditionalCopyProtocolTests
{
    private const long MaximumSingleCopyBytes = 5L * 1024 * 1024 * 1024;
    private const string SourceEtag = "\"source-etag\"";
    private const string DestinationEtag = "\"destination-etag\"";

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    [InlineData(6L * 1024 * 1024, true)]
    [InlineData(MaximumSingleCopyBytes, false)]
    [InlineData(MaximumSingleCopyBytes, true)]
    public async Task CreateOnlyCopyUsesSignedR2HeadersAtEverySupportedSize(long length, bool multipartEnabled)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Head
            ? SourceHead(length) : Copied());
        await Run(transport, Context(), profile: Profile with { EnableMultipartCopy = multipartEnabled });

        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Put }, transport.Requests.Select(item => item.Method));
        var copy = transport.Requests[1];
        Assert.Equal("/target/copy", copy.Uri.AbsolutePath);
        Assert.Equal("source/key", Uri.UnescapeDataString(copy.Headers["x-amz-copy-source"]).TrimStart('/'));
        Assert.Equal(SourceEtag, copy.Headers["x-amz-copy-source-if-match"]);
        Assert.Equal("*", copy.Headers["cf-copy-destination-if-none-match"]);
        Assert.DoesNotContain("cf-copy-destination-if-match", copy.Headers.Keys);
        Assert.DoesNotContain("If-Match", copy.Headers.Keys);
        Assert.DoesNotContain("If-None-Match", copy.Headers.Keys);
        AssertSignedConditions(copy, "cf-copy-destination-if-none-match");
        Assert.DoesNotContain("versionId", copy.Headers["x-amz-copy-source"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("COPY", copy.Headers["x-amz-metadata-directive"]);
        Assert.DoesNotContain(copy.Headers.Keys, name => name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("x-amz-tagging", copy.Headers.Keys);
    }

    [Fact]
    public async Task ConditionalReplacementUsesR2DestinationEtagWithoutChangingSourceCondition()
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Head ? SourceHead(12) : Copied());
        await Run(transport, Context(overwrite: true, expected: DestinationEtag));
        var copy = transport.Requests[1];
        Assert.Equal(DestinationEtag, copy.Headers["cf-copy-destination-if-match"]);
        Assert.Equal(SourceEtag, copy.Headers["x-amz-copy-source-if-match"]);
        Assert.DoesNotContain("cf-copy-destination-if-none-match", copy.Headers.Keys);
        Assert.DoesNotContain("If-Match", copy.Headers.Keys);
        AssertSignedConditions(copy, "cf-copy-destination-if-match");
    }

    [Fact]
    public async Task ExplicitOverwriteDoesNotInventDestinationProtection()
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Head ? SourceHead(12) : Copied());
        await Run(transport, Context(overwrite: true));
        var copy = transport.Requests[1];
        Assert.Equal(SourceEtag, copy.Headers["x-amz-copy-source-if-match"]);
        Assert.DoesNotContain(copy.Headers.Keys, name => name.StartsWith("cf-copy-destination-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("If-Match", copy.Headers.Keys);
        Assert.DoesNotContain("If-None-Match", copy.Headers.Keys);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed", false)]
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed", true)]
    [InlineData(HttpStatusCode.Conflict, "ConditionalRequestConflict", false)]
    [InlineData(HttpStatusCode.Conflict, "ConditionalRequestConflict", true)]
    public async Task DestinationRaceFailsWithoutUnconditionalRetryOrDeletingSource(
        HttpStatusCode status, string error, bool replace)
    {
        var destination = "concurrent-writer";
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head) return SourceHead(12);
            // The model rejects only if the extension header reaches the wire.
            if (request.Headers.Contains(replace ? "cf-copy-destination-if-match" : "cf-copy-destination-if-none-match"))
                return Error(status, error);
            destination = "incorrectly-overwritten";
            return Copied();
        });
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Run(transport, Context(overwrite: replace, expected: replace ? DestinationEtag : null)));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.Equal("concurrent-writer", destination);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Put }, transport.Requests.Select(item => item.Method));
    }

    [Fact]
    public async Task SourceChangeBeforeSelectionIsRejectedWithoutWritingDestination()
    {
        var written = false;
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head) return SourceHead(12);
            if (Header(request, "x-amz-copy-source-if-match") == SourceEtag)
                return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            written = true;
            return Copied();
        });
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(transport, Context()));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.False(written);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Put }, transport.Requests.Select(item => item.Method));
    }

    [Fact]
    public async Task MissingSourceEtagFailsBeforeCopy()
    {
        using var transport = new Transport(_ =>
        {
            var response = SourceHead(12);
            response.Headers.Remove("ETag");
            return response;
        });
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(transport, Context()));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.Equal(HttpMethod.Head, Assert.Single(transport.Requests).Method);
    }

    [Fact]
    public async Task SourceChangeAfterSelectionMayCopyEarlierSnapshotWithoutDeletingNewSource()
    {
        var source = "selected-bytes";
        string? destination = null;
        // Include a field omitted from HEAD: the request must ask R2 to COPY
        // metadata server-side rather than rebuild a lossy snapshot from HEAD.
        var sourceMetadata = new Dictionary<string, string>
        {
            ["visible"] = "value", ["中文"] = "not exposed by S3 HEAD",
            ["Content-Language"] = "en", ["Content-Disposition"] = "attachment", ["Expires"] = "0"
        };
        Dictionary<string, string>? destinationMetadata = null;
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head) return SourceHead(12);
            Assert.Equal(SourceEtag, Header(request, "x-amz-copy-source-if-match"));
            var selected = source;
            var selectedMetadata = new Dictionary<string, string>(sourceMetadata);
            source = "new-source-bytes";
            sourceMetadata["visible"] = "new-metadata";
            // R2 does not promise that source selection and destination commit
            // share one instant. Only the destination predicate is checked now.
            Assert.Equal("*", Header(request, "cf-copy-destination-if-none-match"));
            Assert.Null(destination);
            Assert.Equal("COPY", Header(request, "x-amz-metadata-directive"));
            destination = selected;
            destinationMetadata = selectedMetadata;
            return Copied();
        });
        await Run(transport, Context());
        Assert.Equal("selected-bytes", destination);
        Assert.Equal("new-source-bytes", source);
        Assert.NotNull(destinationMetadata);
        Assert.Equal("value", destinationMetadata["visible"]);
        Assert.Equal("not exposed by S3 HEAD", destinationMetadata["中文"]);
        Assert.Equal("0", destinationMetadata["Expires"]);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Put }, transport.Requests.Select(item => item.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeRelayRefusesUnrecoverableMetadataInsteadOfDroppingIt(bool multipartEnabled)
    {
        using var transport = new Transport(_ => SourceHead(MaximumSingleCopyBytes + 1));
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(transport, Context(),
            profile: Profile with { EnableMultipartCopy = multipartEnabled }));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Head }, transport.Requests.Select(item => item.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyMultipartStateIsNeverReusedOrConvertedToSingleCopy(bool copyCompleted)
    {
        using var transport = new Transport(_ => throw new InvalidOperationException("No request should be sent."));
        var context = Context() with
        {
            MultipartCheckpoint = new MultipartUploadCheckpoint("legacy", 5L * 1024 * 1024, [],
                Bucket: "target", ObjectKey: "copy", SourceLength: 12)
            {
                CopySourceBucket = "source", CopySourceKey = "key", CopySourceETag = SourceEtag,
                CopySourceVersionId = "advertised-but-not-addressable", CopyCompleted = copyCompleted,
                CopyDestinationETag = DestinationEtag
            }
        };
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(transport, context));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "EntityTooLarge")]
    [InlineData(HttpStatusCode.BadRequest, "InvalidRequest")]
    [InlineData(HttpStatusCode.NotImplemented, "NotImplemented")]
    public async Task FailedCopyDoesNotLoseHiddenMetadataByRelayingIncompleteHead(HttpStatusCode status, string code)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Head
            ? SourceHead(12) : Error(status, code));
        await Assert.ThrowsAsync<TransferExecutionException>(() => Run(transport, Context()));
        Assert.Equal(HttpMethod.Head, transport.Requests[0].Method);
        Assert.All(transport.Requests.Where(item => item.Method == HttpMethod.Put), request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("*", request.Headers["cf-copy-destination-if-none-match"]);
            Assert.Equal(SourceEtag, request.Headers["x-amz-copy-source-if-match"]);
            AssertSignedConditions(request, "cf-copy-destination-if-none-match");
            Assert.DoesNotContain("uploadId", request.Uri.Query, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task LostSuccessfulResponseCannotCauseUnconditionalRetry()
    {
        var copies = 0;
        string? destination = null;
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head) return SourceHead(12);
            Assert.Equal("*", Header(request, "cf-copy-destination-if-none-match"));
            if (++copies == 1)
            {
                destination = "first-copy";
                // Model a committed write whose response was lost/replaced by a
                // gateway error. The SDK may retry the same protected request.
                return Error(HttpStatusCode.InternalServerError, "InternalError");
            }
            return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        });
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Run(transport, Context()));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.Equal("first-copy", destination);
        Assert.Equal(2, copies);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Put, HttpMethod.Put }, transport.Requests.Select(item => item.Method));
        Assert.All(transport.Requests.Skip(1), request => AssertSignedConditions(request, "cf-copy-destination-if-none-match"));
    }

    [Theory]
    [InlineData("/leading//file")]
    [InlineData("folder/")]
    [InlineData("literal%2F + 中文.txt")]
    public async Task EmptyObjectCopyRetainsExactSourceAndTargetKeys(string key)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Head ? SourceHead(0) : Copied());
        await Run(transport, Context(), sourceKey: key, destinationKey: key);
        Assert.Equal("/source/" + key, Uri.UnescapeDataString(transport.Requests[0].Uri.AbsolutePath));
        var copy = transport.Requests[1];
        Assert.Equal("/target/" + key, Uri.UnescapeDataString(copy.Uri.AbsolutePath));
        Assert.Equal("source/" + key, Uri.UnescapeDataString(copy.Headers["x-amz-copy-source"]).TrimStart('/'));
        Assert.Equal("*", copy.Headers["cf-copy-destination-if-none-match"]);
    }

    [Fact]
    public async Task MoveCopiesThenRechecksSourceBeforeOrdinaryDelete()
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Head ? SourceHead(12) :
            request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Copied());
        await Run(transport, Context(), move: true);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Put, HttpMethod.Head, HttpMethod.Delete },
            transport.Requests.Select(item => item.Method));
        var delete = transport.Requests.Last();
        Assert.DoesNotContain("If-Match", delete.Headers.Keys);
        Assert.DoesNotContain("versionId", delete.Uri.Query);
    }

    [Fact]
    public async Task SingleCopyLimitFallsBackToConditionalGetAndPutWithoutDroppingDestinationCondition()
    {
        using var transport = new Transport(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = SourceHead(12);
                head.Headers.Remove("x-amz-missing-meta");
                return head;
            }
            if (request.Method == HttpMethod.Put && Header(request, "x-amz-copy-source") is not null)
                return Error(HttpStatusCode.BadRequest, "EntityTooLarge");
            if (request.Method == HttpMethod.Get)
            {
                var get = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[12]) };
                get.Headers.TryAddWithoutValidation("ETag", SourceEtag);
                return get;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        await Run(transport, Context());
        var get = Assert.Single(transport.Requests, item => item.Method == HttpMethod.Get);
        Assert.Equal(SourceEtag, get.Headers["If-Match"]);
        Assert.DoesNotContain("versionId", get.Uri.Query);
        var put = transport.Requests.Last();
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("*", put.Headers["If-None-Match"]);
        Assert.DoesNotContain("x-amz-copy-source", put.Headers.Keys);
        Assert.DoesNotContain(transport.Requests, item => item.Uri.Query.Contains("uploadId"));
    }

    [Fact]
    public async Task SelfCopyIsRejectedBeforeAnyNetworkAccess()
    {
        using var transport = new Transport(_ => throw new InvalidOperationException("No request should be sent."));
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        await Assert.ThrowsAsync<TransferExecutionException>(() => service.CopyObjectAsync(
            Profile, "source", "key", "source", "key", Context(), TestContext.Current.CancellationToken));
        Assert.Empty(transport.Requests);
    }

    private static Task Run(Transport transport, TransferOperationContext context, bool move = false,
        ConnectionProfile? profile = null, string sourceKey = "key", string destinationKey = "copy")
    {
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        return move
            ? service.MoveObjectAsync(profile ?? Profile, "source", sourceKey, "target", destinationKey,
                context, TestContext.Current.CancellationToken)
            : service.CopyObjectAsync(profile ?? Profile, "source", sourceKey, "target", destinationKey,
                context, TestContext.Current.CancellationToken);
    }

    private static ConnectionProfile Profile => new()
    {
        Name = "isolated-r2", ServiceType = S3ServiceType.CloudflareR2,
        Endpoint = "https://r2.example.test", Region = "auto", AccessKey = "test-access", SecretKey = "test-secret",
        AddressingStyle = AddressingStyle.PathStyle
    };

    private static TransferOperationContext Context(bool overwrite = false, string? expected = null) => new(
        new TransferExecutionOptions
        {
            AllowDestinationOverwrite = overwrite, ExpectedDestinationETag = expected,
            MultipartThresholdBytes = 5L * 1024 * 1024, PartSizeBytes = 5L * 1024 * 1024,
            RetainFailedTransferState = false
        }, new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);

    private static HttpResponseMessage SourceHead(long length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = length;
        response.Headers.TryAddWithoutValidation("ETag", SourceEtag);
        response.Headers.TryAddWithoutValidation("x-amz-version-id", "advertised-but-not-addressable");
        response.Headers.TryAddWithoutValidation("x-amz-meta-visible", "value");
        response.Headers.TryAddWithoutValidation("x-amz-missing-meta", "1");
        return response;
    }

    private static HttpResponseMessage Copied() => Xml(HttpStatusCode.OK,
        "<CopyObjectResult><ETag>\"copy-etag\"</ETag><LastModified>2026-11-04T00:00:00Z</LastModified></CopyObjectResult>");
    private static HttpResponseMessage Error(HttpStatusCode status, string code) => Xml(status,
        $"<Error><Code>{code}</Code><Message>Isolated protocol rejection</Message><RequestId>test</RequestId></Error>");
    private static HttpResponseMessage Xml(HttpStatusCode status, string text) => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/xml")
    };
    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? Assert.Single(values) : null;

    private sealed record Request(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers);

    private static void AssertSignedConditions(Request request, string destinationHeader)
    {
        var field = Assert.Single(request.Headers["Authorization"].Split(',')
            .Select(value => value.Trim()), value => value.StartsWith("SignedHeaders=", StringComparison.Ordinal));
        var signed = field["SignedHeaders=".Length..].Split(';');
        Assert.Contains(destinationHeader, signed);
        Assert.Contains("x-amz-copy-source-if-match", signed);
    }

    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public List<Request> Requests { get; } = [];
        public override HttpClient CreateHttpClient(IClientConfig config) => _client ??= new HttpClient(new Handler(request =>
        {
            var headers = request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            if (request.Content is not null)
                foreach (var pair in request.Content.Headers) headers[pair.Key] = string.Join(",", pair.Value);
            Requests.Add(new Request(request.Method, request.RequestUri!, headers));
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
