namespace S3Explorer.Core;

[Flags]
public enum CdnCapabilities
{
    None = 0,
    BuildUrl = 1,
    DownloadProbe = 2,
    Warmup = 4,
    Purge = 8
}

public enum CdnAuthenticationType
{
    None,
    BearerToken,
    CustomHeader
}

public enum CdnWarmupMode
{
    Head,
    RangeGet,
    FullGet
}

public sealed record CdnProfile
{
    public const string GenericHttpProviderId = "generic-http";
    public const string AlibabaCloudProviderId = "aliyun-cdn";
    public const int MaximumNotesLength = 2000;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string ProviderId { get; init; } = GenericHttpProviderId;
    public string BaseUrl { get; init; } = string.Empty;
    public Guid? CredentialId { get; init; }
    public CdnWarmupMode WarmupMode { get; init; } = CdnWarmupMode.RangeGet;
    public long WarmupRangeBytes { get; init; } = 1024 * 1024;
    public string PurgeEndpointTemplate { get; init; } = string.Empty;
    public string PurgeHttpMethod { get; init; } = "POST";
    public string PurgeBodyTemplate { get; init; } = string.Empty;
    public string PurgeContentType { get; init; } = "application/json";
    public int TimeoutSeconds { get; init; } = 100;
    public bool FollowRedirects { get; init; } = true;
    public bool Enabled { get; init; } = true;
    public CdnCertificateCheckResult? LastCertificateCheck { get; init; }

    public CdnCapabilities Capabilities =>
        CdnCapabilities.BuildUrl |
        CdnCapabilities.DownloadProbe |
        CdnCapabilities.Warmup |
        (string.Equals(ProviderId, AlibabaCloudProviderId, StringComparison.OrdinalIgnoreCase) ||
         !string.IsNullOrWhiteSpace(PurgeEndpointTemplate)
            ? CdnCapabilities.Purge
            : CdnCapabilities.None);
}

public sealed record CdnCredential
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public CdnAuthenticationType AuthenticationType { get; init; }
    public string HeaderName { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
}

public sealed record CdnBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid StorageProfileId { get; init; }
    public string Bucket { get; init; } = string.Empty;
    public string SourcePrefix { get; init; } = string.Empty;
    public Guid CdnProfileId { get; init; }
    public string CdnPathPrefix { get; init; } = string.Empty;
    public CdnUploadAction NewObjectAction { get; init; } = CdnUploadAction.None;
    public CdnUploadAction OverwriteAction { get; init; } = CdnUploadAction.None;
    public bool IsDefault { get; init; } = true;
    public bool Enabled { get; init; } = true;
}

public sealed record CdnConfiguration(
    IReadOnlyList<CdnProfile> Profiles,
    IReadOnlyList<CdnBinding> Bindings)
{
    public static CdnConfiguration Empty { get; } = new([], []);
}

public sealed record CdnResolvedTarget(
    CdnProfile Profile,
    CdnBinding Binding,
    Uri Url,
    string ObjectKey);

public sealed record CdnProbeResult(
    Uri RequestedUrl,
    Uri FinalUrl,
    int StatusCode,
    string ReasonPhrase,
    TimeSpan TimeToHeaders,
    TimeSpan TotalElapsed,
    long BytesRead,
    long? ContentLength,
    string? ContentType,
    string CacheStatus,
    IReadOnlyDictionary<string, string> Headers)
{
    public bool Success => StatusCode is >= 200 and < 400;
    public double BytesPerSecond => TotalElapsed.TotalSeconds <= 0 ? 0 : BytesRead / TotalElapsed.TotalSeconds;
}

public sealed record CdnDownloadResult(
    Uri RequestedUrl,
    Uri FinalUrl,
    int StatusCode,
    long BytesWritten,
    string? ContentType);

public sealed record CdnOperationResult(
    bool Success,
    int? StatusCode,
    TimeSpan Elapsed,
    long BytesRead,
    string Message,
    string ResponseSnippet = "");

