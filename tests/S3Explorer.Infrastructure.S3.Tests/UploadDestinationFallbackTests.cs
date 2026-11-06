using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Amazon.Runtime;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class UploadDestinationFallbackTests
{
    public static TheoryData<S3ServiceType, bool> FallbackProviders
    {
        get
        {
            var cases = new TheoryData<S3ServiceType, bool>();
            foreach (var provider in Enum.GetValues<S3ServiceType>())
            foreach (var multipart in new[] { false, true })
                if (provider is not (S3ServiceType.AmazonS3 or S3ServiceType.MinIO) &&
                    (provider != S3ServiceType.CloudflareR2 || multipart))
                    cases.Add(provider, multipart);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(FallbackProviders))]
    public async Task MissingDestinationAllowsOrdinaryUploadWithoutUnsupportedConditions(S3ServiceType provider, bool multipart)
    {
        using var transport = new Transport(_ => HttpStatusCode.NotFound);
        await Upload(transport, provider, multipart);
        Assert.Equal(multipart ? 2 : 1, transport.Heads);
        Assert.Equal(multipart ? 1 : 0, transport.Initiates);
        var commit = Assert.Single(transport.Commits);
        Assert.Null(commit.NoneMatch);
        Assert.Null(commit.Match);
        Assert.Equal(0, transport.Aborts);
        Assert.All(transport.Paths, path => Assert.EndsWith("/folder/file.bin", path));
    }

    [Theory]
    [MemberData(nameof(FallbackProviders))]
    public async Task ExistingDestinationReturnsConflictBeforeAnyWrite(S3ServiceType provider, bool multipart)
    {
        using var transport = new Transport(_ => HttpStatusCode.OK);
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Upload(transport, provider, multipart));
        Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        Assert.Equal(1, transport.Heads);
        Assert.Equal(0, transport.Initiates);
        Assert.Empty(transport.Commits);
        Assert.Equal(0, transport.Parts);
    }

    [Theory]
    [MemberData(nameof(FallbackProviders))]
    public async Task ExplicitOverwriteDoesNotRequireDestinationReadAccess(S3ServiceType provider, bool multipart)
    {
        using var transport = new Transport(_ => HttpStatusCode.Forbidden);
        await Upload(transport, provider, multipart, overwrite: true);
        Assert.Equal(0, transport.Heads);
        var commit = Assert.Single(transport.Commits);
        Assert.Null(commit.NoneMatch);
        Assert.Null(commit.Match);
    }

    [Theory]
    [MemberData(nameof(FallbackProviders))]
    public async Task ExpectedVersionStillFailsBeforeAnyRequest(S3ServiceType provider, bool multipart)
    {
        using var transport = new Transport(_ => HttpStatusCode.NotFound);
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            Upload(transport, provider, multipart, overwrite: true, expected: "\"original\""));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.Empty(transport.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionFailureIsNotTreatedAsMissing(bool multipart)
    {
        using var transport = new Transport(_ => HttpStatusCode.Forbidden);
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Upload(transport, S3ServiceType.Custom, multipart));
        Assert.Equal(TransferFailureCategory.Authorization, failure.Failure.Category);
        Assert.Equal(1, transport.Heads);
        Assert.Equal(0, transport.Initiates);
        Assert.Empty(transport.Commits);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task MultipartRechecksBeforeCommitAndAbortsOnConflictOrReadFailure(HttpStatusCode finalHead)
    {
        using var transport = new Transport(head => head == 1 ? HttpStatusCode.NotFound : finalHead);
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => Upload(transport, S3ServiceType.CloudflareR2, true));
        if (finalHead == HttpStatusCode.OK)
            Assert.Equal(TransferFailureCategory.Conflict, failure.Failure.Category);
        else
            Assert.Equal(TransferFailureCategory.Authorization, failure.Failure.Category);
        Assert.Equal(2, transport.Heads);
        Assert.Equal(1, transport.Initiates);
        Assert.True(transport.Parts > 0);
        Assert.Empty(transport.Commits);
        Assert.Equal(1, transport.Aborts);
    }

    [Theory]
    [InlineData(S3ServiceType.AmazonS3, false)]
    [InlineData(S3ServiceType.AmazonS3, true)]
    [InlineData(S3ServiceType.MinIO, false)]
    [InlineData(S3ServiceType.MinIO, true)]
    [InlineData(S3ServiceType.CloudflareR2, false)]
    public async Task SupportedProvidersKeepAtomicCreateAndEditConditions(S3ServiceType provider, bool multipart)
    {
        using var transport = new Transport(_ => HttpStatusCode.Forbidden);
        await Upload(transport, provider, multipart);
        var create = Assert.Single(transport.Commits);
        Assert.Equal("*", create.NoneMatch);
        Assert.Null(create.Match);
        transport.Commits.Clear();
        await Upload(transport, provider, multipart, overwrite: true, expected: "\"original\"");
        var edit = Assert.Single(transport.Commits);
        Assert.Null(edit.NoneMatch);
        Assert.Equal("\"original\"", edit.Match);
        Assert.Equal(0, transport.Heads);
    }

    private static async Task Upload(Transport transport, S3ServiceType provider, bool multipart,
        bool overwrite = false, string? expected = null)
    {
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = File.OpenWrite(path)) file.SetLength(multipart ? 6 * 1024 * 1024 : 17);
            var profile = ConnectionProfile.CreatePreset(provider) with
            {
                Name = "isolated-upload-fallback", Endpoint = "https://storage.example.test", AccessKey = "test-access", SecretKey = "test-secret"
            };
            var options = new TransferExecutionOptions
            {
                MultipartThresholdBytes = 5 * 1024 * 1024, PartSizeBytes = 5 * 1024 * 1024,
                AllowDestinationOverwrite = overwrite, ExpectedDestinationETag = expected, RetainFailedTransferState = false
            };
            var context = new TransferOperationContext(options, new SharedTransferBandwidthLimiter(), null, null,
                _ => { }, (_, _, _, _) => Task.CompletedTask);
            var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
            await service.UploadFileAsync(profile, "bucket", "folder/file.bin", path, "STANDARD", context,
                TestContext.Current.CancellationToken);
        }
        finally { File.Delete(path); }
    }

    private sealed record Commit(string? NoneMatch, string? Match);

    private sealed class Transport : HttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        public int Heads;
        public int Initiates;
        public int Parts;
        public int Aborts;
        public ConcurrentBag<string> Paths { get; } = [];
        public ConcurrentBag<Commit> Commits { get; } = [];
        public Transport(Func<int, HttpStatusCode> headStatus) => _client = new HttpClient(new Handler(this, headStatus));
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
        public void Dispose() => _client.Dispose();

        private sealed class Handler(Transport owner, Func<int, HttpStatusCode> headStatus) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Paths.Add(request.RequestUri!.AbsolutePath);
                var query = request.RequestUri.Query;
                var status = HttpStatusCode.OK;
                var xml = "";
                if (request.Method == HttpMethod.Head)
                    status = headStatus(Interlocked.Increment(ref owner.Heads));
                else if (request.Method == HttpMethod.Delete)
                    Interlocked.Increment(ref owner.Aborts);
                else if (request.Method == HttpMethod.Post && query.Contains("uploads"))
                {
                    Interlocked.Increment(ref owner.Initiates);
                    xml = "<InitiateMultipartUploadResult><UploadId>test-upload</UploadId></InitiateMultipartUploadResult>";
                }
                else if (request.Method == HttpMethod.Put && query.Contains("partNumber"))
                    Interlocked.Increment(ref owner.Parts);
                else
                {
                    owner.Commits.Add(new(
                        request.Headers.TryGetValues("If-None-Match", out var none) ? none.Single() : null,
                        request.Headers.TryGetValues("If-Match", out var match) ? match.Single() : null));
                    if (request.Method == HttpMethod.Post)
                        xml = "<CompleteMultipartUploadResult><ETag>\"complete\"</ETag></CompleteMultipartUploadResult>";
                }
                var response = new HttpResponseMessage(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"test-etag\"");
                return Task.FromResult(response);
            }
        }
    }
}
