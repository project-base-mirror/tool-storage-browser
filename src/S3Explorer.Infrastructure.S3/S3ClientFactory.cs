using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed class S3ClientFactory
{
    public IAmazonS3 Create(ConnectionProfile profile)
    {
        profile.Validate();

        AWSCredentials credentials = string.IsNullOrWhiteSpace(profile.SessionToken)
            ? new BasicAWSCredentials(profile.AccessKey, profile.SecretKey)
            : new SessionAWSCredentials(profile.AccessKey, profile.SecretKey, profile.SessionToken);

        var endpoint = new Uri(profile.Endpoint);
        var config = new AmazonS3Config
        {
            ServiceURL = profile.Endpoint.TrimEnd('/'),
            AuthenticationRegion = profile.Region,
            ForcePathStyle = profile.AddressingStyle == AddressingStyle.PathStyle,
            UseHttp = endpoint.Scheme == Uri.UriSchemeHttp,
            Timeout = TimeSpan.FromSeconds(profile.RequestTimeoutSeconds),
            ReadWriteTimeout = TimeSpan.FromSeconds(profile.RequestTimeoutSeconds),
            MaxErrorRetry = 3
        };

        if (profile.ServiceType == S3ServiceType.AmazonS3 &&
            profile.Endpoint.Contains("amazonaws.com", StringComparison.OrdinalIgnoreCase))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(profile.Region);
        }

        return new AmazonS3Client(credentials, config);
    }
}
