using System.Net;
using Amazon.Runtime;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class NativeMetadataResponseTests
{
    [Theory]
    [InlineData(S3ServiceType.AliyunOss, "x-oss-")]
    [InlineData(S3ServiceType.TencentCos, "x-cos-")]
    public async Task NativeHeadAliasesPreserveMetadataAndProtectedProperties(S3ServiceType provider, string prefix)
    {
        using var transport = new Transport(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = 42;
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain");
            response.Headers.TryAddWithoutValidation("ETag", "\"etag\"");
            foreach (var pair in new Dictionary<string, string>
            {
                ["meta-padded"] = " value ", ["storage-class"] = "STANDARD_IA", ["version-id"] = "v1",
                ["object-type"] = "Appendable", ["server-side-encryption"] = "AES256",
                ["server-side-encryption-customer-key-md5"] = "key-hash", ["object-lock-mode"] = "COMPLIANCE",
                ["object-lock-retain-until-date"] = "2027-01-01T00:00:00Z", ["object-lock-legal-hold"] = "ON"
            }) response.Headers.TryAddWithoutValidation(prefix + pair.Key, pair.Value);
            return response;
        });
        var profile = ConnectionProfile.CreatePreset(provider) with
        { Name = "test", AccessKey = "test", SecretKey = "test", AddressingStyle = AddressingStyle.PathStyle };
        var factory = new S3ClientFactory { HttpClientFactoryOverride = transport };
        var properties = await new S3StorageService(factory).GetObjectPropertiesAsync(profile, "bucket", "key", TestContext.Current.CancellationToken);
        Assert.Equal(" value ", properties.Metadata["padded"]);
        Assert.Equal("STANDARD_IA", properties.StorageClass);
        Assert.Equal("v1", properties.VersionId);
        Assert.Equal("Appendable", properties.ProviderObjectType);
        Assert.Equal("AES256", properties.ServerSideEncryptionMethod);
        Assert.Equal("SSE-C", properties.CustomerEncryptionMethod);
        Assert.Equal("COMPLIANCE", properties.ObjectLockMode);
        Assert.Equal("2027-01-01T00:00:00Z", properties.ProviderRetentionUntil);
        Assert.Equal(DateTimeOffset.Parse("2027-01-01T00:00:00Z"), properties.ObjectLockRetainUntilDate);
        Assert.Equal("ON", properties.ObjectLockLegalHoldStatus);
        Assert.False(ObjectMetadataRewritePolicy.For(provider, properties).Supported);
    }

    [Fact]
    public async Task ConflictingMetadataAliasesAreNotSilentlyAccepted()
    {
        using var transport = new Transport(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("x-amz-meta-same", "one");
            response.Headers.TryAddWithoutValidation("x-cos-meta-same", "two");
            return response;
        });
        var profile = ConnectionProfile.CreatePreset(S3ServiceType.TencentCos) with { Name = "test", AccessKey = "test", SecretKey = "test" };
        var properties = await new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport })
            .GetObjectPropertiesAsync(profile, "bucket", "key", TestContext.Current.CancellationToken);
        Assert.True(properties.MissingMetadataCount > 0);
    }

    private sealed class Transport(Func<HttpResponseMessage> response) : HttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public override HttpClient CreateHttpClient(IClientConfig config) => _client ??= new HttpClient(new Handler(response));
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public void Dispose() => _client?.Dispose();
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response());
    }
}
