namespace S3Explorer.Core;

/// <summary>CDN provider metadata shared by configuration, validation and clients.</summary>
public sealed record CdnProviderDescriptor(
    string Id,
    string DisplayName,
    CredentialProviderKind ControlCredentialProvider,
    IReadOnlySet<CredentialKind> ControlCredentialKinds,
    CdnCapabilities NativeCapabilities,
    bool SupportsControlPlane,
    bool RequiresControlResourceId,
    string ControlResourceLabel = "",
    int? ControlResourceLength = null,
    bool ControlResourceMustBeHex = false);

public static class CdnProviderCatalog
{
    private static readonly IReadOnlyDictionary<string, CdnProviderDescriptor> Providers =
        new Dictionary<string, CdnProviderDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            [CdnProfile.GenericHttpProviderId] = new(
                CdnProfile.GenericHttpProviderId, "通用 HTTP", CredentialProviderKind.GenericHttp,
                new HashSet<CredentialKind> { CredentialKind.BearerToken, CredentialKind.CustomHeader }, CdnCapabilities.None, false, false),
            [CdnProfile.AlibabaCloudProviderId] = new(
                CdnProfile.AlibabaCloudProviderId, "阿里云 CDN", CredentialProviderKind.AlibabaCloud,
                new HashSet<CredentialKind> { CredentialKind.AccessKeyPair }, CdnCapabilities.Purge | CdnCapabilities.Warmup, true, false),
            [CdnProfile.TencentCloudProviderId] = new(
                CdnProfile.TencentCloudProviderId, "腾讯云 CDN", CredentialProviderKind.TencentCloud,
                new HashSet<CredentialKind> { CredentialKind.AccessKeyPair }, CdnCapabilities.Purge | CdnCapabilities.Warmup, true, false),
            [CdnProfile.CloudflareProviderId] = new(
                CdnProfile.CloudflareProviderId, "Cloudflare", CredentialProviderKind.Cloudflare,
                new HashSet<CredentialKind> { CredentialKind.BearerToken }, CdnCapabilities.Purge, true, true,
                "Zone ID", 32, true)
        };

    public static IReadOnlyCollection<CdnProviderDescriptor> All => Providers.Values.ToArray();

    public static bool TryGet(string? providerId, out CdnProviderDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(providerId) && Providers.TryGetValue(providerId.Trim(), out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static CdnProviderDescriptor Get(string providerId) =>
        TryGet(providerId, out var descriptor)
            ? descriptor
            : throw new ArgumentException($"不支持的 CDN Provider：{providerId}", nameof(providerId));

    public static bool IsCredentialCompatible(string providerId, CredentialProfile credential)
    {
        return TryGet(providerId, out var descriptor) &&
               credential.Provider == descriptor.ControlCredentialProvider &&
               descriptor.ControlCredentialKinds.Contains(credential.Kind);
    }

    public static bool IsControlResourceIdValid(CdnProviderDescriptor descriptor, string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!descriptor.RequiresControlResourceId)
            return normalized.Length == 0 || IsSafeControlResourceId(normalized);
        if (!IsSafeControlResourceId(normalized))
            return false;
        if (descriptor.ControlResourceLength is int length && normalized.Length != length)
            return false;
        return !descriptor.ControlResourceMustBeHex || normalized.All(Uri.IsHexDigit);
    }

    private static bool IsSafeControlResourceId(string value) =>
        value.Length is > 0 and <= 256 && value.IndexOfAny(['\r', '\n']) < 0;
}
