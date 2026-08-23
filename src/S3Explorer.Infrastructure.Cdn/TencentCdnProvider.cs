using System.Reflection;
using TencentCloud.Cdn.V20180606;
using TencentCloud.Cdn.V20180606.Models;
using TencentCloud.Common;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

/// <summary>腾讯云 CDN 原生控制面适配器。</summary>
public sealed class TencentCdnProvider : ICdnProvider, ICdnControlPermissionChecker
{
    public const string ProviderIdValue = CdnProfile.TencentCloudProviderId;
    private const int PurgeBatchSize = 1000;
    private const int PushBatchSize = 500;
    private readonly ITencentCdnClientFactory _clientFactory;

    public TencentCdnProvider(ITencentCdnClientFactory? clientFactory = null) =>
        _clientFactory = clientFactory ?? new TencentSdkCdnClientFactory();

    public string ProviderId => ProviderIdValue;
    public CdnCapabilities Capabilities => CdnCapabilities.Purge | CdnCapabilities.Warmup | CdnCapabilities.BuildUrl;

    public async Task<CdnProviderResult> SubmitAsync(CdnProviderRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return Failed("请求不能为空。", false);
        if (!string.Equals(request.Profile.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
            return Failed("Provider 不匹配。", false);
        if (request.Action == CdnJobAction.PurgeThenWarmup)
            return Failed("腾讯云 CDN 的刷新后预热需要两阶段任务状态，当前接口不支持安全表达。", false);
        if (!TryGetCredential(request.ControlCredential, out var error, out var credential))
            return Failed(error, false);
        var urls = DistinctUrls(request.Urls).Select(value => value.AbsoluteUri).ToArray();
        if (urls.Length == 0) return Failed("至少需要一个 URL。", false);

        try
        {
            var client = _clientFactory.Create(credential);
            var taskIds = new List<string>();
            var size = request.Action == CdnJobAction.PurgeUrl ? PurgeBatchSize : PushBatchSize;
            foreach (var batch in urls.Chunk(size))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = request.Action == CdnJobAction.PurgeUrl
                    ? await client.PurgeUrlsAsync(batch, cancellationToken).ConfigureAwait(false)
                    : await client.PushUrlsAsync(batch, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(id)) taskIds.Add(id.Trim());
            }
            var unique = taskIds.Distinct(StringComparer.Ordinal).ToArray();
            return new(unique.Length == 0 ? CdnProviderOperationState.Completed : CdnProviderOperationState.Accepted,
                unique.Length == 0 ? "腾讯云 CDN 操作已完成。" : $"腾讯云 CDN 已接受 {unique.Length} 个任务。",
                ProviderTaskId: string.Join(",", unique));
        }
        catch (Exception ex) { return FromException(ex); }
    }

    public async Task<CdnProviderResult> QueryAsync(CdnProviderRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return Failed("请求不能为空。", false);
        if (request.Action == CdnJobAction.PurgeThenWarmup)
            return Failed("PurgeThenWarmup 不能直接查询执行阶段。请改为查询 PurgeUrl 或 Warmup。", false);
        if (!TryGetCredential(request.ControlCredential, out var error, out var credential))
            return Failed(error, false);
        var ids = request.ProviderTaskId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return Failed("缺少 Provider 任务 ID。", false);
        try
        {
            var client = _clientFactory.Create(credential);
            var statuses = new List<string>();
            foreach (var id in ids)
            {
                var task = request.Action == CdnJobAction.PurgeUrl
                    ? await client.QueryPurgeAsync(id, cancellationToken).ConfigureAwait(false)
                    : await client.QueryPushAsync(id, cancellationToken).ConfigureAwait(false);
                statuses.Add(task.Status);
            }
            var normalized = statuses.Select(value => value.Trim().ToLowerInvariant()).ToArray();
            if (normalized.Any(value => value is "fail" or "failed" or "invalid" or "timeout" or "canceled" or "cancelled"))
                return new(CdnProviderOperationState.Failed, "腾讯云 CDN 任务失败或无效。", false, request.ProviderTaskId);
            if (normalized.All(value => value is "done" or "complete" or "completed" or "success"))
                return new(CdnProviderOperationState.Completed, "腾讯云 CDN 任务已完成。", false, request.ProviderTaskId);
            if (normalized.Any(value => value is not ("done" or "complete" or "completed" or "success" or "process" or "processing" or "pending" or "waiting")))
                return new(CdnProviderOperationState.Failed, "腾讯云 CDN 返回了无法识别的任务状态。", false, request.ProviderTaskId);
            return new(CdnProviderOperationState.Accepted, "腾讯云 CDN 任务仍在处理中。", true, request.ProviderTaskId);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    public async Task<IReadOnlyList<PermissionCheck>> CheckControlPermissionsAsync(
        CdnProfile profile, CredentialProfile? credential, CancellationToken cancellationToken)
    {
        var checks = new List<PermissionCheck>
        {
            new("cdn-control", "DescribeDomains", PermissionCheckState.Indeterminate,
                "正在查询腾讯云 CDN 域名。"),
            new("cdn-control", "PurgeUrlsCache", PermissionCheckState.Indeterminate,
                "写入权限不执行副作用探针。") { Required = false },
            new("cdn-control", "PushUrlsCache", PermissionCheckState.Indeterminate,
                "写入权限不执行副作用探针。") { Required = false }
        };
        if (!TryGetCredential(credential, out var error, out var value))
            return checks.Select(check => check with { State = PermissionCheckState.Denied, Message = error }).ToArray();
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return checks.Select(check => check with { State = PermissionCheckState.Indeterminate, Message = "CDN BaseUrl 不是有效域名。" }).ToArray();
        try
        {
            var result = await _clientFactory.Create(value).DescribeDomainsAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            var found = result.Domains.Any(domain => string.Equals(domain, uri.Host, StringComparison.OrdinalIgnoreCase));
            checks[0] = checks[0] with
            {
                State = found ? PermissionCheckState.Passed : PermissionCheckState.Denied,
                Message = found ? "已找到精确腾讯云 CDN 域名。" : "腾讯云 CDN 未返回精确域名。",
                StatusCode = result.StatusCode, ProviderCode = result.Code, RequestId = result.RequestId
            };
        }
        catch (Exception ex)
        {
            var result = FromException(ex);
            checks[0] = checks[0] with
            {
                State = result.StatusCode is 401 or 403 ? PermissionCheckState.Denied : PermissionCheckState.Indeterminate,
                Message = result.Message, StatusCode = result.StatusCode,
                ProviderCode = Extract(result.ResponseSnippet, "code"), RequestId = Extract(result.ResponseSnippet, "requestId")
            };
        }
        return checks;
    }

    private static IEnumerable<Uri> DistinctUrls(IEnumerable<Uri> urls) =>
        urls.Where(value => value is not null)
            .Select(value => new Uri(value.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped), UriKind.Absolute))
            .DistinctBy(value => value.AbsoluteUri, StringComparer.OrdinalIgnoreCase);

    private static bool TryGetCredential(CredentialProfile? value, out string error, out TencentCdnCredential credential)
    {
        if (value is null || value.Provider != CredentialProviderKind.TencentCloud ||
            value.Kind != CredentialKind.AccessKeyPair || string.IsNullOrWhiteSpace(value.AccessKeyId) || string.IsNullOrWhiteSpace(value.Secret))
        {
            error = "凭据必须是完整的腾讯云 SecretId/SecretKey。";
            credential = default;
            return false;
        }
        error = string.Empty;
        credential = new(value.AccessKeyId, value.Secret, value.SessionToken);
        return true;
    }

    private static CdnProviderResult Failed(string message, bool retryable, int? statusCode = null, string snippet = "") =>
        new(CdnProviderOperationState.Failed, message, retryable, StatusCode: statusCode, ResponseSnippet: snippet);

    private static CdnProviderResult FromException(Exception exception)
    {
        var code = ReadString(exception, "ErrorCode") ?? ReadString(exception, "Code") ?? exception.GetType().Name;
        var requestId = ReadString(exception, "RequestId") ?? string.Empty;
        var status = ReadInt(exception, "HttpStatusCode") ?? ReadInt(exception, "StatusCode");
        status ??= code.StartsWith("UnauthorizedOperation", StringComparison.OrdinalIgnoreCase) ||
                   code.StartsWith("AuthFailure", StringComparison.OrdinalIgnoreCase) ? 403 :
                   code.StartsWith("RequestLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
                   code.StartsWith("LimitExceeded", StringComparison.OrdinalIgnoreCase) ? 429 : null;
        var retryable = status is null or 408 or 425 or 429 || status >= 500;
        var message = status switch
        {
            401 or 403 => "腾讯云 CDN 拒绝了控制面请求。",
            429 => "腾讯云 CDN 请求过于频繁。",
            >= 500 => "腾讯云 CDN 服务暂时不可用。",
            _ => "腾讯云 CDN 请求失败。"
        };
        return Failed(message, retryable, status, $"code={code}; status={(status?.ToString() ?? "")}; requestId={requestId}");
    }

    private static string? ReadString(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)?.ToString();
    private static int? ReadInt(object value, string name) => int.TryParse(ReadString(value, name), out var result) ? result : null;
    private static string Extract(string value, string key) => value.Split(';').FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal))?[((key.Length) + 1)..] ?? "";
}

public readonly record struct TencentCdnCredential(string SecretId, string SecretKey, string SessionToken);
public readonly record struct TencentCdnTaskResult(string Status);
public readonly record struct TencentCdnDomainResult(IReadOnlyList<string> Domains, string Code = "", int? StatusCode = null, string RequestId = "");

public interface ITencentCdnClientFactory { ITencentCdnClient Create(TencentCdnCredential credential); }
public interface ITencentCdnClient
{
    Task<TencentCdnDomainResult> DescribeDomainsAsync(string domainName, CancellationToken cancellationToken);
    Task<string> PurgeUrlsAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken);
    Task<string> PushUrlsAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken);
    Task<TencentCdnTaskResult> QueryPurgeAsync(string taskId, CancellationToken cancellationToken);
    Task<TencentCdnTaskResult> QueryPushAsync(string taskId, CancellationToken cancellationToken);
}

