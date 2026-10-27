using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Model.Internal.MarshallTransformations;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class ConditionalEditTests
{
    [Fact]
    public async Task PreviewLimitNeverAcceptsSilentlyTruncatedResponse()
    {
        using var ended = new MemoryStream();
        await S3StorageService.VerifyBoundedDownloadEndAsync(ended, TestContext.Current.CancellationToken);
        using var oversized = new MemoryStream(new byte[] { 1 });
        var error = await Assert.ThrowsAsync<TransferExecutionException>(() =>
            S3StorageService.VerifyBoundedDownloadEndAsync(oversized, TestContext.Current.CancellationToken));
        Assert.False(error.Failure.Retryable);
    }

    [Fact]
    public void EditedAes256ObjectExplicitlyRetainsEncryptionOnWire()
    {
        var options = new TransferExecutionOptions { ExpectedDestinationETag = "etag", RequireSseS3Encryption = true };
        var encryption = S3StorageService.RequiredUploadEncryption(options);
        var request = PutObjectRequestMarshaller.Instance.Marshall(new PutObjectRequest
        {
            BucketName = "bucket", Key = "key", IfMatch = options.ExpectedDestinationETag,
            ServerSideEncryptionMethod = encryption
        });
        Assert.Equal("AES256", request.Headers["x-amz-server-side-encryption"]);
        Assert.Equal("etag", request.Headers["If-Match"]);
        Assert.Null(S3StorageService.RequiredUploadEncryption(new TransferExecutionOptions()));
    }

    [Fact]
    public void HeadProtectionFieldsRemainVisibleToEditSafetyPolicy()
    {
        var properties = S3StorageService.CreateObjectProperties("bucket", "key", new GetObjectMetadataResponse
        {
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
            ServerSideEncryptionKeyManagementServiceKeyId = "test-key-id",
            ServerSideEncryptionCustomerMethod = "AES256",
            ObjectLockMode = new ObjectLockMode("GOVERNANCE"),
            ObjectLockLegalHoldStatus = new ObjectLockLegalHoldStatus("ON")
        });
        Assert.Equal("aws:kms", properties.ServerSideEncryptionMethod);
        Assert.Equal("test-key-id", properties.EncryptionKeyId);
        Assert.Equal("AES256", properties.CustomerEncryptionMethod);
        Assert.Equal("GOVERNANCE", properties.ObjectLockMode);
        Assert.Equal("ON", properties.ObjectLockLegalHoldStatus);
    }

    [Fact]
    public void ConditionalReplacementAndCreateOnlyCannotBeCombined()
    {
        Assert.Throws<ArgumentException>(() => new TransferExecutionOptions
            { ExpectedDestinationETag = "etag", AllowDestinationOverwrite = false }.Validate());
        Assert.Throws<ArgumentException>(() => new TransferExecutionOptions { ExpectedDestinationETag = " " }.Validate());
        new TransferExecutionOptions { ExpectedDestinationETag = "\"etag\"" }.Validate();
    }

    [Fact]
    public void SdkSerializesMatchForPutAndMultipartComplete()
    {
        var put = PutObjectRequestMarshaller.Instance.Marshall(new PutObjectRequest
            { BucketName = "bucket", Key = "key", IfMatch = "\"etag\"" });
        var complete = CompleteMultipartUploadRequestMarshaller.Instance.Marshall(new CompleteMultipartUploadRequest
            { BucketName = "bucket", Key = "key", UploadId = "upload", IfMatch = "\"etag\"", PartETags = [] });
        Assert.Equal("\"etag\"", put.Headers["If-Match"]);
        Assert.Equal("\"etag\"", complete.Headers["If-Match"]);
    }

    [Fact]
    public async Task UnsupportedProviderConditionalEditFailsBeforeNetwork()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "edit", TestContext.Current.CancellationToken);
            var context = new TransferOperationContext(new TransferExecutionOptions { ExpectedDestinationETag = "etag" },
                new SharedTransferBandwidthLimiter(), null, null, _ => { }, (_, _, _, _) => Task.CompletedTask);
            var service = new S3StorageService(new S3ClientFactory());
            var failure = await Assert.ThrowsAsync<TransferExecutionException>(() => service.UploadFileAsync(
                ConnectionProfile.CreatePreset(S3ServiceType.Custom), "bucket", "key", path, "STANDARD", context,
                TestContext.Current.CancellationToken));
            Assert.Equal(TransferFailureCategory.Validation, failure.Failure.Category);
            Assert.False(failure.Failure.Retryable);
        }
        finally { File.Delete(path); }
    }
}
