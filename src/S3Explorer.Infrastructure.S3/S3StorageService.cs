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
        var credentialSource = profile.CredentialSourceDisplayName;
        using var connectionTimeout = CreateConnectionTimeout(profile, cancellationToken);
        try
        {
            var creation = _factory.CreateResolved(profile);
            credentialSource = creation.CredentialResolution.DisplayName;
            using var client = creation.Client;
            var response = await client.ListBucketsAsync(connectionTimeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var bucketCount = response.Buckets
                .Select(bucket => bucket.BucketName)
                .Concat(profile.KnownBuckets)
                .Distinct(StringComparer.Ordinal)
                .Count();
            return new(true, stopwatch.Elapsed, bucketCount,
                $"连接成功，发现或配置了 {bucketCount} 个 Bucket。",
                CredentialSource: creation.CredentialResolution.DisplayName);
        }
        catch (AmazonS3Exception ex) when (S3CompatibilityPolicy.IsRestrictedListBuckets(ex))
        {
            stopwatch.Stop();
            var configuredCount = profile.KnownBuckets.Count;
            var message = configuredCount > 0
                ? $"已到达 S3 服务；当前凭据无权列出全部 Bucket，将使用已配置的 {configuredCount} 个 Bucket。"
                : "已到达 S3 服务，但当前凭据无权列出全部 Bucket。请配置默认 Bucket 或外部 Bucket。";
            return new(true, stopwatch.Elapsed, configuredCount, message, (int)ex.StatusCode, ex.ErrorCode, ex.RequestId,
                credentialSource);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && connectionTimeout.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(false, stopwatch.Elapsed, 0, $"连接超时（{profile.ConnectionTimeoutSeconds} 秒）。", null, "ConnectionTimeout",
                CredentialSource: credentialSource);
        }
        catch (AmazonS3Exception ex)
        {
            stopwatch.Stop();
            var reachedServer = ex.StatusCode != 0;
            var message = reachedServer
                ? $"请求已到达服务器，但操作失败：{ex.ErrorCode} - {ex.Message}"
                : ex.Message;
            return new(false, stopwatch.Elapsed, 0, message, (int)ex.StatusCode, ex.ErrorCode, ex.RequestId,
                credentialSource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new(false, stopwatch.Elapsed, 0, ex.Message, CredentialSource: credentialSource);
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
        var response = await client.GetACLAsync(new GetACLRequest
        {
            BucketName = bucket
        }, cancellationToken).ConfigureAwait(false);
        var grants = response.AccessControlList.Grants
            .Select(grant => new BucketAclGrant(
                grant.Grantee?.DisplayName ?? grant.Grantee?.URI ?? grant.Grantee?.EmailAddress ?? "未知主体",
                grant.Permission?.Value ?? "未知权限"))
            .ToArray();
        var publicRead = grants.Any(grant =>
            grant.Permission.Contains("READ", StringComparison.OrdinalIgnoreCase) &&
            grant.Grantee.Contains("AllUsers", StringComparison.OrdinalIgnoreCase));
        var owner = response.AccessControlList.Owner?.DisplayName ??
            response.AccessControlList.Owner?.Id ?? "未知";
        return new BucketAclSnapshot(
            owner, publicRead ? BucketAclMode.PublicRead : BucketAclMode.Private, grants);
    }

    public async Task PutBucketAclAsync(
        ConnectionProfile profile, string bucket, BucketAclMode mode, CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Acl, "Bucket ACL");
        using var client = _factory.Create(profile);
        await client.PutACLAsync(new PutACLRequest
        {
            BucketName = bucket,
            CannedACL = mode == BucketAclMode.PublicRead
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
                    value.BlockPublicAcls, value.IgnorePublicAcls,
                    value.BlockPublicPolicy, value.RestrictPublicBuckets);
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
                    rule.Id, rule.AllowedOrigins.ToArray(), rule.AllowedMethods.ToArray(),
                    rule.AllowedHeaders.ToArray(), rule.ExposeHeaders.ToArray(),
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
            return BucketTagValidator.Validate(response.TagSet.Select(tag =>
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
            objectCount += page.S3Objects.Count;
            totalBytes += page.S3Objects.Sum(item => item.Size);
            continuationToken = page.IsTruncated ? page.NextContinuationToken : null;
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
            var request = new DeleteObjectsRequest { BucketName = bucket };
            request.Objects.AddRange(batch);
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
            if (page.S3Objects.Count == 0)
                break;

            var request = new DeleteObjectsRequest { BucketName = bucket };
            request.Objects.AddRange(page.S3Objects.Select(item => new KeyVersion { Key = item.Key }));
            await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
            deletedObjects += page.S3Objects.Count;
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

    public async Task<PagedObjectVersionResult> ListObjectVersionsAsync(
        ConnectionProfile profile,
        string bucket,
        string prefix,
        string? keyMarker,
        string? versionIdMarker,
        int pageSize,
        CancellationToken cancellationToken)
    {
        EnsureBucketFeature(BucketCapabilityMatrix.For(profile.ServiceType).Versioning, "对象版本列表");
        using var client = _factory.Create(profile);
        var response = await client.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = bucket,
            Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
            KeyMarker = string.IsNullOrWhiteSpace(keyMarker) ? null : keyMarker,
            VersionIdMarker = string.IsNullOrWhiteSpace(versionIdMarker) ? null : versionIdMarker,
            MaxKeys = Math.Clamp(pageSize, 1, 1000)
        }, cancellationToken).ConfigureAwait(false);
        var items = response.Versions.Select(item => new ObjectVersionEntry(
            item.Key,
            item.VersionId ?? string.Empty,
            item.IsLatest,
            item.IsDeleteMarker,
            item.IsDeleteMarker ? 0 : item.Size,
            item.LastModified,
            item.IsDeleteMarker ? null : item.ETag,
            item.IsDeleteMarker ? string.Empty : item.StorageClass?.Value ?? "STANDARD"))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ThenByDescending(item => item.LastModified)
            .ToArray();
        if (response.IsTruncated && string.IsNullOrWhiteSpace(response.NextKeyMarker))
            throw new InvalidOperationException("ListObjectVersions 返回了无效的下一页 Key Marker。");
        return new PagedObjectVersionResult(
            items,
            response.IsTruncated ? response.NextKeyMarker : null,
            response.IsTruncated ? response.NextVersionIdMarker : null,
            response.IsTruncated);
    }

    public async Task UploadFileAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        string storageClass,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken)
    {
        transferContext.Options.Validate();
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

                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = throttled,
                    AutoCloseStream = false,
                    StorageClass = S3StorageClass.FindValue(storageClass)
                }, cancellationToken).ConfigureAwait(false);
                transferContext.ReportProgress(new TransferProgress(file.Length, file.Length));
                return;
            }

            await UploadMultipartFileAsync(
                client, bucket, key, file, storageClass, transferContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToTransferException(exception);
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
                    ByteRange = completed > 0 ? new ByteRange(completed, remote.Length - 1) : null
                };
                using var response = await client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
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
                    var read = await response.ResponseStream
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

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

            foreach (var upload in response.MultipartUploads)
            {
                var initiated = new DateTimeOffset(upload.Initiated.ToUniversalTime());
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

            keyMarker = response.IsTruncated ? response.NextKeyMarker : null;
            uploadIdMarker = response.IsTruncated ? response.NextUploadIdMarker : null;
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

    public async Task DeleteObjectVersionAsync(
        ConnectionProfile profile, string bucket, string key, string versionId,
        CancellationToken cancellationToken)
    {
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
            var request = new DeleteObjectsRequest { BucketName = bucket };
            request.Objects.AddRange(batch.Select(item => new KeyVersion
            {
                Key = item.Key,
                VersionId = item.VersionId
            }));
            var response = await client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.DeleteErrors.Count > 0)
            {
                var first = response.DeleteErrors[0];
                throw new InvalidOperationException(
                    $"永久删除对象版本时有 {response.DeleteErrors.Count:N0} 项失败；首项 Key={first.Key}，VersionId={first.VersionId}，Code={first.Code}。");
            }
        }
    }

    public async Task RestoreObjectVersionAsync(
        ConnectionProfile profile, string bucket, string key, string versionId,
        CancellationToken cancellationToken)
    {
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

    private static async Task UploadMultipartFileAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        FileInfo file,
        string storageClass,
        TransferOperationContext transferContext,
        CancellationToken cancellationToken)
    {
        var partSize = transferContext.Options.PartSizeBytes;
        var modified = new DateTimeOffset(file.LastWriteTimeUtc);
        var checkpoint = transferContext.MultipartCheckpoint;

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
            var initiated = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                StorageClass = S3StorageClass.FindValue(storageClass)
            }, cancellationToken).ConfigureAwait(false);
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
            UploadId = uploadId
        };
        complete.AddPartETags(completedParts.Values
            .OrderBy(part => part.PartNumber)
            .Select(part => new PartETag(part.PartNumber, part.ETag)));
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
            parts.AddRange(response.Parts.Select(part =>
                new MultipartPartCheckpoint(part.PartNumber, part.ETag, part.Size)));
            if (!response.IsTruncated) break;
            var nextMarker = response.Parts.Count == 0
                ? marker
                : response.Parts[^1].PartNumber.ToString();
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
                versions += response.Versions.LongCount(item => !item.IsDeleteMarker);
                markers += response.Versions.LongCount(item => item.IsDeleteMarker);
                more = response.IsTruncated;
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
                items.AddRange(response.Versions.Select(item =>
                    new KeyVersion { Key = item.Key, VersionId = item.VersionId }));
                versions += response.Versions.LongCount(item => !item.IsDeleteMarker);
                markers += response.Versions.LongCount(item => item.IsDeleteMarker);
                more = response.IsTruncated;
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