[Flags]
public enum CdnCertificateProblems
{
    None = 0,
    NotYetValid = 1,
    Expired = 2,
    NameMismatch = 4,
    UntrustedChain = 8
}

public sealed record CdnCertificateCheckResult(
    Uri Endpoint,
    DateTimeOffset CheckedAt,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Subject,
    string Issuer,
    string Sha256Fingerprint,
    string TlsProtocol,
    CdnCertificateProblems Problems,
    IReadOnlyList<string> ChainErrors)
{
    public long DaysRemaining => (long)Math.Floor((NotAfter - CheckedAt).TotalDays);
    public bool IsCurrentlyValid =>
        !Problems.HasFlag(CdnCertificateProblems.NotYetValid) &&
        !Problems.HasFlag(CdnCertificateProblems.Expired);
    public bool IsExpiringSoon => IsCurrentlyValid && DaysRemaining <= 30;

    public string StatusText
    {
        get
        {
            if (Problems.HasFlag(CdnCertificateProblems.Expired))
                return $"已过期 {Math.Abs(DaysRemaining)} 天";
            if (Problems.HasFlag(CdnCertificateProblems.NotYetValid))
                return "证书尚未生效";
            if (Problems.HasFlag(CdnCertificateProblems.NameMismatch))
                return "证书域名不匹配";
            if (Problems.HasFlag(CdnCertificateProblems.UntrustedChain))
                return "证书链不受信任";
            return IsExpiringSoon
                ? $"即将到期，剩余 {Math.Max(0, DaysRemaining)} 天"
                : $"正常，剩余 {DaysRemaining} 天";
        }
    }
}

public interface ICdnConfigurationStore
{
    Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CdnConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface ICdnCredentialStore
{
    Task<IReadOnlyList<CdnCredential>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyCollection<CdnCredential> credentials, CancellationToken cancellationToken = default);
}

public interface ICdnDeliveryService
{
    Task<CdnProbeResult> ProbeAsync(
        CdnProfile profile,
        CredentialProfile? credential,
        Uri url,
        long sampleBytes,
        CancellationToken cancellationToken);

    Task<CdnProbeResult> ProbeHeadAsync(
        CdnProfile profile,
        CredentialProfile? credential,
        Uri url,
        CancellationToken cancellationToken) =>
        ProbeAsync(profile, credential, url, 1, cancellationToken);

    Task<CdnDownloadResult> DownloadAsync(
        CdnProfile profile,
        CredentialProfile? credential,
        Uri url,
        Stream destination,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("当前 CDN Provider 不支持直接下载。");

    Task<CdnOperationResult> WarmupAsync(
        CdnProfile profile,
        CredentialProfile? credential,
        Uri url,
        CancellationToken cancellationToken);

    Task<CdnOperationResult> PurgeAsync(
        CdnProfile profile,
        CredentialProfile? credential,
        Uri url,
        CancellationToken cancellationToken);
}

public interface ICdnCertificateInspector
{
    Task<CdnCertificateCheckResult> InspectAsync(
        CdnProfile profile,
        CancellationToken cancellationToken);
}

public static class CdnConfigurationValidator
{
    private const string HttpTokenSymbols = "!#$%&'*+-.^_`|~";
    private static readonly HashSet<string> AllowedPurgeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    public static IReadOnlyList<string> Validate(
        CdnConfiguration configuration,
        IReadOnlyCollection<CredentialProfile>? credentials = null)
    {
        var errors = ValidateLegacy(configuration, null).ToList();
        if (credentials is null) return errors;

        var ids = credentials.Select(value => value.Id).ToHashSet();
        foreach (var credential in credentials)
        {
            try { credential.Validate(); }
            catch (ArgumentException exception) { errors.Add(exception.Message); }
        }
        foreach (var profile in configuration.Profiles)
        {
            if (profile.CredentialId is not Guid credentialId) continue;
            var credential = credentials.FirstOrDefault(value => value.Id == credentialId);
            if (!ids.Contains(credentialId) || credential is null)
                errors.Add($"CDN 配置“{profile.Name}”引用了不存在的凭据。");
            else if (!credential.IsCompatibleWith(profile.ProviderId))
                errors.Add($"CDN 配置“{profile.Name}”引用的凭据与 Provider 不兼容。");
        }
        return errors;
    }

