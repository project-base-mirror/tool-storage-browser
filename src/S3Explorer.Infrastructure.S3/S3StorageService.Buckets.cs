using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    public async Task CreateBucketAsync(ConnectionProfile profile, string bucket, string region, CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);
        using var client = _factory.Create(profile);
        var request = S3CompatibilityPolicy.CreateBucketRequest(profile, bucket, region);
        try
        {
            await client.PutBucketAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (S3CompatibilityPolicy.IsMinioEndpointRoutingError(profile, exception))
        {
            throw new InvalidOperationException(
                "MinIO Endpoint 未正确路由到 S3 API。请使用 API 根地址（默认端口 9000），不要填写 Console 端口、/browser、/login 或其他路径前缀。",
                exception);
        }
    }

    public async Task DeleteEmptyBucketAsync(ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        var check = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            MaxKeys = 1
        }, cancellationToken).ConfigureAwait(false);

        if ((check.S3Objects?.Count ?? 0) > 0 || (check.CommonPrefixes?.Count ?? 0) > 0)
            throw new InvalidOperationException("Bucket 非空，默认不允许删除。");

        var uploads = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = bucket,
            MaxUploads = 1
        }, cancellationToken).ConfigureAwait(false);
        if ((uploads.MultipartUploads?.Count ?? 0) > 0)
            throw new InvalidOperationException("Bucket 存在未完成的分片上传，不能删除。");

        await client.DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BucketPropertiesSnapshot> GetBucketPropertiesAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        var capabilities = BucketCapabilityMatrix.For(profile.ServiceType);
        var policy = capabilities.Policy.Supported
            ? await GetBucketPolicyAsync(profile, bucket, cancellationToken).ConfigureAwait(false)
            : null;
        var acl = capabilities.Acl.Supported
            ? await GetBucketAclAsync(profile, bucket, cancellationToken).ConfigureAwait(false)
            : new BucketAclSnapshot("未查询", BucketAclMode.Private, []);
        var publicAccessBlock = capabilities.PublicAccessBlock.Supported
            ? await GetBucketPublicAccessBlockAsync(profile, bucket, cancellationToken).ConfigureAwait(false)
            : null;
        var objectOwnership = capabilities.ObjectOwnership.Supported
            ? await GetBucketObjectOwnershipAsync(profile, bucket, cancellationToken).ConfigureAwait(false)
            : null;
        var versioning = capabilities.Versioning.Supported
            ? (await GetBucketVersioningAsync(profile, bucket, cancellationToken).ConfigureAwait(false)).ToString()
            : capabilities.Versioning.Reason;
        var encryption = capabilities.Encryption.Supported
            ? (await GetBucketEncryptionAsync(profile, bucket, cancellationToken).ConfigureAwait(false)).Summary
            : capabilities.Encryption.Reason;
        return new BucketPropertiesSnapshot(
            bucket, profile.Endpoint, profile.ServiceType, profile.EffectiveSignatureRegion,
            versioning, encryption,
            policy is not null, acl, publicAccessBlock, objectOwnership, capabilities);
    }

    public async Task<string?> GetBucketPolicyAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Policy, "Bucket Policy");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetBucketPolicyAsync(new GetBucketPolicyRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
            if (response.HttpStatusCode == HttpStatusCode.NotFound)
                return null;
            return string.IsNullOrWhiteSpace(response.Policy)
                ? null
                : BucketPolicyDocument.ValidateAndNormalize(response.Policy);
        }
        catch (AmazonS3Exception exception) when (IsMissingBucketPolicy(exception))
        {
            return null;
        }
    }

    public async Task PutBucketPolicyAsync(
        ConnectionProfile profile, string bucket, string policyJson, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Policy, "Bucket Policy");
        var normalized = BucketPolicyDocument.ValidateAndNormalize(policyJson);
        using var client = _factory.Create(profile);
        await client.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = bucket,
            Policy = normalized
        }, cancellationToken).ConfigureAwait(false);
        var readBack = await client.GetBucketPolicyAsync(new GetBucketPolicyRequest
        {
            BucketName = bucket
        }, cancellationToken).ConfigureAwait(false);
        if (!BucketPolicyDocument.AreSemanticallyEquivalent(readBack.Policy, normalized))
            throw new InvalidOperationException("Bucket Policy 保存后回读内容不一致。");
    }

    public async Task DeleteBucketPolicyAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Policy, "Bucket Policy");
        using var client = _factory.Create(profile);
        try
        {
            await client.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsMissingBucketPolicy(exception))
        {
        }
    }

    public async Task<BucketAclSnapshot> GetBucketAclAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Acl, "Bucket ACL");
        using var client = _factory.Create(profile);
        var response = await client.GetBucketAclAsync(new GetBucketAclRequest
        {
            BucketName = bucket
        }, cancellationToken).ConfigureAwait(false);
        var grants = (response.Grants ?? [])
            .Select(grant => new BucketAclGrant(
                grant.Grantee?.DisplayName ?? grant.Grantee?.URI ?? grant.Grantee?.EmailAddress ?? "未知主体",
                grant.Permission?.Value ?? "未知权限"))
            .ToArray();
        var publicRead = grants.Any(grant =>
            grant.Permission.Contains("READ", StringComparison.OrdinalIgnoreCase) &&
            grant.Grantee.Contains("AllUsers", StringComparison.OrdinalIgnoreCase));
        var owner = response.Owner?.DisplayName ?? response.Owner?.Id ?? "未知";
        return new BucketAclSnapshot(
            owner, publicRead ? BucketAclMode.PublicRead : BucketAclMode.Private, grants);
    }

    public async Task PutBucketAclAsync(
        ConnectionProfile profile, string bucket, BucketAclMode mode, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Acl, "Bucket ACL");
        using var client = _factory.Create(profile);
        await client.PutBucketAclAsync(new PutBucketAclRequest
        {
            BucketName = bucket,
            ACL = mode == BucketAclMode.PublicRead
                ? S3CannedACL.PublicRead
                : S3CannedACL.Private
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BucketPublicAccessBlockSnapshot?> GetBucketPublicAccessBlockAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            BucketCapabilityMatrix.For(profile.ServiceType).PublicAccessBlock,
            "Public Access Block");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
            var value = response.PublicAccessBlockConfiguration;
            return value is null
                ? null
                : new BucketPublicAccessBlockSnapshot(
                    value.BlockPublicAcls.GetValueOrDefault(), value.IgnorePublicAcls.GetValueOrDefault(),
                    value.BlockPublicPolicy.GetValueOrDefault(), value.RestrictPublicBuckets.GetValueOrDefault());
        }
        catch (AmazonS3Exception exception) when (IsMissingPublicAccessBlock(exception))
        {
            return null;
        }
    }

    public async Task PutBucketPublicAccessBlockAsync(
        ConnectionProfile profile, string bucket,
        BucketPublicAccessBlockSnapshot configuration, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            BucketCapabilityMatrix.For(profile.ServiceType).PublicAccessBlock,
            "Public Access Block");
        using var client = _factory.Create(profile);
        await client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = configuration.BlockPublicAcls,
                IgnorePublicAcls = configuration.IgnorePublicAcls,
                BlockPublicPolicy = configuration.BlockPublicPolicy,
                RestrictPublicBuckets = configuration.RestrictPublicBuckets
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BucketObjectOwnershipMode?> GetBucketObjectOwnershipAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            BucketCapabilityMatrix.For(profile.ServiceType).ObjectOwnership,
            "Object Ownership");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetBucketOwnershipControlsAsync(
                new GetBucketOwnershipControlsRequest { BucketName = bucket },
                cancellationToken).ConfigureAwait(false);
            var value = response.OwnershipControls?.Rules?.FirstOrDefault()?.ObjectOwnership?.Value;
            return value switch
            {
                "BucketOwnerEnforced" => BucketObjectOwnershipMode.BucketOwnerEnforced,
                "BucketOwnerPreferred" => BucketObjectOwnershipMode.BucketOwnerPreferred,
                "ObjectWriter" => BucketObjectOwnershipMode.ObjectWriter,
                _ => null
            };
        }
        catch (AmazonS3Exception exception) when (IsMissingOwnershipControls(exception))
        {
            return null;
        }
    }

    public async Task PutBucketObjectOwnershipAsync(
        ConnectionProfile profile, string bucket,
        BucketObjectOwnershipMode mode, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(
            BucketCapabilityMatrix.For(profile.ServiceType).ObjectOwnership,
            "Object Ownership");
        using var client = _factory.Create(profile);
        await client.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls
            {
                Rules =
                [
                    new OwnershipControlsRule
                    {
                        ObjectOwnership = mode switch
                        {
                            BucketObjectOwnershipMode.BucketOwnerEnforced => ObjectOwnership.BucketOwnerEnforced,
                            BucketObjectOwnershipMode.BucketOwnerPreferred => ObjectOwnership.BucketOwnerPreferred,
                            BucketObjectOwnershipMode.ObjectWriter => ObjectOwnership.ObjectWriter,
                            _ => throw new ArgumentOutOfRangeException(nameof(mode))
                        }
                    }
                ]
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BucketCorsConfiguration> GetBucketCorsAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Cors, "Bucket CORS");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetCORSConfigurationAsync(new GetCORSConfigurationRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
            return BucketCorsDocument.Validate(new BucketCorsConfiguration(
                (response.Configuration?.Rules ?? []).Select(rule => new BucketCorsRule(
                    rule.Id, (rule.AllowedOrigins ?? []).ToArray(), (rule.AllowedMethods ?? []).ToArray(),
                    (rule.AllowedHeaders ?? []).ToArray(), (rule.ExposeHeaders ?? []).ToArray(),
                    rule.MaxAgeSeconds > 0 ? rule.MaxAgeSeconds : null)).ToArray()));
        }
        catch (AmazonS3Exception exception) when (IsMissingCors(exception))
        {
            return new BucketCorsConfiguration([]);
        }
    }

    public async Task PutBucketCorsAsync(
        ConnectionProfile profile, string bucket, BucketCorsConfiguration configuration,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Cors, "Bucket CORS");
        var normalized = BucketCorsDocument.Validate(configuration);
        if (normalized.Rules.Count == 0)
            throw new ArgumentException("CORS 规则为空时请使用删除配置。", nameof(configuration));
        using var client = _factory.Create(profile);
        await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules = normalized.Rules.Select(rule => new CORSRule
                {
                    Id = rule.Id,
                    AllowedOrigins = rule.AllowedOrigins.ToList(),
                    AllowedMethods = rule.AllowedMethods.ToList(),
                    AllowedHeaders = rule.AllowedHeaders.ToList(),
                    ExposeHeaders = rule.ExposeHeaders.ToList(),
                    MaxAgeSeconds = rule.MaxAgeSeconds ?? 0
                }).ToList()
            }
        }, cancellationToken).ConfigureAwait(false);
        var readBack = await GetBucketCorsAsync(profile, bucket, cancellationToken).ConfigureAwait(false);
        if (!BucketCorsDocument.AreSemanticallyEquivalent(normalized, readBack))
            throw new InvalidOperationException("Bucket CORS 保存后回读内容不一致。");
    }

    public async Task DeleteBucketCorsAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Cors, "Bucket CORS");
        using var client = _factory.Create(profile);
        try
        {
            await client.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsMissingCors(exception)) { }
    }

    public async Task<BucketVersioningState> GetBucketVersioningAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Versioning, "Bucket Versioning");
        using var client = _factory.Create(profile);
        var response = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest
        {
            BucketName = bucket
        }, cancellationToken).ConfigureAwait(false);
        return response.VersioningConfig?.Status?.Value switch
        {
            "Enabled" => BucketVersioningState.Enabled,
            "Suspended" => BucketVersioningState.Suspended,
            _ => BucketVersioningState.Disabled
        };
    }

    public async Task PutBucketVersioningAsync(
        ConnectionProfile profile, string bucket, BucketVersioningState state,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Versioning, "Bucket Versioning");
        if (state == BucketVersioningState.Disabled)
            throw new ArgumentException("版本控制启用后不能恢复到从未启用状态，只能暂停。", nameof(state));
        using var client = _factory.Create(profile);
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig
            {
                Status = state == BucketVersioningState.Enabled
                    ? VersionStatus.Enabled
                    : VersionStatus.Suspended
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BucketEncryptionConfiguration> GetBucketEncryptionAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Encryption, "Bucket 默认加密");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetBucketEncryptionAsync(new GetBucketEncryptionRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
            var value = response.ServerSideEncryptionConfiguration?.ServerSideEncryptionRules?
                .FirstOrDefault()?.ServerSideEncryptionByDefault;
            return value?.ServerSideEncryptionAlgorithm?.Value switch
            {
                "aws:kms" or "aws:kms:dsse" => new BucketEncryptionConfiguration(
                    BucketEncryptionMode.SseKms,
                    value.ServerSideEncryptionKeyManagementServiceKeyId),
                "AES256" => new BucketEncryptionConfiguration(BucketEncryptionMode.SseS3),
                _ => new BucketEncryptionConfiguration(BucketEncryptionMode.None)
            };
        }
        catch (AmazonS3Exception exception) when (IsMissingEncryption(exception))
        {
            return new BucketEncryptionConfiguration(BucketEncryptionMode.None);
        }
    }

    public async Task PutBucketEncryptionAsync(
        ConnectionProfile profile, string bucket, BucketEncryptionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var capabilities = BucketCapabilityMatrix.For(profile.ServiceType);
        EnsureBucketFeature(capabilities.Encryption, "Bucket 默认加密");
        configuration.Validate(capabilities.KmsEncryption.Supported);
        if (configuration.Mode == BucketEncryptionMode.None)
            throw new ArgumentException("未配置加密时请使用删除配置。", nameof(configuration));
        using var client = _factory.Create(profile);
        await client.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
        {
            BucketName = bucket,
            ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules =
                [
                    new ServerSideEncryptionRule
                    {
                        ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault
                        {
                            ServerSideEncryptionAlgorithm = configuration.Mode == BucketEncryptionMode.SseS3
                                ? ServerSideEncryptionMethod.AES256
                                : ServerSideEncryptionMethod.AWSKMS,
                            ServerSideEncryptionKeyManagementServiceKeyId = configuration.Mode == BucketEncryptionMode.SseKms
                                ? configuration.KmsKeyId
                                : null
                        }
                    }
                ]
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBucketEncryptionAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Encryption, "Bucket 默认加密");
        using var client = _factory.Create(profile);
        try
        {
            await client.DeleteBucketEncryptionAsync(new DeleteBucketEncryptionRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsMissingEncryption(exception)) { }
    }

    public async Task<IReadOnlyList<BucketTag>> GetBucketTagsAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Tagging, "Bucket Tagging");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetBucketTaggingAsync(new GetBucketTaggingRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
            return BucketTagValidator.Validate((response.TagSet ?? []).Select(tag =>
                new BucketTag(tag.Key, tag.Value)));
        }
        catch (AmazonS3Exception exception) when (IsMissingTagSet(exception))
        {
            return [];
        }
    }

    public async Task PutBucketTagsAsync(
        ConnectionProfile profile, string bucket, IReadOnlyCollection<BucketTag> tags,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Tagging, "Bucket Tagging");
        var normalized = BucketTagValidator.Validate(tags);
        if (normalized.Count == 0)
            throw new ArgumentException("Tag 为空时请使用删除配置。", nameof(tags));
        using var client = _factory.Create(profile);
        await client.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet = normalized.Select(tag => new Amazon.S3.Model.Tag
            {
                Key = tag.Key,
                Value = tag.Value
            }).ToList()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBucketTagsAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Tagging, "Bucket Tagging");
        using var client = _factory.Create(profile);
        try
        {
            await client.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest
            {
                BucketName = bucket
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsMissingTagSet(exception)) { }
    }

    public async Task<BucketLifecycleConfiguration> GetBucketLifecycleAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        var capabilities = BucketCapabilityMatrix.For(profile.ServiceType);
        EnsureBucketFeature(capabilities.Lifecycle, "Bucket 生命周期");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetLifecycleConfigurationAsync(
                new GetLifecycleConfigurationRequest { BucketName = bucket }, cancellationToken)
                .ConfigureAwait(false);
            var configuration = S3LifecycleMapper.ToCore(response.Configuration);
            return BucketLifecycleDocument.Validate(
                configuration,
                capabilities.LifecycleStorageTransitions.Supported,
                capabilities.LifecycleMultipartCleanup.Supported);
        }
        catch (AmazonS3Exception exception) when (IsMissingLifecycle(exception))
        {
            return new BucketLifecycleConfiguration([]);
        }
    }

    public async Task PutBucketLifecycleAsync(
        ConnectionProfile profile,
        string bucket,
        BucketLifecycleConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var capabilities = BucketCapabilityMatrix.For(profile.ServiceType);
        EnsureBucketFeature(capabilities.Lifecycle, "Bucket 生命周期");
        var normalized = BucketLifecycleDocument.Validate(
            configuration,
            capabilities.LifecycleStorageTransitions.Supported,
            capabilities.LifecycleMultipartCleanup.Supported);
        if (normalized.Rules.Count == 0)
            throw new ArgumentException("生命周期规则为空时请使用删除配置。", nameof(configuration));
        using var client = _factory.Create(profile);
        await client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = S3LifecycleMapper.ToSdk(normalized)
        }, cancellationToken).ConfigureAwait(false);
        var readBack = await GetBucketLifecycleAsync(profile, bucket, cancellationToken).ConfigureAwait(false);
        if (!BucketLifecycleDocument.AreSemanticallyEquivalent(
                normalized, readBack,
                capabilities.LifecycleStorageTransitions.Supported,
                capabilities.LifecycleMultipartCleanup.Supported))
            throw new InvalidOperationException(
                "Bucket 生命周期保存后回读内容不一致。" +
                $"\r\n提交：{BucketLifecycleDocument.Serialize(normalized, capabilities.LifecycleStorageTransitions.Supported, capabilities.LifecycleMultipartCleanup.Supported)}" +
                $"\r\n回读：{BucketLifecycleDocument.Serialize(readBack, capabilities.LifecycleStorageTransitions.Supported, capabilities.LifecycleMultipartCleanup.Supported)}");
    }

    public async Task DeleteBucketLifecycleAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Lifecycle, "Bucket 生命周期");
        using var client = _factory.Create(profile);
        try
        {
            await client.DeleteLifecycleConfigurationAsync(
                new DeleteLifecycleConfigurationRequest { BucketName = bucket }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsMissingLifecycle(exception)) { }
    }

    public async Task<BucketObjectLockSnapshot> GetBucketObjectLockAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).ObjectLock, "Object Lock");
        using var client = _factory.Create(profile);
        try
        {
            var response = await client.GetObjectLockConfigurationAsync(
                new GetObjectLockConfigurationRequest { BucketName = bucket }, cancellationToken)
                .ConfigureAwait(false);
            var configuration = response.ObjectLockConfiguration;
            var enabled = string.Equals(
                configuration?.ObjectLockEnabled?.Value,
                ObjectLockEnabled.Enabled.Value,
                StringComparison.Ordinal);
            var retention = configuration?.Rule?.DefaultRetention;
            return new BucketObjectLockSnapshot(
                enabled,
                FromSdkRetentionMode(retention?.Mode),
                retention?.Days is > 0 ? retention.Days : null,
                retention?.Years is > 0 ? retention.Years : null);
        }
        catch (AmazonS3Exception exception) when (IsMissingObjectLockConfiguration(exception))
        {
            return new BucketObjectLockSnapshot(false);
        }
    }

    public async Task<BucketEmptySummary> ScanBucketAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        long objectCount = 0;
        long totalBytes = 0;
        string? continuationToken = null;
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                ContinuationToken = continuationToken,
                MaxKeys = 1000
            }, cancellationToken).ConfigureAwait(false);
            var objects = page.S3Objects ?? [];
            objectCount += objects.Count;
            totalBytes += objects.Sum(item => item.Size.GetValueOrDefault());
            continuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (continuationToken is not null);

        var versionScan = await ScanVersionsAsync(client, bucket, cancellationToken).ConfigureAwait(false);
        var uploads = await ListIncompleteMultipartUploadsAsync(
            profile, bucket, null, null, cancellationToken).ConfigureAwait(false);
        return new BucketEmptySummary(
            objectCount, versionScan.Versions, versionScan.DeleteMarkers,
            uploads.Count, totalBytes, versionScan.Supported);
    }

    public async Task<BucketEmptyResult> EmptyBucketAsync(
        ConnectionProfile profile, string bucket, CancellationToken cancellationToken)
    {
        using var client = _factory.Create(profile);
        long deletedVersions = 0;
        long deletedMarkers = 0;
        var versionScan = await CollectVersionsAsync(client, bucket, cancellationToken).ConfigureAwait(false);
        foreach (var batch in versionScan.Items.Chunk(1000))
        {
            var request = new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = batch.ToList()
            };
            await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        deletedVersions = versionScan.VersionCount;
        deletedMarkers = versionScan.DeleteMarkerCount;

        long deletedObjects = 0;
        while (true)
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                MaxKeys = 1000
            }, cancellationToken).ConfigureAwait(false);
            var objects = page.S3Objects ?? [];
            if (objects.Count == 0)
                break;

            var request = new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = objects.Select(item => new KeyVersion { Key = item.Key }).ToList()
            };
            await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
            deletedObjects += objects.Count;
        }

        var uploads = await ListIncompleteMultipartUploadsAsync(
            profile, bucket, null, null, cancellationToken).ConfigureAwait(false);
        foreach (var upload in uploads)
            await AbortMultipartUploadAsync(
                profile, upload.Bucket, upload.ObjectKey, upload.UploadId, cancellationToken)
                .ConfigureAwait(false);
        return new BucketEmptyResult(
            deletedObjects, deletedVersions, deletedMarkers, uploads.Count);
    }

}
