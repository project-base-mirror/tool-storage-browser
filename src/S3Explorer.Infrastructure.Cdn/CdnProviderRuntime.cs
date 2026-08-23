using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

/// <summary>One authoritative registration point for built-in CDN providers.</summary>
public static class CdnProviderRuntime
{
    public static IReadOnlyList<ICdnProvider> CreateProviders(ICdnDeliveryService deliveryService)
    {
        ArgumentNullException.ThrowIfNull(deliveryService);
        return
        [
            new GenericHttpCdnProvider(deliveryService),
            new AliyunCdnProvider(),
            new TencentCdnProvider(),
            new CloudflareCdnProvider(deliveryService: deliveryService)
        ];
    }

    public static IReadOnlyDictionary<string, ICdnControlPermissionChecker> CreateControlPermissionCheckers() =>
        new ICdnControlPermissionChecker[]
        {
            new AliyunCdnProvider(),
            new TencentCdnProvider(),
            new CloudflareCdnProvider()
        }.ToDictionary(value => value.ProviderId, StringComparer.OrdinalIgnoreCase);
}
