namespace S3Explorer.Core;

public sealed record BucketInfo(
    string Name,
    DateTimeOffset? CreatedAt,
    string? Region = null,
    bool IsConfigured = false);

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

public sealed record ObjectVersionEntry(
    string Key,
    string VersionId,
    bool IsLatest,
    bool IsDeleteMarker,
    long Size,
    DateTimeOffset? LastModified,
    string? ETag,
    string StorageClass);

public sealed record ObjectVersionIdentity(string Key, string VersionId);

public sealed record PagedObjectVersionResult(
    IReadOnlyList<ObjectVersionEntry> Items,
    string? NextKeyMarker,
    string? NextVersionIdMarker,
    bool HasMore);

public enum ObjectAclMode
{
    Private,
    PublicRead
}

public sealed record ConnectionTestResult(
    bool Success,
    TimeSpan Elapsed,
    int BucketCount,
    string Message,
    int? HttpStatusCode = null,
    string? ErrorCode = null,
    string? RequestId = null,
    string? CredentialSource = null,
    AwsIdentitySummary? AwsIdentity = null);

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

    async Task<ConnectionProfileConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default) =>
        new(await LoadAsync(cancellationToken).ConfigureAwait(false), []);

    Task SaveConfigurationAsync(
        ConnectionProfileConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SaveAsync(configuration.Profiles, cancellationToken);
}

public interface IS3StorageService
{
    Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task CreateBucketAsync(ConnectionProfile profile, string bucket, string region, CancellationToken cancellationToken);
    Task DeleteEmptyBucketAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<BucketPropertiesSnapshot> GetBucketPropertiesAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<string?> GetBucketPolicyAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketPolicyAsync(ConnectionProfile profile, string bucket, string policyJson, CancellationToken cancellationToken);
    Task DeleteBucketPolicyAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<BucketAclSnapshot> GetBucketAclAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketAclAsync(ConnectionProfile profile, string bucket, BucketAclMode mode, CancellationToken cancellationToken);
    Task<BucketPublicAccessBlockSnapshot?> GetBucketPublicAccessBlockAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketPublicAccessBlockAsync(ConnectionProfile profile, string bucket, BucketPublicAccessBlockSnapshot configuration, CancellationToken cancellationToken);
    Task<BucketObjectOwnershipMode?> GetBucketObjectOwnershipAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketObjectOwnershipAsync(ConnectionProfile profile, string bucket, BucketObjectOwnershipMode mode, CancellationToken cancellationToken);
    Task<BucketCorsConfiguration> GetBucketCorsAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketCorsAsync(ConnectionProfile profile, string bucket, BucketCorsConfiguration configuration, CancellationToken cancellationToken);
    Task DeleteBucketCorsAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<BucketVersioningState> GetBucketVersioningAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketVersioningAsync(ConnectionProfile profile, string bucket, BucketVersioningState state, CancellationToken cancellationToken);
    Task<BucketEncryptionConfiguration> GetBucketEncryptionAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketEncryptionAsync(ConnectionProfile profile, string bucket, BucketEncryptionConfiguration configuration, CancellationToken cancellationToken);
    Task DeleteBucketEncryptionAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<IReadOnlyList<BucketTag>> GetBucketTagsAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task PutBucketTagsAsync(ConnectionProfile profile, string bucket, IReadOnlyCollection<BucketTag> tags, CancellationToken cancellationToken);
    Task DeleteBucketTagsAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<BucketEmptySummary> ScanBucketAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<BucketEmptyResult> EmptyBucketAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken);
    Task<PagedObjectResult> ListObjectsAsync(ConnectionProfile profile, string bucket, string prefix, string? continuationToken, int pageSize, CancellationToken cancellationToken);
    Task<PagedObjectVersionResult> ListObjectVersionsAsync(ConnectionProfile profile, string bucket, string prefix, string? keyMarker, string? versionIdMarker, int pageSize, CancellationToken cancellationToken);
    Task UploadFileAsync(ConnectionProfile profile, string bucket, string key, string localPath, string storageClass, TransferOperationContext transfer, CancellationToken cancellationToken);
    Task PutObjectAclAsync(ConnectionProfile profile, string bucket, string key, ObjectAclMode mode, CancellationToken cancellationToken);
    Task DownloadFileAsync(ConnectionProfile profile, string bucket, string key, string localPath, TransferOperationContext transfer, CancellationToken cancellationToken);
    Task DownloadObjectVersionAsync(ConnectionProfile profile, string bucket, string key, string versionId, string localPath, TransferOperationContext transfer, CancellationToken cancellationToken);
    Task RestoreObjectVersionAsync(ConnectionProfile profile, string bucket, string key, string versionId, CancellationToken cancellationToken);
    Task DeleteObjectVersionAsync(ConnectionProfile profile, string bucket, string key, string versionId, CancellationToken cancellationToken);
    Task DeleteObjectVersionsAsync(ConnectionProfile profile, string bucket, IReadOnlyCollection<ObjectVersionIdentity> versions, CancellationToken cancellationToken);
    Task<IReadOnlyList<IncompleteMultipartUpload>> ListIncompleteMultipartUploadsAsync(ConnectionProfile profile, string bucket, string? prefix, DateTimeOffset? initiatedBefore, CancellationToken cancellationToken);
    Task AbortMultipartUploadAsync(ConnectionProfile profile, string bucket, string key, string uploadId, CancellationToken cancellationToken);
    Task<MultipartCleanupResult> CleanupMultipartUploadsAsync(ConnectionProfile profile, IReadOnlyCollection<IncompleteMultipartUpload> uploads, CancellationToken cancellationToken);
    Task CreateFolderAsync(ConnectionProfile profile, string bucket, string folderKey, CancellationToken cancellationToken);
    Task DeleteObjectsAsync(ConnectionProfile profile, string bucket, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
    Task<bool> ObjectExistsAsync(ConnectionProfile profile, string bucket, string key, CancellationToken cancellationToken);
    Task CopyObjectAsync(ConnectionProfile profile, string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken);
    Task MoveObjectAsync(ConnectionProfile profile, string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken);
    Task<ObjectProperties> GetObjectPropertiesAsync(ConnectionProfile profile, string bucket, string key, CancellationToken cancellationToken);
    string CreatePresignedUrl(ConnectionProfile profile, string bucket, string key, TimeSpan lifetime);
}
