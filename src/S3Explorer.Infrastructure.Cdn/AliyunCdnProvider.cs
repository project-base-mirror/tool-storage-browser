using System.Collections;
using System.Reflection;
using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.SDK.Cdn20180510;
using AlibabaCloud.SDK.Cdn20180510.Models;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

/// <summary>Typed Alibaba CDN adapter. The client seam keeps tests offline and makes SDK upgrades explicit.</summary>
public sealed class AliyunCdnProvider : ICdnProvider
{
    public const string ProviderIdValue = CdnProfile.AlibabaCloudProviderId;
    private const int RefreshBatchSize = 1000;
    private const int WarmupBatchSize = 100;
    private readonly IAliyunCdnClientFactory _clientFactory;

    public AliyunCdnProvider(IAliyunCdnClientFactory? clientFactory = null) =>
        _clientFactory = clientFactory ?? new AliyunSdkCdnClientFactory();

    public string ProviderId => ProviderIdValue;
    public CdnCapabilities Capabilities => CdnCapabilities.Purge | CdnCapabilities.Warmup | CdnCapabilities.BuildUrl;

    public async Task<CdnProviderResult> SubmitAsync(CdnProviderRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return Failed("invalid_request", null, null, "请求不能为空。", false);
        if (!string.Equals(request.Profile.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
            return Failed("provider_mismatch", null, null, "Provider 不匹配。", false);
        if (request.Action == CdnJobAction.PurgeThenWarmup)
            return Failed("unsupported", null, null, "阿里云 CDN 的清理后预热需要两阶段任务状态，当前接口不支持安全表达。", false);
        if (!TryGetCredential(request.Credential, out var credentialError, out var credential))
            return Failed("invalid_credential", null, null, credentialError, false);
        if (request.Urls.Count == 0) return Failed("invalid_request", null, null, "至少需要一个 URL。", false);

        try
        {
            var client = _clientFactory.Create(credential);
            var taskIds = new List<string>();
            var batchSize = request.Action == CdnJobAction.PurgeUrl ? RefreshBatchSize : WarmupBatchSize;
            foreach (var batch in DistinctUrls(request.Urls).Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var paths = string.Join("\n", batch.Select(value => value.ToString()));
                var id = request.Action == CdnJobAction.PurgeUrl
                    ? await client.RefreshAsync(paths, cancellationToken).ConfigureAwait(false)
                    : await client.PushAsync(paths, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(id)) taskIds.Add(id.Trim());
            }
            var unique = taskIds.Distinct(StringComparer.Ordinal).ToArray();
            return new CdnProviderResult(
                unique.Length == 0 ? CdnProviderOperationState.Completed : CdnProviderOperationState.Accepted,
                unique.Length == 0 ? "阿里云 CDN 操作已完成。" : $"阿里云 CDN 已接受 {unique.Length} 个任务。",
                ProviderTaskId: string.Join(",", unique));
        }
        catch (Exception ex) { return FromException(ex); }
    }

    public async Task<CdnProviderResult> QueryAsync(CdnProviderRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return Failed("invalid_request", null, null, "请求不能为空。", false);
        if (!TryGetCredential(request.Credential, out var credentialError, out var credential))
            return Failed("invalid_credential", null, null, credentialError, false);
        var ids = request.ProviderTaskId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return Failed("invalid_request", null, null, "缺少 Provider 任务 ID。", false);
        try
        {
            var client = _clientFactory.Create(credential);
            var statuses = new List<string>();
            foreach (var id in ids)
            {
                var task = await client.QueryAsync(id, cancellationToken).ConfigureAwait(false);
                statuses.Add(task.Status);
            }
            var normalized = statuses.Select(value => value.Trim().ToLowerInvariant()).ToArray();
            if (normalized.Any(value => value is "failed" or "timeout" or "canceled" or "cancelled"))
                return new(CdnProviderOperationState.Failed, "阿里云 CDN 任务失败或超时。", false, request.ProviderTaskId);
            if (normalized.All(value => value is "complete" or "completed" or "success"))
                return new(CdnProviderOperationState.Completed, "阿里云 CDN 任务已完成。", false, request.ProviderTaskId);
            return new(CdnProviderOperationState.Accepted, "阿里云 CDN 任务仍在处理中。", true, request.ProviderTaskId);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    /// <summary>Read-only exact-domain permission probe; it does not modify CDN state.</summary>
    public async Task<AliyunCdnPermissionResult> CheckDomainPermissionAsync(
        CdnProfile profile, CredentialProfile? credential, CancellationToken cancellationToken = default)
    {
        if (!TryGetCredential(credential, out var error, out var value))
            return new(PermissionCheckState.Denied, error);
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return new(PermissionCheckState.Indeterminate, "CDN BaseUrl 不是有效域名。", "invalid_request");
        try
        {
            var response = await _clientFactory.Create(value).DescribeDomainsAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            var found = response.Domains.Any(domain => string.Equals(domain, uri.Host, StringComparison.OrdinalIgnoreCase));
            return found
                ? new(PermissionCheckState.Passed, "已找到精确 CDN 域名。", response.Code, response.StatusCode, response.RequestId)
                : new(PermissionCheckState.Denied, "阿里云 CDN 未返回精确域名。", response.Code, response.StatusCode, response.RequestId);
        }
        catch (Exception ex)
        {
            var result = FromException(ex);
            var denied = result.StatusCode is 401 or 403;
            return new(
                denied ? PermissionCheckState.Denied : PermissionCheckState.Indeterminate,
                denied ? "阿里云 CDN 拒绝了域名查询。" : result.Message,
                Extract(result.ResponseSnippet, "code"),
                result.StatusCode,
                Extract(result.ResponseSnippet, "requestId"));
        }
    }

    private static IEnumerable<Uri> DistinctUrls(IEnumerable<Uri> urls) =>
        urls.Where(value => value is not null)
            .Select(value => new Uri(value.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped), UriKind.Absolute))
            .DistinctBy(value => value.AbsoluteUri, StringComparer.OrdinalIgnoreCase);

    private static bool TryGetCredential(CredentialProfile? value, out string error, out AliyunCredential credential)
    {
        if (value is null) { error = "未配置 Alibaba Cloud 凭据。"; credential = default; return false; }
        if (value.Provider != CredentialProviderKind.AlibabaCloud || value.Kind != CredentialKind.AccessKeyPair ||
            string.IsNullOrWhiteSpace(value.AccessKeyId) || string.IsNullOrWhiteSpace(value.Secret))
        { error = "凭据必须是完整的 Alibaba Cloud AccessKeyPair。"; credential = default; return false; }
        error = string.Empty;
        credential = new(value.AccessKeyId, value.Secret, value.SessionToken);
        return true;
    }

    private static CdnProviderResult Failed(string code, int? status, string? requestId, string message, bool retryable) =>
        new(CdnProviderOperationState.Failed, message, retryable, StatusCode: status,
            ResponseSnippet: $"code={code}; status={(status?.ToString() ?? "")}; requestId={requestId ?? ""}");

    private static CdnProviderResult FromException(Exception exception)
    {
        var code = ReadString(exception, "Code") ?? ReadString(exception, "ErrorCode") ?? exception.GetType().Name;
        var requestId = ReadString(exception, "RequestId");
        var status = ReadInt(exception, "StatusCode") ?? ReadInt(exception, "HttpStatusCode");
        var message = "阿里云 CDN 请求失败。";
        var retryable = status is null or 408 or 425 or 429 || status >= 500;
        return Failed(code, status, requestId, message, retryable);
    }

    private static string? ReadString(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)?.ToString();
    private static int? ReadInt(object value, string name) => int.TryParse(ReadString(value, name), out var result) ? result : null;
    private static string Extract(string value, string key) => value.Split(';').FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal))?[((key.Length) + 1)..] ?? "";
}

public readonly record struct AliyunCredential(string AccessKeyId, string Secret, string SessionToken);
public sealed record AliyunCdnPermissionResult(PermissionCheckState State, string Message, string Code = "", int? StatusCode = null, string RequestId = "");
public readonly record struct AliyunTaskResult(string Status);
public readonly record struct AliyunDomainResult(IReadOnlyList<string> Domains, string Code = "", int? StatusCode = null, string RequestId = "");

public interface IAliyunCdnClientFactory { IAliyunCdnClient Create(AliyunCredential credential); }
public interface IAliyunCdnClient
{
    Task<string> RefreshAsync(string objectPath, CancellationToken cancellationToken);
    Task<string> PushAsync(string objectPath, CancellationToken cancellationToken);
    Task<AliyunTaskResult> QueryAsync(string taskId, CancellationToken cancellationToken);
    Task<AliyunDomainResult> DescribeDomainsAsync(string domainName, CancellationToken cancellationToken);
}

public sealed class AliyunSdkCdnClientFactory : IAliyunCdnClientFactory
{
    public IAliyunCdnClient Create(AliyunCredential credential) => new AliyunSdkCdnClient(credential);
}

internal sealed class AliyunSdkCdnClient : IAliyunCdnClient
{
    private readonly Client _client;
    public AliyunSdkCdnClient(AliyunCredential credential) => _client = new(new Config { AccessKeyId = credential.AccessKeyId, AccessKeySecret = credential.Secret, SecurityToken = credential.SessionToken, Endpoint = "cdn.aliyuncs.com" });
    public async Task<string> RefreshAsync(string objectPath, CancellationToken cancellationToken) =>
        (await _client.RefreshObjectCachesAsync(new RefreshObjectCachesRequest { ObjectPath = objectPath, ObjectType = "File" })
            .WaitAsync(cancellationToken).ConfigureAwait(false)).Body?.RefreshTaskId ?? "";
    public async Task<string> PushAsync(string objectPath, CancellationToken cancellationToken) =>
        (await _client.PushObjectCacheAsync(new PushObjectCacheRequest { ObjectPath = objectPath })
            .WaitAsync(cancellationToken).ConfigureAwait(false)).Body?.PushTaskId ?? "";
    public async Task<AliyunTaskResult> QueryAsync(string taskId, CancellationToken cancellationToken) => new(
        (await _client.DescribeRefreshTaskByIdAsync(new DescribeRefreshTaskByIdRequest { TaskId = taskId })
            .WaitAsync(cancellationToken).ConfigureAwait(false)).Body?.Tasks?.FirstOrDefault()?.Status ?? "Unknown");
    public async Task<AliyunDomainResult> DescribeDomainsAsync(string domainName, CancellationToken cancellationToken)
    {
        var response = await _client.DescribeUserDomainsAsync(new DescribeUserDomainsRequest { DomainName = domainName, PageSize = 100 })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var domains = response.Body?.Domains?.PageData?.Select(value => value.DomainName).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray() ?? [];
        return new(domains, "", response.StatusCode, response.Body?.RequestId ?? "");
    }
}
