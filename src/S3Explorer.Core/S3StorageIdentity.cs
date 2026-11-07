namespace S3Explorer.Core;

/// <summary>Identifies the storage namespace used by a connection, not its saved profile name.</summary>
public static class S3StorageIdentity
{
    public static bool IsSameStorage(ConnectionProfile source, ConnectionProfile destination)
    {
        var left = EffectiveEndpoint(source);
        var right = EffectiveEndpoint(destination);
        if (left == right) return true;
        // AWS regional/global S3 endpoints address the same bucket namespace within
        // a partition. Compare the effective Host, not the reverse proxy's address.
        return source.ServiceType == S3ServiceType.AmazonS3 && destination.ServiceType == S3ServiceType.AmazonS3 &&
            AmazonS3Partition(left) is { } partition && partition == AmazonS3Partition(right);
    }

    private static Uri EffectiveEndpoint(ConnectionProfile profile)
    {
        var endpoint = new Uri(EndpointCompatibility.NormalizeServiceUrl(profile.ServiceType, profile.Endpoint));
        if (string.IsNullOrWhiteSpace(profile.CustomHostHeader)) return endpoint;
        var authority = new Uri(endpoint.Scheme + "://" + profile.CustomHostHeader.Trim());
        return new UriBuilder(endpoint) { Host = authority.Host, Port = authority.Port }.Uri;
    }

    private static string? AmazonS3Partition(Uri endpoint)
    {
        if (endpoint.AbsolutePath != "/" || !endpoint.IsDefaultPort) return null;
        var host = endpoint.DnsSafeHost;
        if (!host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("s3-", StringComparison.OrdinalIgnoreCase)) return null;
        if (host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase)) return "aws-cn";
        if (host.StartsWith("s3.us-gov-", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("s3-us-gov-", StringComparison.OrdinalIgnoreCase))
            return host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) ? "aws-us-gov" : null;
        return host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) ? "aws" : null;
    }
}
