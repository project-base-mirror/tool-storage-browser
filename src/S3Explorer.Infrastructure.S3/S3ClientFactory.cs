using System.Net;
using System.Net.Security;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

public sealed record S3ClientOptionsSnapshot(
    string ServiceUrl,
    string AuthenticationRegion,
    bool UsesSessionCredentials,
    bool ForcePathStyle,
    bool DisableHostPrefixInjection,
    bool AllowAutoRedirect,
    bool IgnoreCertificateErrors,
    string CustomHostHeader,
    string CredentialSource);

public sealed record S3ClientCreation(
    IAmazonS3 Client,
    AwsCredentialResolution CredentialResolution);

public sealed class S3ClientFactory
{
    private readonly AwsCredentialResolver _credentialResolver;

    public S3ClientFactory(AwsCredentialResolver? credentialResolver = null) =>
        _credentialResolver = credentialResolver ?? new AwsCredentialResolver();

    public IAmazonS3 Create(ConnectionProfile profile)
        => CreateResolved(profile).Client;

    public S3ClientCreation CreateResolved(ConnectionProfile profile, bool allowInteractiveSso = false)
    {
        profile.Validate();
        var config = CreateConfig(profile);
        var resolution = _credentialResolver.Resolve(profile, allowInteractiveSso);
        var client = new AmazonS3Client(resolution.Credentials, config);
        if (!string.IsNullOrWhiteSpace(profile.CustomHostHeader))
        {
            var customHost = profile.CustomHostHeader.Trim();
            client.BeforeRequestEvent += (_, args) =>
            {
                if (args is WebServiceRequestEventArgs request)
                    request.Headers["Host"] = customHost;
            };
        }

        return new(client, resolution);
    }

    public AmazonS3Config CreateConfig(ConnectionProfile profile)
    {
        profile.Validate();
        var endpoint = profile.NormalizedEndpoint;
        var forcePathStyle = S3CompatibilityPolicy.ShouldForcePathStyle(profile);
        var config = new AmazonS3Config
        {
            ServiceURL = EndpointCompatibility.NormalizeServiceUrl(profile.ServiceType, profile.Endpoint),
            AuthenticationRegion = profile.EffectiveSignatureRegion,
            ForcePathStyle = forcePathStyle,
            DisableHostPrefixInjection = forcePathStyle,
            UseHttp = endpoint.Scheme == Uri.UriSchemeHttp,
            Timeout = TimeSpan.FromSeconds(profile.RequestTimeoutSeconds),
            MaxErrorRetry = 3,
            AllowAutoRedirect = profile.FollowTemporaryRedirects,
            RequestChecksumCalculation = profile.ServiceType == S3ServiceType.AmazonS3
                ? RequestChecksumCalculation.WHEN_SUPPORTED
                : RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = profile.ServiceType == S3ServiceType.AmazonS3
                ? ResponseChecksumValidation.WHEN_SUPPORTED
                : ResponseChecksumValidation.WHEN_REQUIRED
        };

        if (profile.IgnoreCertificateErrors)
            config.HttpClientFactory = new CompatibilityHttpClientFactory();

        if (profile.ServiceType == S3ServiceType.AmazonS3 &&
            !string.IsNullOrWhiteSpace(profile.Region) &&
            !string.Equals(profile.Region.Trim(), "auto", StringComparison.OrdinalIgnoreCase) &&
            endpoint.Host.EndsWith("amazonaws.com", StringComparison.OrdinalIgnoreCase) &&
            endpoint.AbsolutePath == "/")
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(profile.Region);
        }

        return config;
    }

    public S3ClientOptionsSnapshot Describe(ConnectionProfile profile)
    {
        var config = CreateConfig(profile);
        return new(
            config.ServiceURL,
            config.AuthenticationRegion,
            profile.UsesTemporarySessionCredentials,
            config.ForcePathStyle,
            config.DisableHostPrefixInjection,
            config.AllowAutoRedirect,
            profile.IgnoreCertificateErrors,
            profile.CustomHostHeader.Trim(),
            profile.CredentialSourceDisplayName);
    }

    private sealed class CompatibilityHttpClientFactory : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = clientConfig.AllowAutoRedirect,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            handler.SslOptions.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true;

            var timeout = clientConfig.Timeout ?? Timeout.InfiniteTimeSpan;
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout
            };
        }

        public override string GetConfigUniqueString(IClientConfig clientConfig)
        {
            var timeout = clientConfig.Timeout ?? Timeout.InfiniteTimeSpan;
            return $"s3explorer-insecure:{clientConfig.AllowAutoRedirect}:{timeout.TotalSeconds}";
        }
    }
}
