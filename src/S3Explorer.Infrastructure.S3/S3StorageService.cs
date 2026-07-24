using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using S3Explorer.Core;
using CoreBucketInfo = S3Explorer.Core.BucketInfo;

namespace S3Explorer.Infrastructure.S3;

public sealed class S3StorageService : IS3StorageService
{
    private const long MaximumSingleCopyBytes = 5L * 1024 * 1024 * 1024;

    private readonly S3ClientFactory _factory;

    public S3StorageService(S3ClientFactory factory) => _factory = factory;

    public async Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var connectionTimeout = CreateConnectionTimeout(profile, cancellationToken);
        try
        {
            using var client = _factory.Create(profile);
            var response = await client.ListBucketsAsync(connectionTimeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var bucketCount = response.Buckets
                .Select(bucket => bucket.BucketName)
                .Concat(profile.KnownBuckets)
                .Distinct(StringComparer.Ordinal)
                .Count();
            return new(true, stopwatch.Elapsed, bucketCount,
                $"连接成功，发现或配置了 {bucketCount} 个 Bucket。");
        }
        catch (AmazonS3Exception ex) when (S3CompatibilityPolicy.IsRestrictedListBuckets(ex))
        {
            stopwatch.Stop();
            var configuredCount = profile.KnownBuckets.Count;
            var message = configuredCount > 0
                ? $"已到达 S3 服务；当前凭据无权列出全部 Bucket，将使用已配置的 {configuredCount} 个 Bucket。"
                : "已到达 S3 服务，但当前凭据无权列出全部 Bucket。请配置默认 Bucket 或外部 Bucket。";
            return new(true, stopwatch.Elapsed, configuredCount, message, (int)ex.StatusCode, ex.ErrorCode, ex.RequestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectionTimeout.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(false, stopwatch.Elapsed, 0, $"连接超时（{profile.ConnectionTimeoutSeconds} 秒）。", null, "ConnectionTimeout");
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

    public async Task<IReadOnlyList<CoreBucketInfo>> ListBucketsAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        using var connectionTimeout = CreateConnectionTimeout(profile, cancellationToken);
        try
        {
            using var client = _factory.Create(profile);
            var response = await client.ListBucketsAsync(connectionTimeout.Token).ConfigureAwait(false);
            return response.Buckets
                .Select(bucket => new CoreBucketInfo(bucket.BucketName, bucket.CreationDate))
                .Concat(profile.KnownBuckets.Select(bucket => new CoreBucketInfo(bucket, null)))
                .GroupBy(bucket => bucket.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (AmazonS3Exception ex) when (S3CompatibilityPolicy.IsRestrictedListBuckets(ex) && profile.KnownBuckets.Count > 0)
        {
            return profile.KnownBuckets
                .Select(bucket => new CoreBucketInfo(bucket, null))
                .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectionTimeout.IsCancellationRequested)
        {
            throw new TimeoutException($"连接在 {profile.ConnectionTimeoutSeconds} 秒内未响应。");
        }
    }

    private static CancellationTokenSource CreateConnectionTimeout(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds));
        return source;
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
        if (keys.Count == 0)
            return;

        using var client = _factory.Create(profile);
        if (!profile.EnableMultiObjectDelete)
        {
            await DeleteOneByOneAsync(client, bucket, keys, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var batch in keys.Chunk(1000))
        {
            var request = new DeleteObjectsRequest { BucketName = bucket };
            request.Objects.AddRange(batch.Select(key => new KeyVersion { Key = key }));
            try
            {
                await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex) when (S3CompatibilityPolicy.ShouldFallbackToSingleDelete(ex))
            {
                await DeleteOneByOneAsync(client, bucket, batch, cancellationToken).ConfigureAwait(false);
            }
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

        long? objectSize = null;
        if (profile.EnableMultipartCopy)
        {
            var metadata = await client.GetObjectMetadataAsync(sourceBucket, sourceKey, cancellationToken).ConfigureAwait(false);
            objectSize = metadata.ContentLength;
            if (objectSize > MaximumSingleCopyBytes)
            {
                await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket, destinationKey, objectSize.Value, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        try
        {
            await CopyObjectSimpleAsync(client, sourceBucket, sourceKey, destinationBucket, destinationKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (profile.EnableMultipartCopy && S3CompatibilityPolicy.RequiresMultipartCopy(ex))
        {
            objectSize ??= (await client.GetObjectMetadataAsync(sourceBucket, sourceKey, cancellationToken).ConfigureAwait(false)).ContentLength;
            await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket, destinationKey, objectSize.Value, cancellationToken)
                .ConfigureAwait(false);
        }
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

    private static async Task DeleteOneByOneAsync(
        IAmazonS3 client,
        string bucket,
        IEnumerable<string> keys,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys)
            await client.DeleteObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
    }

    private static Task CopyObjectSimpleAsync(
        IAmazonS3 client,
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken) =>
        client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            DestinationBucket = destinationBucket,
            DestinationKey = destinationKey
        }, cancellationToken);

    private static async Task MultipartCopyAsync(
        IAmazonS3 client,
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        long objectSize,
        CancellationToken cancellationToken)
    {
        if (objectSize <= 0)
            throw new InvalidOperationException("无法对空对象执行 Multipart Copy。");

        var initiate = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = destinationBucket,
            Key = destinationKey
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            var partSize = S3CompatibilityPolicy.CalculateCopyPartSize(objectSize);
            var partEtags = new List<PartETag>();
            var partNumber = 1;
            for (long offset = 0; offset < objectSize; offset += partSize, partNumber++)
            {
                var lastByte = Math.Min(objectSize - 1, offset + partSize - 1);
                var response = await client.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = sourceBucket,
                    SourceKey = sourceKey,
                    DestinationBucket = destinationBucket,
                    DestinationKey = destinationKey,
                    UploadId = initiate.UploadId,
                    PartNumber = partNumber,
                    FirstByte = offset,
                    LastByte = lastByte
                }, cancellationToken).ConfigureAwait(false);
                partEtags.Add(new PartETag(partNumber, response.ETag));
            }

            var complete = new CompleteMultipartUploadRequest
            {
                BucketName = destinationBucket,
                Key = destinationKey,
                UploadId = initiate.UploadId
            };
            complete.AddPartETags(partEtags);
            await client.CompleteMultipartUploadAsync(complete, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = destinationBucket,
                    Key = destinationKey,
                    UploadId = initiate.UploadId
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original copy error. Cleanup can be retried from the MPU manager.
            }
            throw;
        }
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
