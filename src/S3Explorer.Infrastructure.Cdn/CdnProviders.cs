using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

public sealed class GenericHttpCdnProvider(ICdnDeliveryService deliveryService) : ICdnProvider
{
    public string ProviderId => CdnProfile.GenericHttpProviderId;
    public CdnCapabilities Capabilities =>
        CdnCapabilities.Warmup | CdnCapabilities.Purge | CdnCapabilities.BuildUrl;

    public async Task<CdnProviderResult> SubmitAsync(
        CdnProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Profile.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
            return Failed($"Provider 不匹配：{request.Profile.ProviderId}", retryable: false);

        long bytesRead = 0;
        int? statusCode = null;
        var messages = new List<string>();
        var snippets = new List<string>();

        if (request.Action is CdnJobAction.PurgeUrl or CdnJobAction.PurgeThenWarmup)
        {
            if (!request.Profile.Capabilities.HasFlag(CdnCapabilities.Purge))
                return Failed($"CDN 配置“{request.Profile.Name}”没有设置刷新端点。", retryable: false);
            foreach (var url in request.Urls)
            {
                var result = await deliveryService.PurgeAsync(
                    request.Profile, request.Credential, url, cancellationToken).ConfigureAwait(false);
                bytesRead += result.BytesRead;
                statusCode = result.StatusCode ?? statusCode;
                messages.Add(result.Message);
                if (result.ResponseSnippet.Length > 0) snippets.Add(result.ResponseSnippet);
                if (!result.Success)
                    return Failed(
                        result.Message,
                        IsRetryable(result.StatusCode),
                        result.StatusCode,
                        string.Join(Environment.NewLine, snippets),
                        bytesRead);
            }
        }

        if (request.Action is CdnJobAction.Warmup or CdnJobAction.PurgeThenWarmup)
        {
            foreach (var url in request.Urls)
            {
                var result = await deliveryService.WarmupAsync(
                    request.Profile, request.Credential, url, cancellationToken).ConfigureAwait(false);
                bytesRead += result.BytesRead;
                statusCode = result.StatusCode ?? statusCode;
                messages.Add(result.Message);
                if (result.ResponseSnippet.Length > 0) snippets.Add(result.ResponseSnippet);
                if (!result.Success)
                    return Failed(
                        result.Message,
                        IsRetryable(result.StatusCode),
                        result.StatusCode,
                        string.Join(Environment.NewLine, snippets),
                        bytesRead);
            }
        }

        return new CdnProviderResult(
            CdnProviderOperationState.Completed,
            messages.Count == 0 ? "CDN 操作已完成。" : string.Join(" ", messages.Distinct()),
            StatusCode: statusCode,
            ResponseSnippet: string.Join(Environment.NewLine, snippets),
            BytesRead: bytesRead);
    }

    public Task<CdnProviderResult> QueryAsync(
        CdnProviderRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(Failed("通用 HTTP Provider 不支持异步任务查询。", retryable: false));

    private static CdnProviderResult Failed(
        string message,
        bool retryable,
        int? statusCode = null,
        string snippet = "",
        long bytesRead = 0) =>
        new(
            CdnProviderOperationState.Failed,
            message,
            retryable,
            StatusCode: statusCode,
            ResponseSnippet: snippet,
            BytesRead: bytesRead);

    private static bool IsRetryable(int? statusCode) =>
        statusCode is null or 408 or 425 or 429 || statusCode >= 500;
}

public sealed class StoreBackedCdnJobExecutor : ICdnJobExecutor
{
    private readonly ICdnConfigurationStore _configurationStore;
    private readonly ICdnCredentialStore _credentialStore;
    private readonly IReadOnlyDictionary<string, ICdnProvider> _providers;

    public StoreBackedCdnJobExecutor(
        ICdnConfigurationStore configurationStore,
        ICdnCredentialStore credentialStore,
        IEnumerable<ICdnProvider> providers)
    {
        _configurationStore = configurationStore;
        _credentialStore = credentialStore;
        _providers = providers.ToDictionary(
            provider => provider.ProviderId,
            StringComparer.OrdinalIgnoreCase);
        if (_providers.Count == 0)
            throw new ArgumentException("至少需要注册一个 CDN Provider。", nameof(providers));
    }

    public async Task<CdnProviderResult> ExecuteAsync(
        CdnJobRecord job,
        CancellationToken cancellationToken)
    {
        job.Validate();
        var configuration = await _configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var credentials = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = configuration.Profiles.FirstOrDefault(value => value.Id == job.CdnProfileId);
        if (profile is null)
            return Failed("CDN 配置已不存在，无法执行任务。");
        if (!profile.Enabled)
            return Failed($"CDN 配置“{profile.Name}”已禁用。");
        if (!_providers.TryGetValue(profile.ProviderId, out var provider))
            return Failed($"没有注册 CDN Provider：{profile.ProviderId}");

        CdnCredential? credential = null;
        if (profile.CredentialId is Guid credentialId)
        {
            credential = credentials.FirstOrDefault(value => value.Id == credentialId);
            if (credential is null)
                return Failed($"CDN 配置“{profile.Name}”引用的凭据不存在。");
        }

        var urls = job.Urls.Select(value => new Uri(value, UriKind.Absolute)).ToArray();
        var request = new CdnProviderRequest(
            job.Action,
            profile,
            credential,
            urls,
            job.ProviderTaskId);
        return job.ProviderTaskId.Length > 0
            ? await provider.QueryAsync(request, cancellationToken).ConfigureAwait(false)
            : await provider.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static CdnProviderResult Failed(string message) =>
        new(CdnProviderOperationState.Failed, message, Retryable: false);
}
