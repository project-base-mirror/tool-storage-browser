using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    public async Task<ObjectProperties> GetObjectPropertiesAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var response = await client.GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        var metadata = ObjectMetadataValidator.Validate(response.Metadata.Keys.ToDictionary(
            name => name,
            name => response.Metadata[name],
            StringComparer.OrdinalIgnoreCase));

        return new ObjectProperties(
            bucket,
            key,
            response.ContentLength,
            response.LastModified,
            response.ETag,
            response.Headers.ContentType,
            response.StorageClass?.Value,
            response.VersionId,
            metadata,
            NullIfWhiteSpace(response.Headers.CacheControl),
            NullIfWhiteSpace(response.Headers.ContentEncoding),
            NullIfWhiteSpace(response.Headers.ContentDisposition),
            response.Headers.Expires is not { } expiresUtc
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)));
    }

    public async Task ReplaceObjectMetadataAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        ObjectWriteHeaders headers,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(ObjectCapabilityMatrix.For(profile.ServiceType).MetadataRewrite, "对象 Metadata 替换");
        headers = headers.ValidateAndNormalize();
        using var client = _factory.Create(profile);
        var existing = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = NullIfWhiteSpace(versionId)
        }, cancellationToken).ConfigureAwait(false);
        if (existing.ContentLength > MaximumSingleCopyBytes)
            throw new InvalidOperationException(
                "超过 5 GiB 的对象不能通过单次原地 Copy 替换 Metadata；请重新上传对象，避免不完整的分片复制。");

        var request = new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = key,
            SourceVersionId = NullIfWhiteSpace(versionId),
            DestinationBucket = bucket,
            DestinationKey = key,
            MetadataDirective = S3MetadataDirective.REPLACE
        };
        var storageClass = S3CompatibilityPolicy.ResolveStorageClass(existing.StorageClass?.Value);
        if (storageClass is not null)
            request.StorageClass = storageClass;
        ApplyWriteHeaders(request.Headers, request.Metadata, null, headers);
        await client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ObjectTag>> GetObjectTagsAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(ObjectCapabilityMatrix.For(profile.ServiceType).Tagging, "对象 Tags");
        using var client = _factory.Create(profile);
        var response = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = NullIfWhiteSpace(versionId)
        }, cancellationToken).ConfigureAwait(false);
        return (response.Tagging ?? [])
            .Select(tag => new ObjectTag(tag.Key, tag.Value))
            .OrderBy(tag => tag.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task PutObjectTagsAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        IReadOnlyCollection<ObjectTag> tags,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(ObjectCapabilityMatrix.For(profile.ServiceType).Tagging, "对象 Tags");
        var validated = ObjectTagValidator.Validate(tags);
        if (validated.Count == 0)
        {
            await DeleteObjectTagsAsync(profile, bucket, key, versionId, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var client = _factory.Create(profile);
        await client.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = NullIfWhiteSpace(versionId),
            Tagging = new Tagging
            {
                TagSet = validated.Select(tag => new Tag { Key = tag.Key, Value = tag.Value }).ToList()
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteObjectTagsAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(ObjectCapabilityMatrix.For(profile.ServiceType).Tagging, "对象 Tags");
        using var client = _factory.Create(profile);
        await client.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = NullIfWhiteSpace(versionId)
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObjectLockSnapshot> GetObjectLockAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).ObjectLock, "Object Lock");
        using var client = _factory.Create(profile);
        ObjectLockRetention? retention = null;
        ObjectLockLegalHold? legalHold = null;
        try
        {
            var response = await client.GetObjectRetentionAsync(new GetObjectRetentionRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = NullIfWhiteSpace(versionId)
            }, cancellationToken).ConfigureAwait(false);
            retention = response.Retention;
        }
        catch (AmazonS3Exception exception) when (IsMissingObjectLockState(exception)) { }

        try
        {
            var response = await client.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = NullIfWhiteSpace(versionId)
            }, cancellationToken).ConfigureAwait(false);
            legalHold = response.LegalHold;
        }
        catch (AmazonS3Exception exception) when (IsMissingObjectLockState(exception)) { }

        return new ObjectLockSnapshot(
            FromSdkRetentionMode(retention?.Mode),
            retention?.RetainUntilDate is not { } retainUntilDate || retainUntilDate == default
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(retainUntilDate, DateTimeKind.Utc)),
            string.Equals(legalHold?.Status?.Value, ObjectLockLegalHoldStatus.On.Value, StringComparison.Ordinal),
            NullIfWhiteSpace(versionId));
    }

    public async Task PutObjectRetentionAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        ObjectRetentionConfiguration retention,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).ObjectLock, "Object Retention");
        var current = await GetObjectLockAsync(profile, bucket, key, versionId, cancellationToken)
            .ConfigureAwait(false);
        retention.Validate(current);
        using var client = _factory.Create(profile);
        await client.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = NullIfWhiteSpace(versionId),
            BypassGovernanceRetention = false,
            Retention = new ObjectLockRetention
            {
                Mode = retention.Mode == ObjectRetentionMode.Compliance
                    ? ObjectLockRetentionMode.Compliance
                    : ObjectLockRetentionMode.Governance,
                RetainUntilDate = retention.RetainUntilDate.UtcDateTime
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PutObjectLegalHoldAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).ObjectLock, "Object Legal Hold");
        using var client = _factory.Create(profile);
        await client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = NullIfWhiteSpace(versionId),
            LegalHold = new ObjectLockLegalHold
            {
                Status = enabled ? ObjectLockLegalHoldStatus.On : ObjectLockLegalHoldStatus.Off
            }
        }, cancellationToken).ConfigureAwait(false);
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

}
