using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

/// <summary>Cloudflare CDN control-plane adapter. Content requests remain on the delivery service.</summary>
public sealed class CloudflareCdnProvider : ICdnProvider, ICdnControlPermissionChecker
{
    public const string ProviderIdValue = CdnProfile.CloudflareProviderId;
    private const int PurgeBatchSize = 100;
    private readonly ICloudflareCdnClientFactory _clientFactory;
    private readonly ICdnDeliveryService? _deliveryService;

    public CloudflareCdnProvider(
        ICloudflareCdnClientFactory? clientFactory = null,
        ICdnDeliveryService? deliveryService = null)
    {
        _clientFactory = clientFactory ?? new CloudflareHttpClientFactory();
        _deliveryService = deliveryService;
    }

    public string ProviderId => ProviderIdValue;
    public CdnCapabilities Capabilities =>
        CdnCapabilities.Purge | CdnCapabilities.Warmup | CdnCapabilities.BuildUrl;

    public async Task<CdnProviderResult> SubmitAsync(CdnProviderRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return Failed("请求不能为空。", false, "invalid_request");
        if (!string.Equals(request.Profile.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
            return Failed("Provider 不匹配。", false, "provider_mismatch");
        if (!TryGetRequest(request, out var error, out var client, out var profile))
            return Failed(error, false, "invalid_request");
        if (request.Urls.Count == 0) return Failed("至少需要一个 URL。", false, "invalid_request");

        if (request.Action == CdnJobAction.Warmup)
            return await WarmupAsync(profile, request.Urls, cancellationToken).ConfigureAwait(false);

        if (request.Action is not (CdnJobAction.PurgeUrl or CdnJobAction.PurgeThenWarmup))
            return Failed("Cloudflare CDN 不支持此操作。", false, "unsupported");

        try
        {
            foreach (var batch in DistinctUrls(request.Urls).Chunk(PurgeBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await client.PurgeAsync(batch, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                    return MapFailure(result, "Cloudflare CDN 缓存清理失败。");
            }

            if (request.Action == CdnJobAction.PurgeThenWarmup)
            {
                var warmup = await WarmupAsync(profile, request.Urls, cancellationToken).ConfigureAwait(false);
                if (warmup.State == CdnProviderOperationState.Failed) return warmup;
                return warmup with { Message = "Cloudflare CDN 缓存已清理；Cloudflare 无原生预热，已通过内容 URL 完成预热。" };
            }

            return new(CdnProviderOperationState.Completed, "Cloudflare CDN 缓存清理已完成。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failed("Cloudflare CDN 请求失败。", IsRetryable(null), "request_failed", ex); }
    }

    public async Task<CdnProviderResult> QueryAsync(CdnProviderRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return Failed("请求不能为空。", false, "invalid_request");
        if (!TryGetRequest(request, out var error, out var client, out var profile))
            return Failed(error, false, "invalid_request");
        if (string.IsNullOrWhiteSpace(request.ProviderTaskId))
            return await CheckZoneAsync(client, profile, cancellationToken).ConfigureAwait(false);
        return Failed("Cloudflare CDN 操作同步完成，不会创建可查询的假任务。", false, "unsupported");
    }

    public async Task<CdnProviderResult> CheckZonePermissionAsync(
        CdnProfile profile,
        CredentialProfile? credential,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCredential(credential, out var error, out var token))
            return Failed(error, false, "invalid_credential");
        if (string.IsNullOrWhiteSpace(profile.ControlResourceId))
            return Failed("Cloudflare CDN 配置缺少 Zone ID。", false, "invalid_resource");
        try
        {
            var result = await _clientFactory.Create(token, profile.ControlResourceId).GetZoneAsync(profile.ControlResourceId, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return MapFailure(result, "Cloudflare Zone 查询失败。");
            if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri) ||
                !IsHostInZone(baseUri.Host, result.Name))
                return Failed("Cloudflare Zone 查询成功，但 Zone 与 CDN 域名不匹配。", false, "zone_mismatch");
            return new(CdnProviderOperationState.Completed, "Cloudflare Zone 与 CDN 域名匹配，控制面凭据可用。", StatusCode: 200);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failed("Cloudflare Zone 查询失败。", IsRetryable(null), "request_failed", ex); }
    }

    public async Task<IReadOnlyList<PermissionCheck>> CheckControlPermissionsAsync(
        CdnProfile profile,
        CredentialProfile? credential,
        CancellationToken cancellationToken)
    {
        var checks = new List<PermissionCheck>();
        var zone = await CheckZonePermissionAsync(profile, credential, cancellationToken).ConfigureAwait(false);
        var zoneState = zone.State == CdnProviderOperationState.Completed
            ? PermissionCheckState.Passed
            : zone.StatusCode is 401 or 403
                ? PermissionCheckState.Denied
                : PermissionCheckState.Indeterminate;
        checks.Add(new PermissionCheck(
            "cdn-control", "ZoneRead", zoneState,
            zone.Message) { Required = true, StatusCode = zone.StatusCode });
        checks.Add(new PermissionCheck(
            "cdn-control", "CachePurge", PermissionCheckState.Indeterminate,
            "Cloudflare 缓存清理需要执行真实变更，普通权限检查不会调用 purge_cache。") { Required = false });
        return checks;
    }

    private async Task<CdnProviderResult> CheckZoneAsync(
        ICloudflareCdnClient client, CdnProfile profile, CancellationToken cancellationToken)
    {
        var result = await client.GetZoneAsync(profile.ControlResourceId, cancellationToken).ConfigureAwait(false);
        if (!result.Success) return MapFailure(result, "Cloudflare Zone 查询失败。");
        return new(CdnProviderOperationState.Completed, $"Cloudflare Zone 查询成功：{result.Name}。", StatusCode: result.StatusCode);
    }

    private async Task<CdnProviderResult> WarmupAsync(
        CdnProfile profile, IReadOnlyList<Uri> urls, CancellationToken cancellationToken)
    {
        if (_deliveryService is null)
            return Failed("Cloudflare CDN 没有原生预热 API，且未配置内容预热服务。", false, "warmup_unavailable");
        foreach (var url in DistinctUrls(urls))
        {
            var result = await _deliveryService.WarmupAsync(profile, url, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                return new(CdnProviderOperationState.Failed, result.Message, IsRetryable(result.StatusCode), StatusCode: result.StatusCode, ResponseSnippet: result.ResponseSnippet, BytesRead: result.BytesRead);
        }
        return new(CdnProviderOperationState.Completed, "Cloudflare 无原生预热，已通过内容 URL 完成预热。", BytesRead: 0);
    }

    private bool TryGetRequest(CdnProviderRequest request, out string error, out ICloudflareCdnClient client, out CdnProfile profile)
    {
        profile = request.Profile;
        client = null!;
        if (!TryGetCredential(request.ControlCredential, out error, out var token)) return false;
        if (string.IsNullOrWhiteSpace(profile.ControlResourceId)) { error = "Cloudflare CDN 配置缺少 Zone ID。"; return false; }
        client = _clientFactory.Create(token, profile.ControlResourceId);
        error = string.Empty;
        return true;
    }

    private static bool TryGetCredential(CredentialProfile? value, out string error, out string token)
    {
        if (value is null || value.Provider != CredentialProviderKind.Cloudflare ||
            value.Kind != CredentialKind.BearerToken || string.IsNullOrWhiteSpace(value.Secret))
        { error = "Cloudflare CDN 控制面凭据必须是带 Token 的 Cloudflare BearerToken。"; token = string.Empty; return false; }
        error = string.Empty; token = value.Secret; return true;
    }

    private static CdnProviderResult MapFailure(CloudflareApiResult result, string operation) =>
        Failed(result.StatusCode switch { 401 => "Cloudflare CDN 凭据未授权。", 403 => "Cloudflare CDN 拒绝了控制面操作。", 429 => "Cloudflare CDN 请求过于频繁，请稍后重试。", >= 500 => "Cloudflare CDN 服务暂时不可用。", _ => operation }, IsRetryable(result.StatusCode), result.Code ?? "cloudflare_error", statusCode: result.StatusCode, snippet: result.Message);

    private static CdnProviderResult Failed(string message, bool retryable, string code, Exception? exception = null, int? statusCode = null, string snippet = "") =>
        new(CdnProviderOperationState.Failed, message, retryable, StatusCode: statusCode, ResponseSnippet: BoundSnippet(snippet.Length > 0 ? snippet : exception?.Message ?? string.Empty));

    private static bool IsRetryable(int? statusCode) => statusCode is null or 408 or 425 or 429 || statusCode >= 500;
    private static string BoundSnippet(string value) => value.Length <= 512 ? value : value[..512];
    private static IEnumerable<Uri> DistinctUrls(IEnumerable<Uri> urls) => urls.DistinctBy(value => value.AbsoluteUri, StringComparer.OrdinalIgnoreCase);
    private static bool IsHostInZone(string host, string zoneName) =>
        !string.IsNullOrWhiteSpace(zoneName) &&
        (string.Equals(host, zoneName, StringComparison.OrdinalIgnoreCase) ||
         host.EndsWith("." + zoneName, StringComparison.OrdinalIgnoreCase));
}

public sealed record CloudflareApiResult(bool Success, int? StatusCode, string? Code, string Message = "", string Name = "");

public interface ICloudflareCdnClientFactory
{
    ICloudflareCdnClient Create(string bearerToken, string zoneId);
}

public interface ICloudflareCdnClient
{
    Task<CloudflareApiResult> GetZoneAsync(string zoneId, CancellationToken cancellationToken);
    Task<CloudflareApiResult> PurgeAsync(IReadOnlyList<Uri> urls, CancellationToken cancellationToken);
}

public sealed class CloudflareHttpClientFactory : ICloudflareCdnClientFactory
{
    public ICloudflareCdnClient Create(string bearerToken, string zoneId) => new CloudflareHttpClient(bearerToken, zoneId);
}

internal sealed class CloudflareHttpClient : ICloudflareCdnClient
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
        Timeout = TimeSpan.FromSeconds(100)
    };
    private readonly string _bearerToken;
    private readonly string _zoneId;
    public CloudflareHttpClient(string bearerToken, string zoneId)
    {
        _bearerToken = bearerToken;
        _zoneId = zoneId;
    }

    public Task<CloudflareApiResult> GetZoneAsync(string zoneId, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, $"zones/{Uri.EscapeDataString(zoneId)}", null, cancellationToken);

    public Task<CloudflareApiResult> PurgeAsync(IReadOnlyList<Uri> urls, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { files = urls.Select(value => value.AbsoluteUri).ToArray() });
        return SendAsync(HttpMethod.Post, $"zones/{Uri.EscapeDataString(_zoneId)}/purge_cache", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
    }

    private async Task<CloudflareApiResult> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        using var response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var message = root.TryGetProperty("errors", out var errors) ? Bound(errors.ToString()) : string.Empty;
            var name = root.TryGetProperty("result", out var result) && result.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            return new(success, (int)response.StatusCode, null, message, name);
        }
        catch (JsonException) { return new(false, (int)response.StatusCode, "invalid_response", "Cloudflare 返回了无效响应。"); }
    }

    private static string Bound(string value) => value.Length <= 512 ? value : value[..512];
}
