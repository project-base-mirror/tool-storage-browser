using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    internal static async Task VerifyBoundedDownloadEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new TransferExecutionException(new TransferFailureInfo(
                "响应超过预览下载上限，拒绝把截断内容作为完整对象。", TransferFailureCategory.Validation, Retryable: false));
    }

    internal static async Task<GetObjectMetadataResponse> GetDownloadMetadataAsync(
        IAmazonS3 client, S3ServiceType provider, string bucket, string key, string? version,
        CancellationToken cancellationToken)
    {
        var request = new GetObjectMetadataRequest { BucketName = bucket, Key = key, VersionId = version };
        if (provider is S3ServiceType.AmazonS3 or S3ServiceType.MinIO)
            request.ChecksumMode = ChecksumMode.ENABLED;
        try
        {
            return await client.GetObjectMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (request.ChecksumMode is not null &&
            exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented)
        {
            // KMS checksum permission and compatible-server support can differ from GET permission.
            // Retry metadata without checksum mode, not the data request, and never label this verified.
            request.ChecksumMode = null;
            return await client.GetObjectMetadataAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static RemoteObjectIdentity CreateDownloadIdentity(GetObjectMetadataResponse metadata)
    {
        var full = string.Equals(metadata.ChecksumType?.Value, "FULL_OBJECT", StringComparison.Ordinal);
        var composite = string.Equals(metadata.ChecksumType?.Value, "COMPOSITE", StringComparison.Ordinal);
        return new RemoteObjectIdentity(metadata.ContentLength, metadata.ETag, metadata.VersionId)
        {
            FullObjectSha256 = full ? metadata.ChecksumSHA256 : null,
            FullObjectSha1 = full ? metadata.ChecksumSHA1 : null,
            FullObjectCrc32 = full ? metadata.ChecksumCRC32 : null,
            IntegrityUnavailableReason = composite
                ? "COMPOSITE 校验值不能作为完整文件哈希验证（仅验证长度与对象身份）"
                : !string.IsNullOrWhiteSpace(metadata.ChecksumCRC64NVME) || !string.IsNullOrWhiteSpace(metadata.ChecksumCRC32C)
                    ? "CRC32C/CRC64NVME 尚未启用应用级验证（仅验证长度与对象身份）"
                    : "无受支持的完整对象 SHA-256/SHA-1/CRC32 校验值（仅验证长度与对象身份）"
        };
    }

    private static async Task<string> VerifyDownloadBeforeCommitAsync(
        string temporaryPath, RemoteObjectIdentity remote, DownloadCheckpoint checkpoint,
        TransferOperationContext context, CancellationToken cancellationToken)
    {
        try { return await VerifyFullObjectChecksumAsync(temporaryPath, remote, cancellationToken).ConfigureAwait(false); }
        catch (TransferExecutionException exception) when (exception.Failure.Category == TransferFailureCategory.Validation)
        {
            // Persist invalidation before destroying data so a manual retry cannot trust a failed full bitmap.
            var invalidated = checkpoint with
            {
                CompletedBytes = 0, CompletedSegments = [], IntegritySummary = exception.Failure.Message
            };
            await context.UpdateCheckpointAsync(0, invalidated, context.MultipartCheckpoint, cancellationToken).ConfigureAwait(false);
            File.Delete(temporaryPath);
            throw;
        }
    }

    internal static async Task<string> VerifyFullObjectChecksumAsync(
        string temporaryPath, RemoteObjectIdentity remote, CancellationToken cancellationToken)
    {
        var (expected, algorithm) = !string.IsNullOrWhiteSpace(remote.FullObjectSha256)
            ? (remote.FullObjectSha256, "SHA-256")
            : !string.IsNullOrWhiteSpace(remote.FullObjectSha1)
                ? (remote.FullObjectSha1, "SHA-1") : (remote.FullObjectCrc32, "CRC32");
        if (string.IsNullOrWhiteSpace(expected)) return remote.IntegrityUnavailableReason;
        await using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using HashAlgorithm hash = algorithm switch
        {
            "SHA-256" => SHA256.Create(),
            "SHA-1" => SHA1.Create(),
            _ => new Amazon.Runtime.Internal.Util.Crc32Managed()
        };
        var actual = Convert.ToBase64String(await hash.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new TransferExecutionException(new TransferFailureInfo(
                $"完整性校验失败：下载文件 {algorithm} 与服务端 FULL_OBJECT 校验值不符。", TransferFailureCategory.Validation, Retryable: false));
        return $"已验证 FULL_OBJECT {algorithm}";
    }
}
