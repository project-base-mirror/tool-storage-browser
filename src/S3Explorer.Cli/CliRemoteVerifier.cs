using S3Explorer.Core;

namespace S3Explorer.Cli;

internal static class CliRemoteVerifier
{
    public static async Task VerifyAsync(
        IS3StorageService storage,
        ConnectionProfile profile,
        string bucket,
        string key,
        long expectedSize,
        string expectedSha256,
        CliTransferRuntime transfer,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"s3explorer-verify-{Guid.NewGuid():N}.tmp");
        try
        {
            await storage.DownloadFileAsync(
                profile, bucket, key, temporaryPath, transfer.CreateContext(), cancellationToken);
            var info = new FileInfo(temporaryPath);
            if (info.Length != expectedSize)
                throw new InvalidDataException(
                    $"远程大小不匹配：预期 {expectedSize}，实际 {info.Length}。");
            var hash = await PublishManifestUtility.ComputeSha256Async(temporaryPath, cancellationToken);
            if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"远程 SHA-256 不匹配：预期 {expectedSha256}，实际 {hash}。");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
