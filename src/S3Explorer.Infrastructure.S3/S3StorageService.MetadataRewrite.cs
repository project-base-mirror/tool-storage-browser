using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    // Legacy callers cannot supply the snapshot they edited. Keep them fail-closed;
    // the UI and batch editor use the snapshot-aware overload below.
    public Task ReplaceObjectMetadataAsync(ConnectionProfile profile, string bucket, string key,
        string? versionId, ObjectWriteHeaders headers, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("请使用带完整对象快照的 Metadata 编辑接口，以检查并发属性变化。"));

    public async Task ReplaceObjectMetadataAsync(ConnectionProfile profile, ObjectProperties expected,
        ObjectWriteHeaders headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMetadataRewrite(profile, expected, headers);
        var creation = _factory.CreateResolved(profile, maxErrorRetry: 0);
        using var client = creation.Client;
        if (profile.ServiceType is S3ServiceType.AliyunOss or S3ServiceType.TencentCos)
        {
            // Resolve the same selected identity as normal S3 operations. Never
            // fall back to stored profile fields when a role/session is selected.
            var credentials = await creation.CredentialResolution.Credentials.GetCredentialsAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.ServiceType == S3ServiceType.AliyunOss)
                await RewriteOssMetadataAsync(profile, expected, headers, credentials, cancellationToken).ConfigureAwait(false);
            else
                await RewriteCosMetadataAsync(client, profile, credentials, expected, headers, cancellationToken).ConfigureAwait(false);
            return;
        }
        await ReplaceObjectMetadataAsync(client, profile, expected, headers, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ReplaceObjectMetadataAsync(IAmazonS3 client, ConnectionProfile profile,
        ObjectProperties expected, ObjectWriteHeaders headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMetadataRewrite(profile, expected, headers);
        var normalized = headers.ValidateForMetadataCopy();
        if (profile.ServiceType is S3ServiceType.AliyunOss or S3ServiceType.TencentCos)
            throw new NotSupportedException("OSS / COS Metadata 编辑必须使用原生签名与属性保留流程，不使用通用 S3 复制。");
        if (profile.ServiceType == S3ServiceType.GoogleCloudStorage)
        {
            await RewriteGcsMetadataAsync(client, profile, expected, normalized, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Read CURRENT, never a pinned historical version: using an old version for a
        // self-copy can roll back newly uploaded content even when its source condition passes.
        var current = CreateObjectProperties(expected.Bucket, expected.Key,
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            { BucketName = expected.Bucket, Key = expected.Key }, cancellationToken).ConfigureAwait(false));
        if (!ObjectMetadataRewritePolicy.Matches(expected, current))
            throw new InvalidOperationException("对象内容、版本或属性已改变，已停止保存；请重新打开属性后编辑。");
        ValidateMetadataRewrite(profile, current, normalized);

        List<S3Grant>? preservedGrants = null;
        if (profile.ServiceType is S3ServiceType.AmazonS3 or S3ServiceType.Custom)
        {
            var aclDisabled = false;
            if (profile.ServiceType == S3ServiceType.AmazonS3)
            {
                try
                {
                    var ownership = await client.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest
                    { BucketName = expected.Bucket }, cancellationToken).ConfigureAwait(false);
                    aclDisabled = ownership.OwnershipControls?.Rules is { Count: 1 } rules &&
                        rules[0].ObjectOwnership?.Value == "BucketOwnerEnforced";
                }
                catch (AmazonS3Exception e) when (e.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound) { }
            }
            if (!aclDisabled)
            {
                var acl = await client.GetObjectAclAsync(new GetObjectAclRequest
                { BucketName = expected.Bucket, Key = expected.Key }, cancellationToken).ConfigureAwait(false);
                if (acl.Grants is not { Count: > 0 } grants)
                    throw new NotSupportedException("无法读取完整对象 ACL，已停止以避免改变访问授权。");
                preservedGrants = grants;
            }
        }
        if (profile.ServiceType is S3ServiceType.AmazonS3 or S3ServiceType.MinIO or S3ServiceType.Custom or S3ServiceType.BackblazeB2)
        {
            try
            {
                await client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest
                { BucketName = expected.Bucket }, cancellationToken).ConfigureAwait(false);
                throw new NotSupportedException("Bucket 存在或无法确认 Object Lock 配置，不能无损原地复制 Metadata。");
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound &&
                e.ErrorCode == "ObjectLockConfigurationNotFoundError") { }
        }
        // Recheck after bucket validation, immediately before dispatch. Metadata-only
        // races remain non-atomic and are explicitly disclosed by both UI entry points.
        var latest = CreateObjectProperties(expected.Bucket, expected.Key,
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            { BucketName = expected.Bucket, Key = expected.Key }, cancellationToken).ConfigureAwait(false));
        if (!ObjectMetadataRewritePolicy.Matches(expected, latest))
            throw new InvalidOperationException("保存前检测到对象变化，已停止；请刷新后重试。");
        var request = new CopyObjectRequest
        {
            SourceBucket = current.Bucket, SourceKey = current.Key,
            // No SourceVersionId: never replay stale version content into the live key.
            DestinationBucket = current.Bucket, DestinationKey = current.Key,
            ETagToMatch = current.ETag,
            // AWS enforces destination If-Match; compatible providers are not assumed
            // to enforce it merely because they accept the header.
            IfMatch = profile.ServiceType == S3ServiceType.AmazonS3 ? current.ETag : null,
            MetadataDirective = S3MetadataDirective.REPLACE,
            StorageClass = string.IsNullOrEmpty(current.StorageClass) ? null : S3StorageClass.FindValue(current.StorageClass),
            WebsiteRedirectLocation = current.WebsiteRedirectLocation
        };
        if (preservedGrants is not null) request.Grants = preservedGrants;
        if (profile.ServiceType is not (S3ServiceType.CloudflareR2 or S3ServiceType.BackblazeB2 or S3ServiceType.SupabaseStorage))
            request.TaggingDirective = TaggingDirective.COPY;
        if (profile.ServiceType is not (S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage))
        {
            if (current.ServerSideEncryptionMethod == "AES256")
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }
        if (profile.ServiceType == S3ServiceType.CloudflareR2)
            request.Headers["cf-copy-destination-if-match"] = current.ETag;
        ApplyWriteHeaders(request.Headers, request.Metadata, null, normalized);
        request.Headers.ContentLanguage = current.ContentLanguage;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
            if (profile.ServiceType is S3ServiceType.BackblazeB2 or S3ServiceType.SupabaseStorage)
            {
                // Compatible/self-hosted servers may accept a directive but ignore an
                // edited field. Never report success without observing the requested snapshot.
                ObjectProperties saved;
                try
                {
                    saved = CreateObjectProperties(expected.Bucket, expected.Key,
                        await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                        { BucketName = expected.Bucket, Key = expected.Key }, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception readError)
                {
                    throw new InvalidOperationException("Metadata 写请求已提交，但无法重新读取确认结果；请核对当前对象，不要直接重试。", readError);
                }
                if (saved.Size != current.Size || saved.ContentType != normalized.ContentType ||
                    saved.CacheControl != normalized.CacheControl || saved.ContentEncoding != normalized.ContentEncoding ||
                    saved.ContentDisposition != normalized.ContentDisposition || saved.ExpiresUtc != normalized.ExpiresUtc ||
                    saved.Metadata.Count != normalized.Metadata!.Count || normalized.Metadata.Any(pair =>
                        !saved.Metadata.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    throw new InvalidOperationException("Metadata 写请求已提交，但重新读取的属性与编辑内容不一致；可能是服务商不支持该字段或并发修改。请核对当前对象，不要直接重试。");
            }
        }
        catch (Exception e) when (e is TimeoutException or HttpRequestException or IOException or OperationCanceledException or Amazon.Runtime.AmazonClientException ||
            e is AmazonS3Exception s3 && ((int)s3.StatusCode == 0 || (int)s3.StatusCode >= 500 ||
                s3.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict))
        {
            throw new InvalidOperationException("Metadata 保存结果未确认，可能已写入或发生并发冲突；请重新读取对象核对，不要直接重试。", e);
        }
    }

    private static void ValidateMetadataRewrite(ConnectionProfile profile, ObjectProperties expected, ObjectWriteHeaders headers)
    {
        var capability = ObjectMetadataRewritePolicy.For(profile.ServiceType, expected);
        if (!capability.Supported) throw new NotSupportedException(capability.Reason);
        if (headers.Tags is { Count: > 0 })
            throw new ArgumentException("此操作保留服务端 Tags，不能同时替换 Tags。", nameof(headers));
        ObjectMetadataRewritePolicy.ValidateEdits(profile.ServiceType, headers);
        _ = headers.ValidateForMetadataCopy();
    }
}
