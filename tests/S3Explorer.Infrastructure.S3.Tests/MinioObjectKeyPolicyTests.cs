using System.Net;
using Amazon.Runtime;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioObjectKeyPolicyTests
{
    [Theory]
    [InlineData("/leading")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("folder//file")]
    [InlineData("folder/./file")]
    [InlineData("folder/../file")]
    [InlineData("folder/ .. /file")]
    [InlineData("folder\\..\\file")]
    public void UnsupportedMinioKeysFailClosedWithoutNormalizing(string key)
    {
        var failure = Assert.Throws<TransferExecutionException>(() => MinioObjectKeyPolicy.Validate(key));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.Contains("MinIO", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("folder/")]
    [InlineData("folder/file")]
    [InlineData(" ")]
    [InlineData("...")]
    [InlineData("folder/\u00a0./file")]
    [InlineData("folder/\u2003../file")]
    [InlineData("literal%2F%20%252F")]
    [InlineData("back\\slash.txt")]
    [InlineData("a/中文 + #?.txt")]
    public void RepresentableKeysAreNotTrimmedOrRejected(string key) => MinioObjectKeyPolicy.Validate(key);

    [Theory]
    [InlineData(S3ServiceType.AmazonS3)]
    [InlineData(S3ServiceType.CloudflareR2)]
    public void KnownNonMinioProvidersRetainTheirOpaqueKeyBehavior(S3ServiceType provider)
    {
        Assert.False(MinioObjectKeyPolicy.AppliesTo(provider));
        MinioObjectKeyPolicy.Validate(provider, "/leading//../key");
        MinioObjectKeyPolicy.ValidateRequest(new DeleteObjectsRequest
        {
            Objects = [new KeyVersion { Key = "/leading//../key" }]
        }, provider);
    }

    [Fact]
    public void UnknownProviderReasonDoesNotClaimItIsMinio()
    {
        var failure = Assert.Throws<TransferExecutionException>(() =>
            MinioObjectKeyPolicy.Validate(S3ServiceType.Custom, "/leading"));
        Assert.Contains("尚未验证", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MinIO", failure.Message, StringComparison.Ordinal);
        Assert.True(MinioObjectKeyPolicy.AppliesTo((S3ServiceType)999));
    }

    [Fact]
    public void EveryAddressedObjectRequestRejectsUnsafeKeys()
    {
        AmazonWebServiceRequest[] requests =
        [
            new GetObjectRequest { Key = "/key" },
            new GetObjectMetadataRequest { Key = "/key" },
            new GetObjectAttributesRequest { Key = "/key" },
            new PutObjectRequest { Key = "/key" },
            new DeleteObjectRequest { Key = "/key" },
            new DeleteObjectsRequest { Objects = [new KeyVersion { Key = "valid" }, new KeyVersion { Key = "/key" }] },
            new GetObjectAclRequest { Key = "/key" },
            new PutObjectAclRequest { Key = "/key" },
            new GetObjectTaggingRequest { Key = "/key" },
            new PutObjectTaggingRequest { Key = "/key" },
            new DeleteObjectTaggingRequest { Key = "/key" },
            new GetObjectRetentionRequest { Key = "/key" },
            new PutObjectRetentionRequest { Key = "/key" },
            new GetObjectLegalHoldRequest { Key = "/key" },
            new PutObjectLegalHoldRequest { Key = "/key" },
            new InitiateMultipartUploadRequest { Key = "/key" },
            new UploadPartRequest { Key = "/key" },
            new CompleteMultipartUploadRequest { Key = "/key" },
            new AbortMultipartUploadRequest { Key = "/key" },
            new ListPartsRequest { Key = "/key" },
            new RestoreObjectRequest { Key = "/key" },
            new SelectObjectContentRequest { Key = "/key" },
            new GetPreSignedUrlRequest { Key = "/key" },
            new CopyObjectRequest { SourceKey = "/key", DestinationKey = "valid" },
            new CopyObjectRequest { SourceKey = "valid", DestinationKey = "/key" },
            new CopyPartRequest { SourceKey = "/key", DestinationKey = "valid" },
            new CopyPartRequest { SourceKey = "valid", DestinationKey = "/key" }
        ];
        foreach (var request in requests)
        {
            var failure = Assert.Throws<TransferExecutionException>(() => MinioObjectKeyPolicy.ValidateRequest(request));
            Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
            Assert.False(failure.Failure.Retryable);
        }
    }

    [Fact]
    public void PrefixDiscoveryDoesNotPretendToAddressAnObject()
    {
        MinioObjectKeyPolicy.ValidateRequest(new ListObjectsV2Request { Prefix = "/prefix//" });
        MinioObjectKeyPolicy.ValidateRequest(new ListVersionsRequest { Prefix = "/prefix//" });
        MinioObjectKeyPolicy.ValidateRequest(new ListMultipartUploadsRequest { Prefix = "/prefix//" });
    }

    [Theory]
    [InlineData(S3ServiceType.MinIO, true)]
    [InlineData(S3ServiceType.MinIO, false)]
    [InlineData(S3ServiceType.Custom, true)]
    [InlineData(S3ServiceType.Custom, false)]
    public async Task FullDeleteSelectionIsValidatedBeforeFirstBatchOrSingleDelete(S3ServiceType provider, bool multiDelete)
    {
        using var transport = new CaptureTransport();
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var keys = Enumerable.Range(0, 1000).Select(index => $"valid-{index}").Append("/unsafe").ToArray();
        var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => service.DeleteObjectsAsync(
            Profile(provider) with { EnableMultiObjectDelete = multiDelete }, "bucket", keys,
            TestContext.Current.CancellationToken));
        Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
        Assert.False(failure.Failure.Retryable);
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task FullVersionSelectionIsValidatedBeforeFirstBatch()
    {
        using var transport = new CaptureTransport();
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        var versions = Enumerable.Range(0, 1000).Select(index => new ObjectVersionIdentity($"valid-{index}", "version"))
            .Append(new ObjectVersionIdentity("/unsafe", "version")).ToArray();
        await Assert.ThrowsAsync<TransferExecutionException>(() => service.DeleteObjectVersionsAsync(
            Profile(S3ServiceType.MinIO), "bucket", versions, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData("/unsafe", "safe")]
    [InlineData("safe", "/unsafe")]
    public async Task CopyValidatesBothKeysBeforeReadingSource(string source, string destination)
    {
        using var transport = new CaptureTransport();
        var service = new S3StorageService(new S3ClientFactory { HttpClientFactoryOverride = transport });
        await Assert.ThrowsAsync<TransferExecutionException>(() => service.CopyObjectAsync(
            Profile(S3ServiceType.MinIO), "bucket", source, "bucket", destination, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Requests);
    }

    private static ConnectionProfile Profile(S3ServiceType provider) => ConnectionProfile.CreatePreset(provider) with
    {
        Name = "isolated key policy", Endpoint = "http://127.0.0.1:1", UseHttps = false,
        AccessKey = "test", SecretKey = "test", AddressingStyle = AddressingStyle.PathStyle
    };

    private sealed class CaptureTransport : HttpClientFactory, IDisposable
    {
        private HttpClient? _client;
        public int Requests;
        public override HttpClient CreateHttpClient(IClientConfig config) => _client ??= new HttpClient(new Handler(this));
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => false;
        public void Dispose() => _client?.Dispose();
        private sealed class Handler(CaptureTransport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<DeleteResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\" />")
                });
            }
        }
    }
}
