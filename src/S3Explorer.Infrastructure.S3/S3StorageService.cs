using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed class S3StorageService : IS3StorageService
{
    private readonly S3ClientFactory _factory;

    public S3StorageService(S3ClientFactory factory) => _factory = factory;

    public async Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = _factory.Create(profile);
            var response = await client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new(true, stopwatch.Elapsed, response.Buckets.Count,
                $"连接成功，发现 {response.Buckets.Count} 个 Bucket。");
        }
        catch (AmazonS3Exception ex)
        {
            stopwatch.Stop();
            var reachedServer = ex.StatusCode != 0;
            var message = reachedServer
                ? $"请求已到达服务器，但操作失败：{ex.ErrorCode} - {ex.Message}"
                : ex.Message;
            return new(false, stopwatch.Elapsed, 0, message, (int)ex.StatusCode, ex.ErrorCode, ex.RequestId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new(false, stopwatch.Elapsed, 0, ex.Message);
        }
    }

    public async Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var response = await client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        return response.Buckets
            .OrderBy(bucket => bucket.BucketName, StringComparer.OrdinalIgnoreCase)
            .Select(bucket => new BucketInfo(bucket.BucketName, bucket.CreationDate))
            .ToArray();
    }

    public async Task CreateBucketAsync(ConnectionProfile profile, string bucket, string region, CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);
        using var client = _factory.Create(profile);
        var request = new PutBucketRequest { BucketName = bucket };
        if (!string.Equals(region, "us-east-1", StringComparison.OrdinalIgnoreCase))
            request.BucketRegionName = region;
        await client.PutBucketAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteEmptyBucketAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var check = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            MaxKeys = 1
        }, cancellationToken).ConfigureAwait(false);

        if (check.S3Objects.Count > 0 || check.CommonPrefixes.Count > 0)
            throw new InvalidOperationException("Bucket 非空，默认不允许删除。");

        var uploads = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = bucket,
            MaxUploads = 1
        }, cancellationToken).ConfigureAwait(false);
        if (uploads.MultipartUploads.Count > 0)
            throw new InvalidOperationException("Bucket 存在未完成的分片上传，不能删除。");

        await client.DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedObjectResult> ListObjectsAsync(
        ConnectionProfile profile,
        string bucket,
        string prefix,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = S3Path.NormalizePrefix(prefix),
            Delimiter = "/",
            ContinuationToken = continuationToken,
            MaxKeys = Math.Clamp(pageSize, 1, 1000)
        }, cancellationToken).ConfigureAwait(false);

        var directories = response.CommonPrefixes.Select(commonPrefix => new S3ObjectEntry(
            commonPrefix,
            S3Path.DisplayName(commonPrefix, true),
            0,
            true,
            null,
            string.Empty));

        var objects = response.S3Objects
            .Where(item => !string.Equals(item.Key, prefix, StringComparison.Ordinal))
            .Select(item => new S3ObjectEntry(
                item.Key,
                S3Path.DisplayName(item.Key, false),
                item.Size,
                false,
                item.LastModified,
                item.StorageClass?.Value ?? "STANDARD",
                item.ETag,
                null,
                item.Owner?.DisplayName ?? item.Owner?.Id));

        var items = directories.Concat(objects)
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(items, response.NextContinuationToken, response.IsTruncated);
    }

    public async Task UploadFileAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        string storageClass,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        using var transfer = new TransferUtility(client);
        var request = new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = key,
            FilePath = localPath,
            StorageClass = S3StorageClass.FindValue(storageClass),
            PartSize = 16L * 1024 * 1024
        };
        request.UploadProgressEvent += (_, args) =>
            progress?.Report(new TransferProgress(args.TransferredBytes, args.TotalBytes));
        await transfer.UploadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadFileAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = localPath + ".s3explorer.download";
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);

        try
        {
            using var client = _factory.Create(profile);
            using var transfer = new TransferUtility(client);
            var request = new TransferUtilityDownloadRequest
            {
                BucketName = bucket,
                Key = key,
                FilePath = temporaryPath
            };
            request.WriteObjectProgressEvent += (_, args) =>
                progress?.Report(new TransferProgress(args.TransferredBytes, args.TotalBytes));
            await transfer.DownloadAsync(request, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, localPath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    public async Task CreateFolderAsync(ConnectionProfile profile, string bucket, string folderKey, CancellationToken cancellationToken)
    {
        if (!folderKey.EndsWith('/'))
            throw new ArgumentException("虚拟目录 Key 必须以 / 结尾。", nameof(folderKey));
        using var client = _factory.Create(profile);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = folderKey,
            ContentBody = string.Empty
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteObjectsAsync(ConnectionProfile profile, string bucket, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        foreach (var batch in keys.Chunk(1000))
        {
            var request = new DeleteObjectsRequest { BucketName = bucket };
            request.Objects.AddRange(batch.Select(key => new KeyVersion { Key = key }));
            await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CopyObjectAsync(
        ConnectionProfile profile,
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            DestinationBucket = destinationBucket,
            DestinationKey = destinationKey
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveObjectAsync(
        ConnectionProfile profile,
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        await CopyObjectAsync(profile, sourceBucket, sourceKey, destinationBucket, destinationKey, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var client = _factory.Create(profile);
            await client.DeleteObjectAsync(sourceBucket, sourceKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("复制成功，但删除源对象失败。", ex);
        }
    }

    public async Task<ObjectProperties> GetObjectPropertiesAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var response = await client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        var metadata = response.Metadata.Keys.ToDictionary(
            name => name,
            name => response.Metadata[name],
            StringComparer.OrdinalIgnoreCase);

        return new ObjectProperties(
            bucket,
            key,
            response.ContentLength,
            response.LastModified,
            response.ETag,
            response.Headers.ContentType,
            response.StorageClass?.Value,
            response.VersionId,
            metadata);
    }

    public string CreatePresignedUrl(ConnectionProfile profile, string bucket, string key, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "有效期必须大于 0 且不超过 7 天。");

        using var client = _factory.Create(profile);
        return client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime)
        });
    }

    private static void ValidateBucketName(string bucket)
    {
        if (bucket.Length is < 3 or > 63)
            throw new ArgumentException("Bucket 名称长度必须为 3 到 63 个字符。", nameof(bucket));
        if (bucket.Any(char.IsUpper))
            throw new ArgumentException("Bucket 名称必须使用小写字符。", nameof(bucket));
        if (IPAddress.TryParse(bucket, out _))
            throw new ArgumentException("Bucket 名称不能采用 IP 地址格式。", nameof(bucket));
        if (!char.IsLetterOrDigit(bucket[0]) || !char.IsLetterOrDigit(bucket[^1]))
            throw new ArgumentException("Bucket 名称必须以字母或数字开头和结尾。", nameof(bucket));
        if (bucket.Any(character => !(char.IsLower(character) || char.IsDigit(character) || character is '.' or '-')))
            throw new ArgumentException("Bucket 名称只能包含小写字母、数字、点和横线。", nameof(bucket));
    }
}
