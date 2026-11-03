using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class R2UploadProtocolTests
{
    [Theory]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Auto, 0)]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Auto, 137)]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Auto, 11 * 1024 * 1024)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.UnsignedPayload, 0)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.UnsignedPayload, 137)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.UnsignedPayload, 11 * 1024 * 1024)]
    [InlineData(S3ServiceType.MinIO, UploadSigningMode.UnsignedPayload, 137)]
    [InlineData(S3ServiceType.AmazonS3, UploadSigningMode.UnsignedPayload, 137)]
    [InlineData(S3ServiceType.AmazonS3, UploadSigningMode.UnsignedPayload, 11 * 1024 * 1024)]
    public async Task UnsignedUploadsKeepAuthenticationAndExplicitChecksumsOnActualSdkRequests(
        S3ServiceType type, UploadSigningMode mode, int length)
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        try
        {
            await File.WriteAllBytesAsync(path, bytes, token);
            using var recording = new RecordingHttpFactory();
            var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = recording });
            await service.UploadFileAsync(Profile() with { ServiceType = type, UploadSigningMode = mode }, "bucket", "目录/file + %.bin", path, "STANDARD", Context(), token);
            var uploads = recording.Requests.Where(request => request.Method == "PUT").OrderBy(request => request.Part).ToArray();
            Assert.Equal(length >= 5 * 1024 * 1024 ? 3 : 1, uploads.Length);
            Assert.Equal(bytes, uploads.SelectMany(request => request.Bytes).ToArray());
            foreach (var upload in uploads)
            {
                Assert.Equal("UNSIGNED-PAYLOAD", upload.PayloadHash);
                Assert.Equal(Convert.ToBase64String(MD5.HashData(upload.Bytes)), upload.Md5);
                Assert.DoesNotContain("aws-chunked", upload.Encoding);
                Assert.Equal(upload.Bytes.LongLength, upload.ContentLength);
                Assert.True(upload.Authenticated);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.UnsignedPayload)]
    public async Task ConcurrentSmallFilesAndFolderMarkersUseSameCompatibilityPolicy(S3ServiceType type, UploadSigningMode mode)
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "isolated-batch-content", token);
            using var recording = new RecordingHttpFactory();
            var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = recording });
            var profile = Profile() with { ServiceType = type, UploadSigningMode = mode };
            await Task.WhenAll(Enumerable.Range(0, 4).Select(index => service.UploadFileAsync(
                profile, "bucket", $"batch/{index}.txt", path, "STANDARD", Context(), token)));
            await service.CreateFolderAsync(profile, "bucket", "empty/", token);
            Assert.Equal(5, recording.Requests.Count);
            Assert.All(recording.Requests, request =>
            {
                Assert.Equal("UNSIGNED-PAYLOAD", request.PayloadHash);
                Assert.Equal(Convert.ToBase64String(MD5.HashData(request.Bytes)), request.Md5);
                Assert.DoesNotContain("aws-chunked", request.Encoding);
            });
            Assert.Single(recording.Requests, request => request.Bytes.Length == 0);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(S3ServiceType.AmazonS3, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.MinIO, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.AliyunOss, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.TencentCos, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.BackblazeB2, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.SupabaseStorage, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.GoogleCloudStorage, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.AmazonS3, UploadSigningMode.Streaming)]
    [InlineData(S3ServiceType.MinIO, UploadSigningMode.Streaming)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.Streaming)]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Streaming)]
    public async Task DefaultAndExplicitStreamingModesKeepSdkSigningAndChecksums(S3ServiceType type, UploadSigningMode mode)
    {
        var put = new PutObjectRequest();
        var part = new UploadPartRequest();
        using var stream = new MemoryStream([1, 2, 3]);
        var profile = Profile() with { ServiceType = type, UploadSigningMode = mode };
        await S3UploadCompatibility.PrepareAsync(profile, put, stream, TestContext.Current.CancellationToken);
        await S3UploadCompatibility.PrepareAsync(profile, part, stream, TestContext.Current.CancellationToken);
        Assert.True(put.UseChunkEncoding);
        Assert.Null(put.DisablePayloadSigning);
        Assert.Null(put.DisableDefaultChecksumValidation);
        Assert.Null(put.MD5Digest);
        Assert.True(part.UseChunkEncoding);
        Assert.Null(part.DisablePayloadSigning);
        Assert.Null(part.DisableDefaultChecksumValidation);
        Assert.Null(part.MD5Digest);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ChecksumPassRestoresPartPositionAndHonorsCancellation()
    {
        using var stream = new MemoryStream([0, 1, 2, 3]);
        stream.Position = 1;
        var request = new UploadPartRequest();
        await S3UploadCompatibility.PrepareAsync(Profile(), request, stream, TestContext.Current.CancellationToken);
        Assert.Equal(1, stream.Position);
        Assert.Equal(Convert.ToBase64String(MD5.HashData([1, 2, 3])), request.MD5Digest);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            S3UploadCompatibility.PrepareAsync(Profile(), new PutObjectRequest(), stream, cancelled.Token));
        Assert.Equal(1, stream.Position);
    }

    [Theory]
    [InlineData(S3ServiceType.CloudflareR2, UploadSigningMode.Auto)]
    [InlineData(S3ServiceType.Custom, UploadSigningMode.UnsignedPayload)]
    public async Task DoesNotSendUnsignedPayloadOverHttp(S3ServiceType type, UploadSigningMode mode)
    {
        using var stream = new MemoryStream([1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => S3UploadCompatibility.PrepareAsync(
            Profile() with { ServiceType = type, UploadSigningMode = mode, Endpoint = "http://storage.example.test", UseHttps = false }, new PutObjectRequest(), stream,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(137)]
    [InlineData(11 * 1024 * 1024)]
    public async Task HttpUnsignedUploadsAreRejectedBeforeAnyRemoteRequest(int length)
    {
        var path = Path.GetTempFileName();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllBytesAsync(path, new byte[length], token);
            using var recording = new RecordingHttpFactory();
            var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = recording });
            var profile = Profile() with
            {
                ServiceType = S3ServiceType.Custom, UploadSigningMode = UploadSigningMode.UnsignedPayload,
                Endpoint = "http://storage.example.test"
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadFileAsync(
                profile, "bucket", "blocked", path, "STANDARD", Context(), token));
            Assert.Empty(recording.Requests);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UnknownModesAreRejectedBeforePreparingUploads()
    {
        var profile = Profile() with { UploadSigningMode = (UploadSigningMode)123 };
        using var source = new MemoryStream([1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => S3UploadCompatibility.ValidateEndpoint(profile));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            S3UploadCompatibility.PrepareAsync(profile, new PutObjectRequest(), source, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            S3UploadCompatibility.PrepareAsync(profile, new UploadPartRequest(), source, TestContext.Current.CancellationToken));
    }

    private static ConnectionProfile Profile() => ConnectionProfile.CreatePreset(S3ServiceType.CloudflareR2) with
    {
        Name = "isolated-r2", Endpoint = "https://storage.example.test", AccessKey = "fake-access", SecretKey = "fake-secret"
    };

    private static TransferOperationContext Context() => new(
        new TransferExecutionOptions { MultipartThresholdBytes = 5 * 1024 * 1024, PartSizeBytes = 5 * 1024 * 1024 },
        new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);

    private sealed record CapturedRequest(string Method, int Part, byte[] Bytes, string PayloadHash,
        string? Md5, string Encoding, long? ContentLength, bool Authenticated);

    private sealed class RecordingHttpFactory : HttpClientFactory, IDisposable
    {
        public ConcurrentBag<CapturedRequest> Requests { get; } = [];
        private readonly HttpClient _client;
        public RecordingHttpFactory() => _client = new HttpClient(new RecordingHandler(Requests));
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
        public void Dispose() => _client.Dispose();
    }

    private sealed class RecordingHandler(ConcurrentBag<CapturedRequest> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var query = request.RequestUri!.Query;
            var partValue = query.TrimStart('?').Split('&').FirstOrDefault(value => value.StartsWith("partNumber="))?.Split('=')[1];
            var part = partValue is null ? 0 : int.Parse(partValue);
            requests.Add(new(request.Method.Method, part, bytes,
                request.Headers.TryGetValues("x-amz-content-sha256", out var hash) ? hash.Single() : "",
                request.Content?.Headers.ContentMD5 is { } md5 ? Convert.ToBase64String(md5) : null,
                string.Join(",", request.Content?.Headers.ContentEncoding ?? []), request.Content?.Headers.ContentLength,
                request.Headers.NonValidated.TryGetValues("Authorization", out var authorization) &&
                authorization.ToString().StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal)));
            var xml = request.Method == HttpMethod.Post && query.Contains("uploads")
                ? "<InitiateMultipartUploadResult><Bucket>bucket</Bucket><Key>key</Key><UploadId>fake-upload</UploadId></InitiateMultipartUploadResult>"
                : request.Method == HttpMethod.Post
                    ? "<CompleteMultipartUploadResult><Bucket>bucket</Bucket><Key>key</Key><ETag>\"completed\"</ETag></CompleteMultipartUploadResult>"
                    : "";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"" + Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant() + "\"");
            return response;
        }
    }
}