public sealed class TencentSdkCdnClientFactory : ITencentCdnClientFactory
{
    public ITencentCdnClient Create(TencentCdnCredential credential) => new TencentSdkCdnClient(credential);
}

internal sealed class TencentSdkCdnClient : ITencentCdnClient
{
    private readonly CdnClient _client;
    public TencentSdkCdnClient(TencentCdnCredential credential) =>
        _client = new(new Credential { SecretId = credential.SecretId, SecretKey = credential.SecretKey, Token = credential.SessionToken }, "");

    public async Task<TencentCdnDomainResult> DescribeDomainsAsync(string domainName, CancellationToken cancellationToken)
    {
        var response = await _client.DescribeDomains(new DescribeDomainsRequest
        {
            Offset = 0,
            Limit = 1000,
            Filters = [new DomainFilter { Name = "domain", Value = [domainName], Fuzzy = false }]
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        var domains = response.Domains?.Select(value => value.Domain).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray() ?? [];
        return new(domains, "", null, response.RequestId ?? "");
    }

    public async Task<string> PurgeUrlsAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        var response = await _client.PurgeUrlsCache(new PurgeUrlsCacheRequest { Urls = urls.ToArray() })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return response.TaskId ?? "";
    }

    public async Task<string> PushUrlsAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        var response = await _client.PushUrlsCache(new PushUrlsCacheRequest { Urls = urls.ToArray() })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return response.TaskId ?? "";
    }

    public async Task<TencentCdnTaskResult> QueryPurgeAsync(string taskId, CancellationToken cancellationToken)
    {
        var response = await _client.DescribePurgeTasks(new DescribePurgeTasksRequest { TaskId = taskId, Limit = 1 })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(response.PurgeLogs?.FirstOrDefault()?.Status ?? "unknown");
    }

    public async Task<TencentCdnTaskResult> QueryPushAsync(string taskId, CancellationToken cancellationToken)
    {
        var response = await _client.DescribePushTasks(new DescribePushTasksRequest { TaskId = taskId, Limit = 1 })
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(response.PushLogs?.FirstOrDefault()?.Status ?? "unknown");
    }
}
