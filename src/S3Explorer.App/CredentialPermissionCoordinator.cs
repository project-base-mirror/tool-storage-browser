using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

/// <summary>
/// Runs non-destructive, scope-aware permission checks for every configuration that references
/// a credential. Destructive S3 write/delete probes remain an explicit CLI operation.
/// </summary>
internal sealed class CredentialPermissionCoordinator(
    IS3StorageService storage,
    ICdnDeliveryService deliveryService,
    AliyunCdnProvider? aliyunCdnProvider = null)
{
    private readonly S3PermissionChecker _storageChecker = new(storage);
    private readonly AliyunCdnProvider _aliyunCdnProvider = aliyunCdnProvider ?? new AliyunCdnProvider();

    public async Task<PermissionCheckReport> CheckAsync(
        CredentialProfile credential,
        IReadOnlyCollection<ConnectionProfile> storageProfiles,
        CdnConfiguration cdnConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        credential.Validate();
        var results = new List<PermissionCheckResult>();

        foreach (var profile in storageProfiles.Where(value => value.CredentialId == credential.Id))
        {
            var targets = cdnConfiguration.Bindings
                .Where(binding => binding.StorageProfileId == profile.Id)
                .Select(binding => (binding.Bucket, binding.SourcePrefix))
                .Concat(profile.KnownBuckets.Select(bucket => (bucket, string.Empty)))
                .Where(target => !string.IsNullOrWhiteSpace(target.Item1))
                .Distinct()
                .ToArray();
            if (targets.Length == 0)
            {
                results.Add(Result(credential.Id, profile.Name, new PermissionCheck(
                    "storage",
                    "TargetScope",
                    PermissionCheckState.Indeterminate,
                    "连接未配置 Bucket，无法执行目标范围权限检查。")));
                continue;
            }

            foreach (var (bucket, prefix) in targets)
            {
                results.Add(await _storageChecker.CheckAsync(
                    new StoragePermissionCheckRequest(
                        profile,
                        bucket,
                        prefix,
                        StoragePermissionOperation.Read |
                        StoragePermissionOperation.Publish |
                        StoragePermissionOperation.Mirror |
                        StoragePermissionOperation.PutObjectAcl,
                        AllowMutation: false),
                    cancellationToken).ConfigureAwait(false));
            }
        }

        foreach (var profile in storageProfiles.Where(value => value.AwsExternalIdCredentialId == credential.Id))
        {
            results.Add(Result(credential.Id, profile.Name, new PermissionCheck(
                "storage",
                "AssumeRoleExternalId",
                PermissionCheckState.Indeterminate,
                "External ID 只参与 AssumeRole；需通过该连接的目标 Bucket 检查实际角色权限。")
            {
                Required = false
            }));
        }

        foreach (var profile in cdnConfiguration.Profiles.Where(value => value.CredentialId == credential.Id))
            results.Add(await CheckCdnAsync(profile, credential, cancellationToken).ConfigureAwait(false));

        if (results.Count == 0)
        {
            results.Add(Result(credential.Id, "未关联", new PermissionCheck(
                "credential",
                "Association",
                PermissionCheckState.Skipped,
                "该凭据尚未关联对象存储连接或 CDN 配置。")
            {
                Required = false
            }));
        }

        return new PermissionCheckReport(results);
    }

    private async Task<PermissionCheckResult> CheckCdnAsync(
        CdnProfile profile,
        CredentialProfile credential,
        CancellationToken cancellationToken)
    {
        if (string.Equals(profile.ProviderId, CdnProfile.AlibabaCloudProviderId, StringComparison.OrdinalIgnoreCase))
        {
            var result = await _aliyunCdnProvider.CheckDomainPermissionAsync(
                profile,
                credential,
                cancellationToken).ConfigureAwait(false);
            return new PermissionCheckResult(credential.Id,
            [
                new PermissionCheck("cdn", "DescribeUserDomains", result.State, result.Message)
                {
                    StatusCode = result.StatusCode,
                    ProviderCode = result.Code,
                    RequestId = result.RequestId
                },
                new PermissionCheck(
                    "cdn",
                    "RefreshObjectCaches/PushObjectCache",
                    PermissionCheckState.Indeterminate,
                    "只读检测不提交刷新或预热任务，无法无副作用证明控制面写权限。")
                {
                    Required = false
                }
            ])
            {
                TargetScope = profile.BaseUrl,
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }

        try
        {
            var probe = await deliveryService.ProbeHeadAsync(
                profile,
                credential,
                new Uri(profile.BaseUrl, UriKind.Absolute),
                cancellationToken).ConfigureAwait(false);
            var authenticationState = probe.StatusCode is 401 or 403
                ? PermissionCheckState.Denied
                : PermissionCheckState.Indeterminate;
            return new PermissionCheckResult(credential.Id,
            [
                new PermissionCheck(
                    "cdn",
                    "DeliveryEndpoint",
                    probe.StatusCode >= 500 ? PermissionCheckState.Indeterminate : PermissionCheckState.Passed,
                    $"CDN 交付端点返回 HTTP {probe.StatusCode}。")
                {
                    StatusCode = probe.StatusCode,
                    Required = false
                },
                new PermissionCheck(
                    "cdn",
                    "Authentication",
                    authenticationState,
                    authenticationState == PermissionCheckState.Denied
                        ? "CDN 明确拒绝了当前认证。"
                        : "通用 HTTP 响应不能证明凭据被实际校验。")
                {
                    StatusCode = probe.StatusCode
                }
            ])
            {
                TargetScope = profile.BaseUrl,
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(credential.Id, profile.BaseUrl, new PermissionCheck(
                "cdn",
                "Authentication",
                PermissionCheckState.Indeterminate,
                SensitiveDataRedactor.Redact(exception.Message)));
        }
    }

    private static PermissionCheckResult Result(Guid credentialId, string scope, params PermissionCheck[] checks) =>
        new(credentialId, checks)
        {
            TargetScope = scope,
            CheckedAtUtc = DateTimeOffset.UtcNow
        };
}
