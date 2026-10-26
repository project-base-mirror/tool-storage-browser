using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MultipartInitiationTests
{
    [Fact]
    public async Task InvalidPartPlanMakesNoRemoteInitiateRequest()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
            var client = DispatchProxy.Create<IAmazonS3, UploadFake>();
            var fake = (UploadFake)(object)client;
            var context = Context() with { Options = new TransferExecutionOptions { PartSizeBytes = 1 } };
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => S3StorageService.UploadMultipartFileAsync(
                client, "bucket", "key", new FileInfo(path), "", ObjectWriteHeaders.Empty, context, TestContext.Current.CancellationToken));
            Assert.Equal(0, fake.Initiations);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NewUploadIsAbortedIfInitialCheckpointCannotBeSaved()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
            var client = DispatchProxy.Create<IAmazonS3, UploadFake>();
            var fake = (UploadFake)(object)client;
            var context = Context() with
            {
                UpdateCheckpointAsync = (_, _, _, _) => throw new IOException("checkpoint disk full")
            };
            await Assert.ThrowsAsync<IOException>(() => S3StorageService.UploadMultipartFileAsync(
                client, "bucket", "key", new FileInfo(path), "", ObjectWriteHeaders.Empty, context, TestContext.Current.CancellationToken));
            Assert.Equal(1, fake.Initiations);
            Assert.Equal("upload-id", fake.AbortedUpload);
        }
        finally { File.Delete(path); }
    }

    private static TransferOperationContext Context() =>
        new(new TransferExecutionOptions(), new SharedTransferBandwidthLimiter(), null, null, _ => { },
            (_, _, _, _) => Task.CompletedTask);

    public class UploadFake : DispatchProxy
    {
        public int Initiations;
        public string? AbortedUpload;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAmazonS3.InitiateMultipartUploadAsync))
            {
                Initiations++;
                return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload-id" });
            }
            if (targetMethod.Name == nameof(IAmazonS3.AbortMultipartUploadAsync))
            {
                AbortedUpload = ((AbortMultipartUploadRequest)args![0]!).UploadId;
                return Task.FromResult(new AbortMultipartUploadResponse());
            }
            if (targetMethod.Name == nameof(IDisposable.Dispose)) return null;
            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
