using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

public sealed class TlsCdnCertificateInspector : ICdnCertificateInspector
{
    private readonly Func<DateTimeOffset> _utcNow;

    public TlsCdnCertificateInspector(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CdnCertificateCheckResult> InspectAsync(
        CdnProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HTTPS 证书检测要求 CDN 基础 URL 使用 https://。");

        var port = endpoint.IsDefaultPort ? 443 : endpoint.Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(endpoint.IdnHost, port, cancellationToken);

        X509Certificate2? capturedCertificate = null;
        SslPolicyErrors policyErrors = SslPolicyErrors.None;
        var chainErrors = new List<string>();
        using var tlsStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, chain, errors) =>
            {
                policyErrors = errors;
                if (certificate is not null)
                    capturedCertificate = X509CertificateLoader.LoadCertificate(
                        certificate.Export(X509ContentType.Cert));
                if (chain is not null)
                {
                    chainErrors.AddRange(chain.ChainStatus.Select(status =>
                        $"{status.Status}: {status.StatusInformation.Trim()}"));
                }

                // This diagnostic sends no HTTP request or credential. Accepting here lets us
                // report expired, self-signed and name-mismatched certificates to the user.
                return true;
            });

        try
        {
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.IdnHost,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                cancellationToken);

            var certificate = capturedCertificate ??
                throw new AuthenticationException("TLS 服务端没有返回证书。");
            capturedCertificate = null;
            using (certificate)
            {
                var checkedAt = _utcNow();
                var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
                var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
                var problems = CdnCertificateProblems.None;
                if (checkedAt < notBefore) problems |= CdnCertificateProblems.NotYetValid;
                if (checkedAt > notAfter) problems |= CdnCertificateProblems.Expired;
                if (policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                    problems |= CdnCertificateProblems.NameMismatch;
                if (policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                    problems |= CdnCertificateProblems.UntrustedChain;

                return new CdnCertificateCheckResult(
                    endpoint,
                    checkedAt,
                    notBefore,
                    notAfter,
                    certificate.Subject,
                    certificate.Issuer,
                    Convert.ToHexString(SHA256.HashData(certificate.RawData)),
                    tlsStream.SslProtocol.ToString(),
                    problems,
                    chainErrors.Distinct(StringComparer.Ordinal).ToArray());
            }
        }
        finally
        {
            capturedCertificate?.Dispose();
        }
    }
}
