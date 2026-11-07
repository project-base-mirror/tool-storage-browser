using System.Globalization;
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
        return CreateObjectProperties(bucket, key, response);
    }

    internal static ObjectProperties CreateObjectProperties(string bucket, string key, GetObjectMetadataResponse response)
    {
        // AWS SDK v4 HEAD stores the wire header in ExpiresString, not Headers.Expires.
        // Keep an unparseable raw value visible but prohibit snapshot writes using it.
        var rawExpires = response.ExpiresString ?? response.Headers["Expires"];
        DateTimeOffset? expiresUtc = DateTimeOffset.TryParse(rawExpires, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsedExpires) ? parsedExpires : null;
        string? Native(string suffix) => response.Headers["x-oss-" + suffix] ?? response.Headers["x-cos-" + suffix];
        var nativeRetention = Native("object-lock-retain-until-date") ?? Native("object-worm-retain-until-date");
        DateTimeOffset? parsedNativeRetention = DateTimeOffset.TryParse(nativeRetention, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var retention) ? retention : null;
        var nativeMissing = Native("missing-meta");
        var missingCount = nativeMissing is null ? response.MissingMeta :
            int.TryParse(nativeMissing, out var count) ? Math.Max(count, response.MissingMeta ?? 0) : 1;
        var metadata = ObjectMetadataValidator.ValidatePreservingValues(response.Metadata.Keys.ToDictionary(
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
            response.Headers["x-goog-storage-class"] ?? Native("storage-class") ?? response.StorageClass?.Value,
            Native("version-id") ?? response.VersionId,
            metadata,
            response.Headers.CacheControl,
            response.Headers.ContentEncoding,
            response.Headers.ContentDisposition,
            expiresUtc)
        {
            ServerSideEncryptionMethod = Native("server-side-encryption") ?? response.ServerSideEncryptionMethod?.Value,
            EncryptionKeyId = response.Headers["x-goog-encryption-kms-key-name"] ?? Native("server-side-encryption-key-id") ??
                Native("server-side-encryption-kms-key-id") ?? Native("server-side-encryption-cos-kms-key-id") ?? response.ServerSideEncryptionKeyManagementServiceKeyId,
            CustomerEncryptionMethod = response.Headers["x-goog-encryption-algorithm"] ?? Native("server-side-encryption-customer-algorithm") ??
                (Native("server-side-encryption-customer-key-md5") is not null ? "SSE-C" : null) ??
                (response.Headers["x-goog-encryption-key-sha256"] is not null ? "GCS-CSEK" : response.ServerSideEncryptionCustomerMethod),
            ObjectLockMode = response.Headers["x-goog-object-lock-mode"] ?? Native("object-lock-mode") ?? Native("object-worm-mode") ?? response.ObjectLockMode?.Value,
            ObjectLockLegalHoldStatus = Native("object-lock-legal-hold") ?? response.ObjectLockLegalHoldStatus?.Value,
            ContentLanguage = response.Headers.ContentLanguage,
            WebsiteRedirectLocation = Native("website-redirect-location") ?? response.WebsiteRedirectLocation,
            MissingMetadataCount = missingCount,
            BucketKeyEnabled = response.BucketKeyEnabled,
            ExpiresHeader = rawExpires,
            ProviderGeneration = response.Headers["x-goog-generation"],
            ProviderMetageneration = response.Headers["x-goog-metageneration"],
            ProviderCustomTime = response.Headers["x-goog-custom-time"],
            ProviderRetentionUntil = response.Headers["x-goog-object-lock-retain-until-date"] ?? nativeRetention,
            ProviderObjectType = Native("object-type"),
            ObjectLockRetainUntilDate = parsedNativeRetention ?? (response.ObjectLockRetainUntilDate is not { } retainUntilDate
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(retainUntilDate, DateTimeKind.Utc)))
        };
    }

    public async Task SaveObjectMetadataCopyAsync(
        ConnectionProfile profile, ObjectProperties source, string destinationKey,
        ObjectWriteHeaders headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMetadataCopy(profile, source, destinationKey, headers);
        using var client = _factory.Create(profile);
        await SaveObjectMetadataCopyAsync(client, profile, source, destinationKey, headers, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task SaveObjectMetadataCopyAsync(
        IAmazonS3 client, ConnectionProfile profile, ObjectProperties source, string destinationKey,
        ObjectWriteHeaders headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMetadataCopy(profile, source, destinationKey, headers);
        var normalized = headers.ValidateForMetadataCopy();

        // A version-pinned snapshot is intentional here. It is written to a DIFFERENT,
        // create-only key, never to the current source key. ETag alone is not a metadata
        // revision token and must never be used to re-enable in-place replacements.
        var snapshot = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = source.Bucket, Key = source.Key, VersionId = source.VersionId
        }, cancellationToken).ConfigureAwait(false);
        if (snapshot.VersionId != source.VersionId || snapshot.ETag != source.ETag ||
            snapshot.ContentLength != source.Size)
            throw new InvalidOperationException("源固定版本身份与打开对话框时不一致，已停止另存；请刷新后重试。");
        var actual = CreateObjectProperties(source.Bucket, source.Key, snapshot);
        ValidateMetadataCopy(profile, actual, destinationKey, normalized);

        // CopyObject does not copy ACLs. Restrict this workflow to buckets in which ACLs
        // are disabled instead of silently changing an object's access grants/owner.
        var ownership = await client.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest
        {
            BucketName = source.Bucket
        }, cancellationToken).ConfigureAwait(false);
        var rules = ownership.OwnershipControls?.Rules;
        if (rules is not { Count: 1 } || rules[0].ObjectOwnership?.Value != "BucketOwnerEnforced")
            throw new NotSupportedException("Metadata 快照另存要求 BucketOwnerEnforced（禁用 ACL）；不能确认时不会创建对象。");

        // HEAD omits retention/hold fields without the relevant permissions. Only an
        // explicit, known 'no configuration' response proves this bucket is not locked.
        try
        {
            await client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest
            {
                BucketName = source.Bucket
            }, cancellationToken).ConfigureAwait(false);
            throw new NotSupportedException("Bucket 存在或无法确认 Object Lock 配置，不能安全地另存 Metadata 快照。");
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.NotFound &&
            exception.ErrorCode == "ObjectLockConfigurationNotFoundError") { }

        var request = new CopyObjectRequest
        {
            SourceBucket = source.Bucket,
            SourceKey = source.Key,
            SourceVersionId = actual.VersionId,
            ETagToMatch = actual.ETag,
            DestinationBucket = source.Bucket,
            DestinationKey = destinationKey,
            IfNoneMatch = "*",
            MetadataDirective = S3MetadataDirective.REPLACE,
            TaggingDirective = TaggingDirective.COPY,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            CannedACL = S3CannedACL.BucketOwnerFullControl,
            StorageClass = S3StorageClass.FindValue(actual.StorageClass ?? "STANDARD"),
            WebsiteRedirectLocation = actual.WebsiteRedirectLocation
        };
        ApplyWriteHeaders(request.Headers, request.Metadata, null, normalized);
        request.Headers.ContentLanguage = actual.ContentLanguage;
        // Never fall back to unguarded COPY or multipart COPY: those paths have a
        // different preservation contract. Tags (and AWS annotations) copy server-side.
        try
        {
            await client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is
            System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
        {
            // A prior SDK attempt may have committed before its response was lost.
            // Even a later 412 is not proof that this operation never created a copy.
            throw new ObjectMetadataCopyException(ObjectMetadataCopyFailure.ConditionNotMet, exception);
        }
        catch (AmazonS3Exception exception) when (
            (int)exception.StatusCode == 0 || (int)exception.StatusCode >= 500)
        {
            throw new ObjectMetadataCopyException(ObjectMetadataCopyFailure.OutcomeUnknown, exception);
        }
        catch (AmazonS3Exception) { throw; }
        catch (Exception exception) when (exception is
            TimeoutException or HttpRequestException or IOException or OperationCanceledException or
            Amazon.Runtime.AmazonClientException)
        {
            throw new ObjectMetadataCopyException(ObjectMetadataCopyFailure.OutcomeUnknown, exception);
        }
    }

    private static void ValidateMetadataCopy(
        ConnectionProfile profile, ObjectProperties source, string destinationKey, ObjectWriteHeaders headers)
    {
        var capability = ObjectMetadataCopyPolicy.For(profile.ServiceType, source);
        if (!capability.Supported) throw new NotSupportedException(capability.Reason);
        ObjectMetadataCopyPolicy.ValidateDestination(source, destinationKey);
        if (headers.Tags is { Count: > 0 })
            throw new ArgumentException("Metadata 快照另存会由服务端保留源 Tags，不能在此操作中替换 Tags。", nameof(headers));
        _ = headers.ValidateForMetadataCopy();
    }

    public async Task<IReadOnlyList<ObjectTag>> GetObjectTagsAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging, "对象 Tags");
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
        EnsureBucketFeature(S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging, "对象 Tags");
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
        EnsureBucketFeature(S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.Tagging, "对象 Tags");
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
        EnsureBucketFeature(S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.ObjectLock, "Object Lock");
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
        EnsureBucketFeature(S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.ObjectLock, "Object Retention");
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
        EnsureBucketFeature(S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.ObjectLock, "Object Legal Hold");
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
        EnsureBucketFeature(
            S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.PresignedUrl,
            "预签名 URL");
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "有效期必须大于 0 且不超过 7 天。");
        MinioObjectKeyPolicy.Validate(profile.ServiceType, key);

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
