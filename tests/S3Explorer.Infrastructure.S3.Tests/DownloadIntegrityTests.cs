using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class DownloadIntegrityTests
{
    [Fact]
    public async Task Crc32UsesStandardBigEndianKnownVectorAndRejectsMismatch()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "123456789", TestContext.Current.CancellationToken);
            // Standard CRC32/ISO-HDLC check value CB F4 39 26, base64 big-endian.
            var remote = new RemoteObjectIdentity(9, null, null) { FullObjectCrc32 = "y/Q5Jg==" };
            Assert.Equal("已验证 FULL_OBJECT CRC32", await S3StorageService.VerifyFullObjectChecksumAsync(path, remote, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<TransferExecutionException>(() => S3StorageService.VerifyFullObjectChecksumAsync(path,
                remote with { FullObjectCrc32 = "AAAAAA==" }, TestContext.Current.CancellationToken));
            Assert.Contains("CRC32C/CRC64NVME", S3StorageService.CreateDownloadIdentity(new GetObjectMetadataResponse
                { ChecksumType = ChecksumType.FULL_OBJECT, ChecksumCRC64NVME = "opaque" }).IntegrityUnavailableReason);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatesOnlyExplicitFullObjectHashes(bool sha1)
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var metadata = new GetObjectMetadataResponse { ContentLength = bytes.Length, ChecksumType = ChecksumType.FULL_OBJECT };
            if (sha1) metadata.ChecksumSHA1 = Convert.ToBase64String(SHA1.HashData(bytes));
            else metadata.ChecksumSHA256 = Convert.ToBase64String(SHA256.HashData(bytes));
            var identity = S3StorageService.CreateDownloadIdentity(metadata);
            Assert.StartsWith("已验证", await S3StorageService.VerifyFullObjectChecksumAsync(path, identity, TestContext.Current.CancellationToken));
            metadata.ChecksumType = ChecksumType.COMPOSITE;
            Assert.Contains("COMPOSITE", await S3StorageService.VerifyFullObjectChecksumAsync(path,
                S3StorageService.CreateDownloadIdentity(metadata), TestContext.Current.CancellationToken));
            Assert.Contains("无受支持", await S3StorageService.VerifyFullObjectChecksumAsync(path,
                S3StorageService.CreateDownloadIdentity(new GetObjectMetadataResponse()), TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task ExplicitChecksumMetadataFallbackNeverClaimsVerified(HttpStatusCode status)
    {
        using var client = new MetadataClient(status);
        var metadata = await S3StorageService.GetDownloadMetadataAsync(client, S3ServiceType.AmazonS3,
            "bucket", "key", "version", TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Calls);
        Assert.True(client.ChecksumRequested);
        Assert.Null(S3StorageService.CreateDownloadIdentity(metadata).FullObjectSha256);
    }

    private sealed class MetadataClient(HttpStatusCode status) : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" })
    {
        public int Calls { get; private set; }
        public bool ChecksumRequested { get; private set; }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal("version", request.VersionId);
            if (Calls == 1)
            {
                ChecksumRequested = request.ChecksumMode == ChecksumMode.ENABLED;
                throw new AmazonS3Exception("checksum unavailable") { StatusCode = status };
            }
            Assert.Null(request.ChecksumMode);
            return Task.FromResult(new GetObjectMetadataResponse());
        }
    }
}
