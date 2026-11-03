using System.Security.Cryptography;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

internal static class S3UploadCompatibility
{
    public static void ValidateEndpoint(ConnectionProfile? profile)
    {
        if (profile?.ServiceType == S3ServiceType.CloudflareR2 && profile.NormalizedEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Cloudflare R2 上传需要 HTTPS Endpoint。");
    }

    public static async Task PrepareAsync(ConnectionProfile? profile, PutObjectRequest request,
        Stream source, CancellationToken cancellationToken)
    {
        if (profile?.ServiceType != S3ServiceType.CloudflareR2) return;
        request.MD5Digest = await ContentMd5Async(profile, source, cancellationToken).ConfigureAwait(false);
        request.UseChunkEncoding = false;
        request.DisablePayloadSigning = true;
        request.DisableDefaultChecksumValidation = true;
    }

    public static async Task PrepareAsync(ConnectionProfile? profile, UploadPartRequest request,
        Stream source, CancellationToken cancellationToken)
    {
        if (profile?.ServiceType != S3ServiceType.CloudflareR2) return;
        request.MD5Digest = await ContentMd5Async(profile, source, cancellationToken).ConfigureAwait(false);
        request.UseChunkEncoding = false;
        request.DisablePayloadSigning = true;
        request.DisableDefaultChecksumValidation = true;
    }

    private static async Task<string> ContentMd5Async(ConnectionProfile profile, Stream source,
        CancellationToken cancellationToken)
    {
        // R2 rejects the SDK's streaming SigV4 payload. Keep request authentication and
        // HTTPS, and send a supported Content-MD5 instead of disabling integrity checks entirely.
        ValidateEndpoint(profile);
        if (!source.CanSeek)
            throw new ArgumentException("R2 上传源必须支持定位，以便校验后重新读取。", nameof(source));
        var position = source.Position;
        try
        {
            return Convert.ToBase64String(await MD5.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
        }
        finally { source.Position = position; }
    }
}
