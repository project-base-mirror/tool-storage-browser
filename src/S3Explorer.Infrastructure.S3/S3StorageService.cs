using System.Diagnostics;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using S3Explorer.Core;
using CoreBucketInfo = S3Explorer.Core.BucketInfo;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService : IS3StorageService
{
    private const long MaximumSingleCopyBytes = 5L * 1024 * 1024 * 1024;

    private readonly S3ClientFactory _factory;

    public S3StorageService(S3ClientFactory factory) => _factory = factory;

    public async Task<ConnectionTestResult> TestConnectionAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var credentialSource = profile.CredentialSourceDisplayName;
        AwsCredentialResolution? credentialResolution = null;
        using var connectionTimeout = CreateConnectionTimeout(profile, cancellationToken);
        try
        {
            var creation = _factory.CreateResolved(profile, allowInteractiveSso: true);
            credentialResolution = creation.CredentialResolution;
            credentialSource = creation.CredentialResolution.DisplayName;
            using var client = creation.Client;
            var response = await client.ListBucketsAsync(connectionTimeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var bucketCount = (response.Buckets ?? [])
                .Select(bucket => bucket.BucketName)
                .Concat(profile.KnownBuckets)
                .Distinct(StringComparer.Ordinal)
                .Count();
            return new(true, stopwatch.Elapsed, bucketCount,
                $"连接成功，发现或配置了 {bucketCount} 个 Bucket。",
                CredentialSource: creation.CredentialResolution.DisplayName,
                AwsIdentity: creation.CredentialResolution.GetCurrentIdentity());
        }
        catch (AmazonS3Exception ex) when (S3CompatibilityPolicy.IsRestrictedListBuckets(ex))
        {
            stopwatch.Stop();
            var configuredCount = profile.KnownBuckets.Count;
            var message = configuredCount > 0
                ? $"已到达 S3 服务；当前凭据无权列出全部 Bucket，将使用已配置的 {configuredCount} 个 Bucket。"
                : "已到达 S3 服务，但当前凭据无权列出全部 Bucket。请配置默认 Bucket 或外部 Bucket。";
            return new(true, stopwatch.Elapsed, configuredCount, message, (int)ex.StatusCode, ex.ErrorCode, ex.RequestId,
                credentialSource, credentialResolution?.GetCurrentIdentity());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectionTimeout.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(false, stopwatch.Elapsed, 0, $"连接超时（{profile.ConnectionTimeoutSeconds} 秒）。", null, "ConnectionTimeout",
                CredentialSource: credentialSource, AwsIdentity: credentialResolution?.GetCurrentIdentity());
        }
        catch (AmazonS3Exception ex)
        {
            stopwatch.Stop();
            var reachedServer = ex.StatusCode != 0;
            var message = reachedServer
                ? $"请求已到达服务器，但操作失败：{ex.ErrorCode} - {SensitiveDataRedactor.Redact(ex.Message)}"
                : SensitiveDataRedactor.Redact(ex.Message);
            return new(false, stopwatch.Elapsed, 0, message, (int)ex.StatusCode, ex.ErrorCode, ex.RequestId,
                credentialSource, credentialResolution?.GetCurrentIdentity());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new(false, stopwatch.Elapsed, 0, SensitiveDataRedactor.Redact(ex.Message), CredentialSource: credentialSource,
                AwsIdentity: credentialResolution?.GetCurrentIdentity());
        }
    }

    public async Task<IReadOnlyList<CoreBucketInfo>> ListBucketsAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        using var connectionTimeout = CreateConnectionTimeout(profile, cancellationToken);
        try
        {
            using var client = _factory.Create(profile);
            var response = await client.ListBucketsAsync(connectionTimeout.Token).ConfigureAwait(false);
            return (response.Buckets ?? [])
                .Select(bucket => new CoreBucketInfo(bucket.BucketName, bucket.CreationDate))
                .Concat(profile.KnownBuckets.Select(bucket => new CoreBucketInfo(bucket, null, IsConfigured: true)))
                .GroupBy(bucket => bucket.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(bucket => bucket.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (AmazonS3Exception ex) when (S3CompatibilityPolicy.IsRestrictedListBuckets(ex) && profile.KnownBuckets.Count > 0)
        {
            return profile.KnownBuckets
                .Select(bucket => new CoreBucketInfo(bucket, null, IsConfigured: true))
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

        var directories = (response.CommonPrefixes ?? []).Select(commonPrefix => new S3ObjectEntry(
            commonPrefix,
            S3Path.DisplayName(commonPrefix, true),
            0,
            true,
            null,
            string.Empty));

        var objects = (response.S3Objects ?? [])
            .Where(item => !string.Equals(item.Key, prefix, StringComparison.Ordinal))
            .Select(item => new S3ObjectEntry(
                item.Key,
                S3Path.DisplayName(item.Key, false),
                item.Size.GetValueOrDefault(),
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

        return new(items, response.NextContinuationToken, response.IsTruncated == true);
    }

    public async Task<PagedObjectVersionResult> ListObjectVersionsAsync(
        ConnectionProfile profile,
        string bucket,
        string prefix,
        string? keyMarker,
        string? versionIdMarker,
        int pageSize,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.VersionOperations, "对象版本列表");
        using var client = _factory.Create(profile);
        var response = await client.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = bucket,
            Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
            KeyMarker = string.IsNullOrWhiteSpace(keyMarker) ? null : keyMarker,
            VersionIdMarker = string.IsNullOrWhiteSpace(versionIdMarker) ? null : versionIdMarker,
            MaxKeys = Math.Clamp(pageSize, 1, 1000)
        }, cancellationToken).ConfigureAwait(false);
        var items = (response.Versions ?? []).Select(item =>
        {
            var isDeleteMarker = item.IsDeleteMarker == true;
            return new ObjectVersionEntry(
                item.Key,
                item.VersionId ?? string.Empty,
                item.IsLatest == true,
                isDeleteMarker,
                isDeleteMarker ? 0 : item.Size.GetValueOrDefault(),
                item.LastModified,
                isDeleteMarker ? null : item.ETag,
                isDeleteMarker ? string.Empty : item.StorageClass?.Value ?? "STANDARD");
        })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ThenByDescending(item => item.LastModified)
            .ToArray();
        var isTruncated = response.IsTruncated == true;
        if (isTruncated && string.IsNullOrWhiteSpace(response.NextKeyMarker))
            throw new InvalidOperationException("ListObjectVersions 返回了无效的下一页 Key Marker。");
        return new PagedObjectVersionResult(
            items,
            isTruncated ? response.NextKeyMarker : null,
            isTruncated ? response.NextVersionIdMarker : null,
            isTruncated);
    }

    public async Task UploadFileAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        string storageClass,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken) =>
        await UploadFileAsync(
            profile, bucket, key, localPath, storageClass, ObjectWriteHeaders.Empty,
            transferContext, cancellationToken).ConfigureAwait(false);

    public async Task UploadFileAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        string storageClass,
        ObjectWriteHeaders headers,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken)
    {
        transferContext.Options.Validate();
        headers = headers.ValidateAndNormalize();
        var file = new FileInfo(localPath);
        if (!file.Exists)
            throw new FileNotFoundException("上传源文件不存在。", localPath);

        try
        {
            using var client = _factory.Create(profile);
            if (file.Length < transferContext.Options.MultipartThresholdBytes)
            {
                await using var source = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                long transferred = 0;
                await using var throttled = new ThrottledReadStream(
                    source,
                    transferContext.BandwidthLimiter,
                    TransferDirection.Upload,
                    bytes =>
                    {
                        transferred += bytes;
                        transferContext.ReportProgress(new TransferProgress(transferred, file.Length));
                    },
                    leaveOpen: false);

                var request = new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = throttled,
                    AutoCloseStream = false,
                    TagSet = []
                };
                var resolvedStorageClass = S3CompatibilityPolicy.ResolveStorageClass(storageClass);
                if (resolvedStorageClass is not null)
                    request.StorageClass = resolvedStorageClass;
                ApplyWriteHeaders(request.Headers, request.Metadata, request.TagSet, headers);
                await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                transferContext.ReportProgress(new TransferProgress(file.Length, file.Length));
                return;
            }

            await UploadMultipartFileAsync(
                client, bucket, key, file, storageClass, headers, transferContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToTransferException(exception);
        }
    }

    public async Task PutObjectAclAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        ObjectAclMode mode,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Acl,
            "对象 ACL");
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("Bucket 不能为空。", nameof(bucket));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("对象 Key 不能为空。", nameof(key));

        using var client = _factory.Create(profile);
        try
        {
            await client.PutObjectAclAsync(new PutObjectAclRequest
            {
                BucketName = bucket,
                Key = key,
                ACL = mode == ObjectAclMode.PublicRead
                    ? S3CannedACL.PublicRead
                    : S3CannedACL.Private
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (
            string.Equals(exception.ErrorCode, "AccessControlListNotSupported", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "对象存储已禁用对象 ACL（常见于 Bucket Owner Enforced）。请使用 CDN 源站鉴权，或由管理员显式配置只读访问；程序不会自动修改 Bucket Policy 或 Public Access Block。",
                exception);
        }
    }

    public Task DownloadFileAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken) =>
        DownloadFileInternalAsync(
            profile, bucket, key, null, localPath, transferContext, cancellationToken);

    public Task DownloadObjectVersionAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string versionId,
        string localPath,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.VersionOperations,
            "对象版本下载");
        if (string.IsNullOrWhiteSpace(versionId))
            throw new ArgumentException("Version ID 不能为空。", nameof(versionId));
        return DownloadFileInternalAsync(
            profile, bucket, key, versionId, localPath, transferContext, cancellationToken);
    }

    private async Task DownloadFileInternalAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        string localPath,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken)
    {
        transferContext.Options.Validate();
        var temporaryPath = ResumableDownloadFile.TemporaryPath(localPath);

        try
        {
            using var client = _factory.Create(profile);
            var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = versionId
            }, cancellationToken).ConfigureAwait(false);

            var maximumDownloadBytes = transferContext.Options.MaximumDownloadBytes;
            if (maximumDownloadBytes > 0 && metadata.ContentLength > maximumDownloadBytes)
                throw new TransferExecutionException(new TransferFailureInfo(
                    $"远端对象大小 {metadata.ContentLength} bytes 超过本次下载上限 {maximumDownloadBytes} bytes。",
                    TransferFailureCategory.Validation,
                    Retryable: false));

            var remote = new RemoteObjectIdentity(
                metadata.ContentLength,
                metadata.ETag,
                metadata.VersionId);
            var temporaryExists = File.Exists(temporaryPath);
            var temporaryLength = temporaryExists ? new FileInfo(temporaryPath).Length : 0;
            var decision = DownloadResumePlanner.Decide(
                temporaryExists,
                temporaryLength,
                transferContext.DownloadCheckpoint,
                remote);

            ResumableDownloadFile.Prepare(
                temporaryPath,
                decision.ResetTemporaryFile,
                decision.Offset);

            var completed = decision.Offset;
            var checkpoint = new DownloadCheckpoint(
                temporaryPath,
                completed,
                remote.Length,
                remote.ETag,
                remote.VersionId);
            await transferContext.UpdateCheckpointAsync(
                completed,
                checkpoint,
                transferContext.MultipartCheckpoint,
                cancellationToken).ConfigureAwait(false);
            transferContext.ReportProgress(new TransferProgress(completed, remote.Length));

            if (completed < remote.Length)
            {
                var request = new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = versionId,
                    ByteRange = maximumDownloadBytes > 0
                        ? new ByteRange(completed, Math.Min(remote.Length, maximumDownloadBytes) - 1)
                        : completed > 0 ? new ByteRange(completed, remote.Length - 1) : null
                };
                using var response = await client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
                if (maximumDownloadBytes > 0 && response.ContentLength > maximumDownloadBytes - completed)
                    throw new TransferExecutionException(new TransferFailureInfo(
                        $"远端响应超过本次下载上限 {maximumDownloadBytes} bytes。",
                        TransferFailureCategory.Validation,
                        Retryable: false));
                if (!SameIdentity(metadata.ETag, response.ETag) ||
                    !SameIdentity(metadata.VersionId, response.VersionId))
                {
                    throw new TransferExecutionException(new TransferFailureInfo(
                        "下载期间远端对象身份发生变化，将在下次重试时重新校验断点。",
                        TransferFailureCategory.Conflict,
                        Retryable: true));
                }

                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                destination.Position = completed;
                var buffer = new byte[128 * 1024];
                var bytesSinceCheckpoint = 0L;
                while (true)
                {
                    if (maximumDownloadBytes > 0 && completed >= maximumDownloadBytes)
                        break;
                    var readBuffer = maximumDownloadBytes > 0
                        ? Math.Min(buffer.Length, checked((int)(maximumDownloadBytes - completed)))
                        : buffer.Length;
                    var read = await response.ResponseStream
                        .ReadAsync(buffer.AsMemory(0, readBuffer), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    if (maximumDownloadBytes > 0 && completed > maximumDownloadBytes - read)
                        throw new TransferExecutionException(new TransferFailureInfo(
                            $"远端响应在传输期间超过本次下载上限 {maximumDownloadBytes} bytes。",
                            TransferFailureCategory.Validation,
                            Retryable: false));

                    await transferContext.BandwidthLimiter
                        .WaitAsync(TransferDirection.Download, read, cancellationToken)
                        .ConfigureAwait(false);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    completed += read;
                    bytesSinceCheckpoint += read;
                    transferContext.ReportProgress(new TransferProgress(completed, remote.Length));

                    if (bytesSinceCheckpoint >= 4L * 1024 * 1024)
                    {
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        destination.Flush(flushToDisk: true);
                        checkpoint = checkpoint with { CompletedBytes = completed };
                        await transferContext.UpdateCheckpointAsync(
                            completed,
                            checkpoint,
                            transferContext.MultipartCheckpoint,
                            cancellationToken).ConfigureAwait(false);
                        bytesSinceCheckpoint = 0;
                    }
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            checkpoint = checkpoint with { CompletedBytes = completed };
            await transferContext.UpdateCheckpointAsync(
                completed,
                checkpoint,
                transferContext.MultipartCheckpoint,
                cancellationToken).ConfigureAwait(false);
            ResumableDownloadFile.Commit(temporaryPath, localPath, remote.Length);
            transferContext.ReportProgress(new TransferProgress(remote.Length, remote.Length));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToTransferException(exception);
        }
    }

    public async Task<IReadOnlyList<IncompleteMultipartUpload>> ListIncompleteMultipartUploadsAsync(
        ConnectionProfile profile,
        string bucket,
        string? prefix,
        DateTimeOffset? initiatedBefore,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var uploads = new List<IncompleteMultipartUpload>();
        string? keyMarker = null;
        string? uploadIdMarker = null;
        do
        {
            var response = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
            {
                BucketName = bucket,
                Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim(),
                KeyMarker = keyMarker,
                UploadIdMarker = uploadIdMarker,
                MaxUploads = 1000
            }, cancellationToken).ConfigureAwait(false);

            foreach (var upload in response.MultipartUploads ?? [])
            {
                var initiated = upload.Initiated is { } initiatedAt
                    ? new DateTimeOffset(initiatedAt.ToUniversalTime())
                    : DateTimeOffset.MinValue;
                if (initiatedBefore is not null && initiated > initiatedBefore.Value)
                    continue;
                try
                {
                    var parts = await ListMultipartPartsAsync(
                        client, bucket, upload.Key, upload.UploadId, cancellationToken).ConfigureAwait(false);
                    uploads.Add(new IncompleteMultipartUpload(
                        bucket,
                        upload.Key,
                        upload.UploadId,
                        initiated,
                        parts.Sum(part => part.Size),
                        parts.Count));
                }
                catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
                {
                }
            }

            keyMarker = response.IsTruncated == true ? response.NextKeyMarker : null;
            uploadIdMarker = response.IsTruncated == true ? response.NextUploadIdMarker : null;
        } while (keyMarker is not null || uploadIdMarker is not null);

        return MultipartUploadPlanner.Filter(uploads, prefix, initiatedBefore);
    }

    public async Task AbortMultipartUploadAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        await AbortMultipartInternalAsync(client, bucket, key, uploadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MultipartCleanupResult> CleanupMultipartUploadsAsync(
        ConnectionProfile profile,
        IReadOnlyCollection<IncompleteMultipartUpload> uploads,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var unique = uploads
            .GroupBy(upload => (upload.Bucket, upload.ObjectKey, upload.UploadId))
            .Select(group => group.First())
            .ToArray();
        var failed = new List<IncompleteMultipartUpload>();
        var cleaned = 0;
        foreach (var upload in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await AbortMultipartInternalAsync(
                    client, upload.Bucket, upload.ObjectKey, upload.UploadId, cancellationToken)
                    .ConfigureAwait(false);
                cleaned++;
            }
            catch
            {
                failed.Add(upload);
            }
        }
        return new MultipartCleanupResult(unique.Length, cleaned, failed);
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
        if (!S3CompatibilityPolicy.ShouldUseMultiObjectDelete(profile))
        {
            await DeleteOneByOneAsync(client, bucket, keys, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var batch in keys.Chunk(1000))
        {
            var request = new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = batch.Select(key => new KeyVersion { Key = key }).ToList()
            };
            try
            {
                var response = await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
                var deleteErrors = response.DeleteErrors ?? [];
                if (deleteErrors.Count > 0)
                {
                    var first = deleteErrors[0];
                    throw new InvalidOperationException(
                        $"删除对象时有 {deleteErrors.Count:N0} 项失败；首项 Key={first.Key}，Code={first.Code}。");
                }
            }
            catch (AmazonS3Exception ex) when (S3CompatibilityPolicy.ShouldFallbackToSingleDelete(ex))
            {
                await DeleteOneByOneAsync(client, bucket, batch, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteObjectVersionAsync(
        ConnectionProfile profile, string bucket, string key, string versionId,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.VersionOperations,
            "对象版本删除");
        if (string.IsNullOrWhiteSpace(versionId))
            throw new ArgumentException("Version ID 不能为空。", nameof(versionId));
        using var client = _factory.Create(profile);
        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteObjectVersionsAsync(
        ConnectionProfile profile, string bucket,
        IReadOnlyCollection<ObjectVersionIdentity> versions,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.VersionOperations,
            "批量对象版本删除");
        var unique = versions
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.VersionId))
            .Distinct()
            .ToArray();
        if (unique.Length != versions.Count)
            throw new ArgumentException("对象版本列表包含空值或重复项。", nameof(versions));
        if (unique.Length == 0) return;
        using var client = _factory.Create(profile);
        foreach (var batch in unique.Chunk(1000))
        {
            var request = new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = batch.Select(item => new KeyVersion
                {
                    Key = item.Key,
                    VersionId = item.VersionId
                }).ToList()
            };
            var response = await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
            var deleteErrors = response.DeleteErrors ?? [];
            if (deleteErrors.Count > 0)
            {
                var first = deleteErrors[0];
                throw new InvalidOperationException(
                    $"永久删除对象版本时有 {deleteErrors.Count:N0} 项失败；首项 Key={first.Key}，VersionId={first.VersionId}，Code={first.Code}。");
            }
        }
    }

    public async Task RestoreObjectVersionAsync(
        ConnectionProfile profile, string bucket, string key, string versionId,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.VersionOperations,
            "对象版本恢复");
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("对象 Key 不能为空。", nameof(key));
        if (string.IsNullOrWhiteSpace(versionId))
            throw new ArgumentException("Version ID 不能为空。", nameof(versionId));
        using var client = _factory.Create(profile);
        var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId
        }, cancellationToken).ConfigureAwait(false);
        if (profile.EnableMultipartCopy && metadata.ContentLength > MaximumSingleCopyBytes)
        {
            await MultipartCopyAsync(
                client, bucket, key, bucket, key, metadata.ContentLength, versionId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        await CopyObjectSimpleAsync(client, bucket, key, bucket, key, versionId, cancellationToken)
            .ConfigureAwait(false);
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
                await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket, destinationKey, objectSize.Value, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        try
        {
            await CopyObjectSimpleAsync(client, sourceBucket, sourceKey, destinationBucket, destinationKey, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (profile.EnableMultipartCopy && S3CompatibilityPolicy.RequiresMultipartCopy(ex))
        {
            objectSize ??= (await client.GetObjectMetadataAsync(sourceBucket, sourceKey, cancellationToken).ConfigureAwait(false)).ContentLength;
            await MultipartCopyAsync(client, sourceBucket, sourceKey, destinationBucket, destinationKey, objectSize.Value, null, cancellationToken)
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

    public async Task<bool> ObjectExistsAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception exception) when (IsObjectNotFound(exception))
        {
            return false;
        }
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

    private static async Task UploadMultipartFileAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        FileInfo file,
        string storageClass,
        ObjectWriteHeaders headers,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken)
    {
        var partSize = transferContext.Options.PartSizeBytes;
        var modified = new DateTimeOffset(file.LastWriteTimeUtc);
        var checkpoint = transferContext.MultipartCheckpoint;

        // Existing checkpoints predate Header/Metadata fingerprints. Restart a customized upload
        // rather than resuming parts whose final object attributes cannot be proven equivalent.
        if (checkpoint is not null && HasWriteHeaders(headers))
        {
            await AbortMultipartInternalAsync(
                client,
                string.IsNullOrWhiteSpace(checkpoint.Bucket) ? bucket : checkpoint.Bucket,
                string.IsNullOrWhiteSpace(checkpoint.ObjectKey) ? key : checkpoint.ObjectKey,
                checkpoint.UploadId,
                cancellationToken).ConfigureAwait(false);
            checkpoint = null;
        }

        if (checkpoint is not null &&
            !checkpoint.Matches(bucket, key, file.Length, modified, partSize))
        {
            await AbortMultipartInternalAsync(
                client,
                string.IsNullOrWhiteSpace(checkpoint.Bucket) ? bucket : checkpoint.Bucket,
                string.IsNullOrWhiteSpace(checkpoint.ObjectKey) ? key : checkpoint.ObjectKey,
                checkpoint.UploadId,
                cancellationToken).ConfigureAwait(false);
            checkpoint = null;
        }

        MultipartUploadReconciliation reconciliation;
        if (checkpoint is not null)
        {
            try
            {
                var remoteParts = await ListMultipartPartsAsync(
                    client, bucket, key, checkpoint.UploadId, cancellationToken).ConfigureAwait(false);
                reconciliation = MultipartUploadPlanner.Reconcile(file.Length, partSize, remoteParts);
                checkpoint = checkpoint with { CompletedParts = reconciliation.ConfirmedParts };
            }
            catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
            {
                checkpoint = null;
                reconciliation = null!;
            }
        }
        else
        {
            reconciliation = null!;
        }

        if (checkpoint is null)
        {
            var request = new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                TagSet = []
            };
            var resolvedStorageClass = S3CompatibilityPolicy.ResolveStorageClass(storageClass);
            if (resolvedStorageClass is not null)
                request.StorageClass = resolvedStorageClass;
            ApplyWriteHeaders(request.Headers, request.Metadata, request.TagSet, headers);
            var initiated = await client.InitiateMultipartUploadAsync(request, cancellationToken).ConfigureAwait(false);
            checkpoint = new MultipartUploadCheckpoint(
                initiated.UploadId,
                partSize,
                [],
                false,
                bucket,
                key,
                file.Length,
                modified,
                DateTimeOffset.UtcNow);
            reconciliation = MultipartUploadPlanner.Reconcile(file.Length, partSize, []);
        }

        var uploadId = checkpoint.UploadId;
        var completedParts = reconciliation.ConfirmedParts
            .ToDictionary(part => part.PartNumber);
        long transferredBytes = reconciliation.ConfirmedBytes;
        await transferContext.UpdateCheckpointAsync(
            transferredBytes, null, checkpoint, cancellationToken).ConfigureAwait(false);
        transferContext.ReportProgress(new TransferProgress(transferredBytes, file.Length));

        using var uploadGate = new SemaphoreSlim(
            transferContext.Options.MultipartConcurrency,
            transferContext.Options.MultipartConcurrency);
        using var checkpointGate = new SemaphoreSlim(1, 1);
        var uploadTasks = reconciliation.MissingParts.Select(async part =>
        {
            await uploadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var source = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                source.Position = part.Offset;
                await using var bounded = new BoundedReadStream(source, part.Size, leaveOpen: true);
                await using var throttled = new ThrottledReadStream(
                    bounded,
                    transferContext.BandwidthLimiter,
                    TransferDirection.Upload,
                    bytes =>
                    {
                        var total = Interlocked.Add(ref transferredBytes, bytes);
                        transferContext.ReportProgress(new TransferProgress(total, file.Length));
                    },
                    leaveOpen: true);
                var response = await client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = part.PartNumber,
                    PartSize = part.Size,
                    InputStream = throttled,
                    IsLastPart = part.Offset + part.Size == file.Length
                }, cancellationToken).ConfigureAwait(false);

                await checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    completedParts[part.PartNumber] = new MultipartPartCheckpoint(
                        part.PartNumber, response.ETag, part.Size);
                    checkpoint = checkpoint with
                    {
                        CompletedParts = completedParts.Values
                            .OrderBy(item => item.PartNumber)
                            .ToArray()
                    };
                    var confirmedBytes = completedParts.Values.Sum(item => item.Size);
                    await transferContext.UpdateCheckpointAsync(
                        confirmedBytes, null, checkpoint, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    checkpointGate.Release();
                }
            }
            finally
            {
                uploadGate.Release();
            }
        }).ToArray();

        await Task.WhenAll(uploadTasks).ConfigureAwait(false);
        var complete = new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = completedParts.Values
                .OrderBy(part => part.PartNumber)
                .Select(part => new PartETag(part.PartNumber, part.ETag))
                .ToList()
        };
        await client.CompleteMultipartUploadAsync(complete, cancellationToken).ConfigureAwait(false);
        transferContext.ReportProgress(new TransferProgress(file.Length, file.Length));
    }

    private static async Task<IReadOnlyList<MultipartPartCheckpoint>> ListMultipartPartsAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var parts = new List<MultipartPartCheckpoint>();
        string? marker = null;
        while (true)
        {
            var response = await client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumberMarker = marker,
                MaxParts = 1000
            }, cancellationToken).ConfigureAwait(false);
            var responseParts = response.Parts ?? [];
            parts.AddRange(responseParts.Select(part =>
                new MultipartPartCheckpoint(
                    part.PartNumber.GetValueOrDefault(), part.ETag, part.Size.GetValueOrDefault())));
            if (response.IsTruncated != true) break;
            var nextMarker = responseParts.Count == 0
                ? marker
                : responseParts[^1].PartNumber.GetValueOrDefault().ToString();
            if (string.IsNullOrWhiteSpace(nextMarker) || string.Equals(nextMarker, marker, StringComparison.Ordinal))
                throw new InvalidOperationException("ListParts 返回了无效的分页游标。");
            marker = nextMarker;
        }
        return parts;
    }

    private static async Task AbortMultipartInternalAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
        {
        }
    }

    private static void EnsureBucketFeature(BucketFeatureSupport support, string feature)
    {
        if (!support.Supported)
            throw new NotSupportedException($"{feature} 不可用：{support.Reason}");
    }

    private static void ApplyWriteHeaders(
        HeadersCollection destinationHeaders,
        MetadataCollection destinationMetadata,
        List<Tag>? destinationTags,
        ObjectWriteHeaders source)
    {
        destinationHeaders.ContentType = source.ContentType;
        destinationHeaders.CacheControl = source.CacheControl;
        destinationHeaders.ContentEncoding = source.ContentEncoding;
        destinationHeaders.ContentDisposition = source.ContentDisposition;
        if (source.ExpiresUtc is not null)
            destinationHeaders.Expires = source.ExpiresUtc.Value.UtcDateTime;

        foreach (var pair in source.Metadata ?? new Dictionary<string, string>())
            destinationMetadata.Add(pair.Key, pair.Value);

        if (destinationTags is null) return;
        destinationTags.Clear();
        destinationTags.AddRange((source.Tags ?? [])
            .Select(tag => new Tag { Key = tag.Key, Value = tag.Value }));
    }

    private static bool HasWriteHeaders(ObjectWriteHeaders headers) =>
        headers.ContentType is not null || headers.CacheControl is not null ||
        headers.ContentEncoding is not null || headers.ContentDisposition is not null ||
        headers.ExpiresUtc is not null || headers.Metadata is { Count: > 0 } ||
        headers.Tags is { Count: > 0 };

    private static bool IsMissingBucketPolicy(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchBucketPolicy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exception.ErrorCode, "NoSuchPolicy", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingPublicAccessBlock(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchPublicAccessBlockConfiguration", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingOwnershipControls(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "OwnershipControlsNotFoundError", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exception.ErrorCode, "NoSuchOwnershipControls", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingCors(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchCORSConfiguration", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingEncryption(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "ServerSideEncryptionConfigurationNotFoundError", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingTagSet(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchTagSet", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingLifecycle(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchLifecycleConfiguration", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingObjectLockConfiguration(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "ObjectLockConfigurationNotFoundError", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exception.ErrorCode, "NoSuchObjectLockConfiguration", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingObjectLockState(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchObjectLockConfiguration", StringComparison.OrdinalIgnoreCase);

    private static ObjectRetentionMode? FromSdkRetentionMode(ObjectLockRetentionMode? value) => value?.Value switch
    {
        "GOVERNANCE" => ObjectRetentionMode.Governance,
        "COMPLIANCE" => ObjectRetentionMode.Compliance,
        _ => null
    };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsUnsupportedBucketFeature(AmazonS3Exception exception) =>
        exception.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed ||
        string.Equals(exception.ErrorCode, "NotImplemented", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exception.ErrorCode, "InvalidRequest", StringComparison.OrdinalIgnoreCase);

    private static async Task<(bool Supported, long Versions, long DeleteMarkers)> ScanVersionsAsync(
        IAmazonS3 client, string bucket, CancellationToken cancellationToken)
    {
        try
        {
            long versions = 0;
            long markers = 0;
            string? keyMarker = null;
            string? versionMarker = null;
            bool more;
            do
            {
                var response = await client.ListVersionsAsync(new ListVersionsRequest
                {
                    BucketName = bucket,
                    KeyMarker = keyMarker,
                    VersionIdMarker = versionMarker,
                    MaxKeys = 1000
                }, cancellationToken).ConfigureAwait(false);
                var responseVersions = response.Versions ?? [];
                versions += responseVersions.LongCount(item => item.IsDeleteMarker != true);
                markers += responseVersions.LongCount(item => item.IsDeleteMarker == true);
                more = response.IsTruncated == true;
                keyMarker = more ? response.NextKeyMarker : null;
                versionMarker = more ? response.NextVersionIdMarker : null;
            } while (more);
            return (true, versions, markers);
        }
        catch (AmazonS3Exception exception) when (IsUnsupportedBucketFeature(exception))
        {
            return (false, 0, 0);
        }
    }

    private static async Task<(List<KeyVersion> Items, long VersionCount, long DeleteMarkerCount)> CollectVersionsAsync(
        IAmazonS3 client, string bucket, CancellationToken cancellationToken)
    {
        var items = new List<KeyVersion>();
        long versions = 0;
        long markers = 0;
        string? keyMarker = null;
        string? versionMarker = null;
        try
        {
            bool more;
            do
            {
                var response = await client.ListVersionsAsync(new ListVersionsRequest
                {
                    BucketName = bucket, KeyMarker = keyMarker,
                    VersionIdMarker = versionMarker, MaxKeys = 1000
                }, cancellationToken).ConfigureAwait(false);
                var responseVersions = response.Versions ?? [];
                items.AddRange(responseVersions.Select(item =>
                    new KeyVersion { Key = item.Key, VersionId = item.VersionId }));
                versions += responseVersions.LongCount(item => item.IsDeleteMarker != true);
                markers += responseVersions.LongCount(item => item.IsDeleteMarker == true);
                more = response.IsTruncated == true;
                keyMarker = more ? response.NextKeyMarker : null;
                versionMarker = more ? response.NextVersionIdMarker : null;
            } while (more);
        }
        catch (AmazonS3Exception exception) when (IsUnsupportedBucketFeature(exception))
        {
        }
        return (items, versions, markers);
    }

    private static bool IsObjectNotFound(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exception.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoSuchUpload(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.OrdinalIgnoreCase);

    private static Task CopyObjectSimpleAsync(
        IAmazonS3 client,
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        string? sourceVersionId,
        CancellationToken cancellationToken) =>
        client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = sourceBucket,
            SourceKey = sourceKey,
            SourceVersionId = sourceVersionId,
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
        string? sourceVersionId,
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
                    SourceVersionId = sourceVersionId,
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
                UploadId = initiate.UploadId,
                PartETags = partEtags
            };
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

    private static bool SameIdentity(string? expected, string? actual) =>
        string.Equals(
            expected?.Trim().Trim('"') ?? string.Empty,
            actual?.Trim().Trim('"') ?? string.Empty,
            StringComparison.Ordinal);

    private static TransferExecutionException ToTransferException(Exception exception)
    {
        if (exception is TransferExecutionException transfer)
            return transfer;

        if (exception is AmazonS3Exception s3)
        {
            var category = s3.StatusCode switch
            {
                HttpStatusCode.Unauthorized => TransferFailureCategory.Authentication,
                HttpStatusCode.Forbidden => TransferFailureCategory.Authorization,
                HttpStatusCode.NotFound => TransferFailureCategory.NotFound,
                HttpStatusCode.Conflict => TransferFailureCategory.Conflict,
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => TransferFailureCategory.Timeout,
                _ when (int)s3.StatusCode >= 500 => TransferFailureCategory.Service,
                _ => TransferFailureCategory.Unknown
            };
            var retryable =
                category is TransferFailureCategory.Timeout or TransferFailureCategory.Service ||
                string.Equals(s3.ErrorCode, "SlowDown", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s3.ErrorCode, "RequestTimeout", StringComparison.OrdinalIgnoreCase);
            return new TransferExecutionException(new TransferFailureInfo(
                s3.Message,
                category,
                (int)s3.StatusCode,
                s3.ErrorCode,
                s3.RequestId,
                retryable),
                s3);
        }

        return new TransferExecutionException(TransferFailureClassifier.Classify(exception), exception);
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
