using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    // Google XML self-copy with current source + four revision preconditions is
    // a metadata update. Never pin a historical source or fall back to PUT.
    internal static async Task RewriteGcsMetadataAsync(IAmazonS3 client, ConnectionProfile profile,
        ObjectProperties expected, ObjectWriteHeaders headers, CancellationToken token)
    {
        ValidateGcsSnapshot(expected);
        ValidateMetadataRewrite(profile, expected, headers);
        var normalized = headers.ValidateForMetadataCopy();
        var current = CreateObjectProperties(expected.Bucket, expected.Key,
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            { BucketName = expected.Bucket, Key = expected.Key }, token).ConfigureAwait(false));
        ValidateGcsSnapshot(current);
        if (!ObjectMetadataRewritePolicy.Matches(expected, current))
            throw new InvalidOperationException("对象内容或属性代际已改变，请刷新后重新编辑。");
        ValidateMetadataRewrite(profile, current, normalized);

        S3CannedACL? cannedAcl;
        try
        {
            var acl = await client.GetObjectAclAsync(new GetObjectAclRequest
            { BucketName = current.Bucket, Key = current.Key }, token).ConfigureAwait(false);
            cannedAcl = GetGcsPreservedCannedAcl(acl);
        }
        catch (AmazonS3Exception error) when (IsGcsUniformBucketAccess(error))
        {
            // Only the explicit Google UBLA error proves object ACLs disabled.
            cannedAcl = null;
        }

        var request = new CopyObjectRequest
        {
            SourceBucket = current.Bucket, SourceKey = current.Key,
            DestinationBucket = current.Bucket, DestinationKey = current.Key,
            MetadataDirective = S3MetadataDirective.REPLACE,
            CannedACL = cannedAcl,
            StorageClass = string.IsNullOrEmpty(current.StorageClass) ? null : S3StorageClass.FindValue(current.StorageClass)
        };
        // AWS HMAC interoperability accepts x-amz-copy-source/metadata-directive.
        // Google-only headers are added before marshalling/signing by the SDK.
        request.Headers["x-goog-copy-source-if-generation-match"] = current.ProviderGeneration;
        request.Headers["x-goog-copy-source-if-metageneration-match"] = current.ProviderMetageneration;
        request.Headers["x-goog-if-generation-match"] = current.ProviderGeneration;
        request.Headers["x-goog-if-metageneration-match"] = current.ProviderMetageneration;
        if (current.ProviderCustomTime is { Length: > 0 } customTime)
            request.Headers["x-goog-custom-time"] = customTime;
        ApplyWriteHeaders(request.Headers, request.Metadata, null, normalized);
        request.Headers.ContentLanguage = current.ContentLanguage;
        token.ThrowIfCancellationRequested();
        try
        {
            await client.CopyObjectAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or HttpRequestException or IOException or OperationCanceledException or Amazon.Runtime.AmazonClientException ||
            error is AmazonS3Exception s3 && ((int)s3.StatusCode == 0 || (int)s3.StatusCode >= 500 ||
                s3.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict))
        {
            throw new InvalidOperationException("Metadata 保存结果未确认或代际冲突；请重新读取对象核对，不要直接重试。", error);
        }
        // A successful HTTP copy response is not proof that an interoperability
        // layer honored every header. Read CURRENT without retrying the write.
        try
        {
            var saved = CreateObjectProperties(current.Bucket, current.Key,
                await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = current.Bucket, Key = current.Key }, token).ConfigureAwait(false));
            ValidateGcsSnapshot(saved);
            if (!MatchesGcsSavedMetadata(current, saved, normalized))
                throw new InvalidOperationException("读取的内容身份、属性代际或 Metadata 与预期不一致。");
        }
        catch (Exception error)
        {
            // Include cancellation, access errors, parse failures and mismatches:
            // the write has already returned success, so never imply no mutation.
            throw new InvalidOperationException("Metadata 写请求已提交但结果未确认；请核对当前对象，不要直接重试。", error);
        }
    }

    private static bool MatchesGcsSavedMetadata(ObjectProperties before, ObjectProperties saved, ObjectWriteHeaders edited) =>
        saved.ProviderGeneration == before.ProviderGeneration &&
        IsPositiveRevision(saved.ProviderMetageneration) && saved.ProviderMetageneration != before.ProviderMetageneration &&
        // Composite / XML-multipart ETags can change when only metadata changes.
        // Generation, not ETag equality, is Google's content identity token.
        saved.Size == before.Size && !string.IsNullOrEmpty(saved.ETag) &&
        saved.ContentType == edited.ContentType && saved.CacheControl == edited.CacheControl &&
        saved.ContentEncoding == edited.ContentEncoding && saved.ContentDisposition == edited.ContentDisposition &&
        saved.ExpiresUtc == edited.ExpiresUtc &&
        (string.IsNullOrEmpty(saved.ExpiresHeader) || saved.ExpiresUtc is not null) &&
        saved.Metadata.Count == edited.Metadata!.Count && edited.Metadata.All(pair =>
            saved.Metadata.TryGetValue(pair.Key, out var value) && value == pair.Value) &&
        saved.ContentLanguage == before.ContentLanguage && saved.StorageClass == before.StorageClass &&
        saved.ProviderCustomTime == before.ProviderCustomTime &&
        saved.ServerSideEncryptionMethod == before.ServerSideEncryptionMethod &&
        saved.EncryptionKeyId == before.EncryptionKeyId && saved.CustomerEncryptionMethod == before.CustomerEncryptionMethod &&
        saved.BucketKeyEnabled == before.BucketKeyEnabled &&
        saved.ObjectLockMode == before.ObjectLockMode && saved.ObjectLockLegalHoldStatus == before.ObjectLockLegalHoldStatus &&
        saved.ObjectLockRetainUntilDate == before.ObjectLockRetainUntilDate &&
        saved.ProviderRetentionUntil == before.ProviderRetentionUntil &&
        saved.MissingMetadataCount is not > 0;

    private static void ValidateGcsSnapshot(ObjectProperties snapshot)
    {
        if (!IsPositiveRevision(snapshot.ProviderGeneration) || !IsPositiveRevision(snapshot.ProviderMetageneration))
            throw new NotSupportedException("缺少有效的 GCS generation / metageneration，无法进行原子 Metadata 更新。");
        if (!string.IsNullOrEmpty(snapshot.ProviderRetentionUntil))
            throw new NotSupportedException("GCS 对象包含 Retention 配置，当前编辑流程不会改写该对象。");
        if (!string.IsNullOrEmpty(snapshot.ProviderCustomTime) &&
            !DateTimeOffset.TryParse(snapshot.ProviderCustomTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new NotSupportedException("GCS Custom-Time 无法解析，不能安全保留。");
        if (!string.IsNullOrEmpty(snapshot.WebsiteRedirectLocation))
            throw new NotSupportedException("GCS 对象含非标准重定向属性，不能无损原地编辑。");
    }

    private static bool IsPositiveRevision(string? value) =>
        value is { Length: > 0 } && value.All(c => c is >= '0' and <= '9') &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;

    internal static bool IsGcsUniformBucketAccess(AmazonS3Exception error) =>
        error.StatusCode == HttpStatusCode.BadRequest && error.ErrorCode == "InvalidArgument" &&
        error.Message.Contains("uniform bucket-level access is enabled", StringComparison.OrdinalIgnoreCase);

    internal static S3CannedACL GetGcsPreservedCannedAcl(GetObjectAclResponse acl)
    {
        // Exact canonical shapes only. Never turn arbitrary custom grants into a
        // canned ACL or restore ACLs with a second, non-atomic write.
        var owner = acl.Owner?.Id;
        if (string.IsNullOrEmpty(owner) || acl.Grants is not { Count: > 0 } grants)
            throw new NotSupportedException("无法读取 GCS 完整对象 ACL，已停止以避免改变访问授权。");
        var ownerGrants = grants.Where(g => g.Grantee?.CanonicalUser == owner &&
            string.IsNullOrEmpty(g.Grantee.EmailAddress) && string.IsNullOrEmpty(g.Grantee.URI) &&
            g.Permission == S3Permission.FULL_CONTROL).ToArray();
        if (ownerGrants.Length == 1)
        {
            if (grants.Count == 1) return S3CannedACL.Private;
            if (grants.Count == 2)
            {
                var other = grants.Single(g => !ReferenceEquals(g, ownerGrants[0]));
                if (other.Permission == S3Permission.READ && string.IsNullOrEmpty(other.Grantee?.CanonicalUser) &&
                    string.IsNullOrEmpty(other.Grantee?.EmailAddress))
                {
                    if (other.Grantee?.URI == "http://acs.amazonaws.com/groups/global/AllUsers") return S3CannedACL.PublicRead;
                    if (other.Grantee?.URI == "http://acs.amazonaws.com/groups/global/AuthenticatedUsers") return S3CannedACL.AuthenticatedRead;
                }
            }
        }
        throw new NotSupportedException("GCS 自定义对象 ACL 无法用同一请求精确保留；当前仅支持标准 ACL 或统一 Bucket 访问。");
    }
}
