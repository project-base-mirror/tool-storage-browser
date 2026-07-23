namespace S3Explorer.Core;

public sealed record BucketInfo(string Name, DateTimeOffset? CreatedAt, string? Region = null);

public sealed record S3ObjectEntry(
    string Key,
    string Name,
    long Size,
    bool IsDirectory,
    DateTimeOffset? LastModified,
    string StorageClass,
    string? ETag = null,
    string? VersionId = null,
    string? Owner = null,
    string? ContentType = null);

public sealed record PagedObjectResult(
    IReadOnlyList<S3ObjectEntry> Items,
    string? ContinuationToken,
    bool HasMore);

public sealed record ConnectionTestResult(
    bool Success,
    TimeSpan Elapsed,
    int BucketCount,
    string Message,
    int? HttpStatusCode = null,
    string? ErrorCode = null,
    string? RequestId = null);

public sealed record ObjectProperties(
    string Bucket,
    string Key,
    long Size,
    DateTimeOffset? LastModified,
    string? ETag,
    string? ContentType,
    string? StorageClass,
    string? VersionId,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record TransferProgress(long TransferredBytes, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(TransferredBytes * 100d / TotalBytes, 0, 100);
}

public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

public interface IProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default);
}

public interface IS3StorageService
{
    Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task CreateBucketAsync(ConnectionProfile profile, string bucket, string region, CancellationToken cancellationToken);
    Task DeleteEmptyBucketAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<PagedObjectResult> ListObjectsAsync(ConnectionProfile profile, string bucket, string prefix, string? continuationToken, int pageSize, CancellationToken cancellationToken);
    Task UploadFileAsync(ConnectionProfile profile, string bucket, string key, string localPath, string storageClass, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    Task DownloadFileAsync(ConnectionProfile profile, string bucket, string key, string localPath, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    Task CreateFolderAsync(ConnectionProfile profile, string bucket, string folderKey, CancellationToken cancellationToken);
    Task DeleteObjectsAsync(ConnectionProfile profile, string bucket, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
    Task CopyObjectAsync(ConnectionProfile profile, string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken);
    Task MoveObjectAsync(ConnectionProfile profile, string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken);
    Task<ObjectProperties> GetObjectPropertiesAsync(ConnectionProfile profile, string bucket, string key, CancellationToken cancellationToken);
    string CreatePresignedUrl(ConnectionProfile profile, string bucket, string key, TimeSpan lifetime);
}
