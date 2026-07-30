using S3Explorer.Core;

namespace S3Explorer.App;

internal static class CdnCertificatePersistence
{
    public static CdnConfiguration Apply(
        CdnConfiguration configuration,
        Guid profileId,
        CdnCertificateCheckResult result)
    {
        var profile = configuration.Profiles.FirstOrDefault(value => value.Id == profileId)
            ?? throw new InvalidOperationException("该 CDN 配置尚未保存，请先保存配置再检测证书。");
        if (!string.Equals(
                profile.BaseUrl.TrimEnd('/'),
                result.Endpoint.AbsoluteUri.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CDN 地址存在未保存的修改，请先保存配置再检测证书。");
        }
        return configuration with
        {
            Profiles = configuration.Profiles
                .Select(value => value.Id == profileId
                    ? value with { LastCertificateCheck = result }
                    : value)
                .ToArray()
        };
    }
}