    public static void EnsureValid(
        CdnConfiguration configuration,
        IReadOnlyCollection<CredentialProfile>? credentials = null)
    {
        var errors = Validate(configuration, credentials);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    public static IReadOnlyList<string> ValidateLegacy(
        CdnConfiguration configuration,
        IReadOnlyCollection<CdnCredential>? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        var profileIds = new HashSet<Guid>();
        var credentialIds = credentials?.Select(value => value.Id).ToHashSet();

        foreach (var profile in configuration.Profiles)
        {
            if (profile.Id == Guid.Empty) errors.Add("CDN 配置 ID 不能为空。");
            if (!profileIds.Add(profile.Id)) errors.Add($"CDN 配置 ID 重复：{profile.Id}");
            if (string.IsNullOrWhiteSpace(profile.Name)) errors.Add("CDN 配置名称不能为空。");
            if (profile.Notes.Length > CdnProfile.MaximumNotesLength)
                errors.Add($"CDN 配置“{profile.Name}”的备注不能超过 {CdnProfile.MaximumNotesLength} 个字符。");
            if (!string.Equals(profile.ProviderId, CdnProfile.GenericHttpProviderId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(profile.ProviderId, CdnProfile.AlibabaCloudProviderId, StringComparison.OrdinalIgnoreCase))
                errors.Add($"CDN 配置“{profile.Name}”使用了当前版本不支持的 Provider：{profile.ProviderId}");
            if (!TryHttpUri(profile.BaseUrl, out var baseUri) || baseUri is null ||
                !string.IsNullOrEmpty(baseUri.UserInfo) ||
                !string.IsNullOrEmpty(baseUri.Query) ||
                !string.IsNullOrEmpty(baseUri.Fragment))
                errors.Add($"CDN 配置“{profile.Name}”的基础 URL 必须是不含凭据、查询参数或片段的 HTTP/HTTPS 绝对地址。");
            if (!Enum.IsDefined(profile.WarmupMode))
                errors.Add($"CDN 配置“{profile.Name}”的预热模式无效。");
            if (profile.TimeoutSeconds is < 1 or > 3600)
                errors.Add($"CDN 配置“{profile.Name}”的超时必须在 1–3600 秒之间。");
            if (profile.WarmupRangeBytes is < 1 or > 1024L * 1024 * 1024)
                errors.Add($"CDN 配置“{profile.Name}”的预热 Range 大小必须在 1 字节到 1 GiB 之间。");
            if (profile.LastCertificateCheck is { } certificate &&
                (!string.Equals(certificate.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 certificate.CheckedAt == default || certificate.NotAfter <= certificate.NotBefore ||
                 !string.Equals(
                     certificate.Endpoint.AbsoluteUri.TrimEnd('/'),
                     profile.BaseUrl.TrimEnd('/'),
                     StringComparison.OrdinalIgnoreCase)))
                errors.Add($"CDN 配置“{profile.Name}”保存的 HTTPS 证书检测结果无效。");
            if (!AllowedPurgeMethods.Contains(profile.PurgeHttpMethod))
                errors.Add($"CDN 配置“{profile.Name}”的刷新方法不受支持：{profile.PurgeHttpMethod}");
            if (!string.IsNullOrWhiteSpace(profile.PurgeEndpointTemplate))
            {
                var sample = profile.PurgeEndpointTemplate
                    .Replace("{url}", Uri.EscapeDataString("https://cdn.example/object"), StringComparison.Ordinal)
                    .Replace("{path}", Uri.EscapeDataString("/object"), StringComparison.Ordinal);
                if (!TryHttpUri(sample, out var endpoint) || endpoint is null ||
                    !string.IsNullOrEmpty(endpoint.UserInfo) ||
                    !string.IsNullOrEmpty(endpoint.Fragment))
                    errors.Add($"CDN 配置“{profile.Name}”的刷新端点模板必须生成 HTTP/HTTPS 绝对地址。");
            }
            if (profile.CredentialId is Guid credentialId &&
                credentialIds is not null &&
                !credentialIds.Contains(credentialId))
                errors.Add($"CDN 配置“{profile.Name}”引用了不存在的凭据。");
        }

        if (credentials is not null)
        {
            var seenCredentialIds = new HashSet<Guid>();
            foreach (var credential in credentials)
            {
                if (credential.Id == Guid.Empty) errors.Add("CDN 凭据 ID 不能为空。");
                if (!seenCredentialIds.Add(credential.Id)) errors.Add($"CDN 凭据 ID 重复：{credential.Id}");
                if (string.IsNullOrWhiteSpace(credential.Name)) errors.Add("CDN 凭据名称不能为空。");
                if (!Enum.IsDefined(credential.AuthenticationType))
                    errors.Add($"CDN 凭据“{credential.Name}”的认证类型无效。");
                if (credential.AuthenticationType == CdnAuthenticationType.CustomHeader &&
                    !IsValidHttpHeaderName(credential.HeaderName))
                    errors.Add($"CDN 凭据“{credential.Name}”必须指定有效的 HTTP Header 名称。");
                if (credential.AuthenticationType != CdnAuthenticationType.None &&
                    string.IsNullOrEmpty(credential.Secret))
                    errors.Add($"CDN 凭据“{credential.Name}”缺少秘密值。");
                if (credential.Secret?.IndexOfAny(['\r', '\n']) >= 0)
                    errors.Add($"CDN 凭据“{credential.Name}”的秘密值不能包含换行符。");
            }
        }

        var bindingIds = new HashSet<Guid>();
        foreach (var binding in configuration.Bindings)
        {
            if (binding.Id == Guid.Empty) errors.Add("CDN 关联 ID 不能为空。");
            if (!bindingIds.Add(binding.Id)) errors.Add($"CDN 关联 ID 重复：{binding.Id}");
            if (binding.StorageProfileId == Guid.Empty) errors.Add("CDN 关联必须指定对象存储连接。");
            if (string.IsNullOrWhiteSpace(binding.Bucket)) errors.Add("CDN 关联必须指定 Bucket。");
            if (!profileIds.Contains(binding.CdnProfileId))
                errors.Add($"Bucket“{binding.Bucket}”引用了不存在的 CDN 配置。");
            if (binding.NewObjectAction is not (CdnUploadAction.None or CdnUploadAction.Warmup))
                errors.Add($"Bucket“{binding.Bucket}”的新对象自动化动作无效。");
            if (binding.OverwriteAction is not (
                    CdnUploadAction.None or CdnUploadAction.Purge or CdnUploadAction.PurgeThenWarmup))
                errors.Add($"Bucket“{binding.Bucket}”的覆盖对象自动化动作无效。");
            var profile = configuration.Profiles.FirstOrDefault(value => value.Id == binding.CdnProfileId);
            if (binding.OverwriteAction is CdnUploadAction.Purge or CdnUploadAction.PurgeThenWarmup &&
                profile is not null &&
                !profile.Capabilities.HasFlag(CdnCapabilities.Purge))
            {
                errors.Add($"Bucket“{binding.Bucket}”的覆盖自动化需要 CDN 配置“{profile.Name}”提供刷新端点。");
            }
        }

        foreach (var duplicate in configuration.Bindings
                     .GroupBy(value => (
                         value.StorageProfileId,
                         value.Bucket,
                         Prefix: CdnUrlMapper.NormalizePrefix(value.SourcePrefix),
                         value.CdnProfileId))
                     .Where(group => group.Count() > 1))
            errors.Add($"对象存储、Bucket、前缀和 CDN 配置的关联重复：{duplicate.Key.Bucket}/{duplicate.Key.Prefix}");

        foreach (var duplicate in configuration.Bindings
                     .Where(value => value.Enabled && value.IsDefault)
                     .GroupBy(value => (
                         value.StorageProfileId,
                         value.Bucket,
                         Prefix: CdnUrlMapper.NormalizePrefix(value.SourcePrefix)))
                     .Where(group => group.Count() > 1))
            errors.Add($"同一 Bucket/前缀只能有一个默认 CDN：{duplicate.Key.Bucket}/{duplicate.Key.Prefix}");

        return errors;
    }

    public static void EnsureLegacyValid(
        CdnConfiguration configuration,
        IReadOnlyCollection<CdnCredential>? credentials = null)
    {
        var errors = ValidateLegacy(configuration, credentials);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    public static bool IsValidHttpHeaderName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' ||
            HttpTokenSymbols.Contains(character));

    private static bool TryHttpUri(string value, out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}

public static class CdnUrlMapper
{
    public static IReadOnlyList<CdnResolvedTarget> ResolveAll(
        CdnConfiguration configuration,
        Guid storageProfileId,
        string bucket,
        string objectKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        objectKey = NormalizeObjectKey(objectKey);

        var profiles = configuration.Profiles
            .Where(value => value.Enabled)
            .ToDictionary(value => value.Id);
        var matches = configuration.Bindings
            .Where(value => value.Enabled &&
                value.StorageProfileId == storageProfileId &&
                string.Equals(value.Bucket, bucket, StringComparison.Ordinal) &&
                profiles.ContainsKey(value.CdnProfileId))
            .Select(value => (Binding: value, Prefix: NormalizePrefix(value.SourcePrefix)))
            .Where(value => objectKey.StartsWith(value.Prefix, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return [];
        var longest = matches.Max(value => value.Prefix.Length);
        return matches
            .Where(value => value.Prefix.Length == longest)
            .OrderByDescending(value => value.Binding.IsDefault)
            .ThenBy(value => profiles[value.Binding.CdnProfileId].Name, StringComparer.OrdinalIgnoreCase)
            .Select(value =>
            {
                var profile = profiles[value.Binding.CdnProfileId];
                return new CdnResolvedTarget(
                    profile,
                    value.Binding,
                    BuildUrl(profile, value.Binding, objectKey),
                    objectKey);
            })
            .ToArray();
    }

    public static CdnResolvedTarget? ResolveDefault(
        CdnConfiguration configuration,
        Guid storageProfileId,
        string bucket,
        string objectKey) =>
        ResolveAll(configuration, storageProfileId, bucket, objectKey).FirstOrDefault();

    public static Uri BuildUrl(CdnProfile profile, CdnBinding binding, string objectKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(binding);
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidOperationException("CDN 基础 URL 必须是 HTTP/HTTPS 绝对地址。");

        var sourcePrefix = NormalizePrefix(binding.SourcePrefix);
        var normalizedKey = NormalizeObjectKey(objectKey);
        if (!normalizedKey.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("对象 Key 不在 CDN 关联的源前缀内。");

        var relative = normalizedKey[sourcePrefix.Length..];
        var targetPath = NormalizePrefix(binding.CdnPathPrefix) + relative;
        var escapedPath = string.Join("/", targetPath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(profile.BaseUrl.TrimEnd('/') + "/" + escapedPath, UriKind.Absolute);
    }

    public static string NormalizePrefix(string? value)
    {
        var normalized = NormalizeObjectKey(value ?? string.Empty);
        return normalized.Length == 0 || normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    public static string NormalizeObjectKey(string value) =>
        value.Replace('\\', '/').TrimStart('/');
}
