using System.Security.Cryptography;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

internal static class S3UploadCompatibility
{
    public static void ValidateEndpoint(ConnectionProfile? profile)
    {
        if (UsesUnsignedPayload(profile) && profile!.NormalizedEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("UnsignedPayload 上传签名模式需要 HTTPS Endpoint。");
    }

    public static async Task PrepareAsync(ConnectionProfile? profile, PutObjectRequest request,
        Stream source, CancellationToken cancellationToken)
    {
        if (!UsesUnsignedPayload(profile)) return;
        request.MD5Digest = await ContentMd5Async(profile!, source, cancellationToken).ConfigureAwait(false);
        request.UseChunkEncoding = false;
        request.DisablePayloadSigning = true;
        request.DisableDefaultChecksumValidation = true;
    }

    public static async Task PrepareAsync(ConnectionProfile? profile, UploadPartRequest request,
        Stream source, CancellationToken cancellationToken)
    {
        if (!UsesUnsignedPayload(profile)) return;
        request.MD5Digest = await ContentMd5Async(profile!, source, cancellationToken).ConfigureAwait(false);
        request.UseChunkEncoding = false;
        request.DisablePayloadSigning = true;
        request.DisableDefaultChecksumValidation = true;
    }

    private static bool UsesUnsignedPayload(ConnectionProfile? profile) => profile?.UploadSigningMode switch
    {
        null => false,
        UploadSigningMode.Auto => profile.ServiceType == S3ServiceType.CloudflareR2,
        UploadSigningMode.Streaming => false,
        UploadSigningMode.UnsignedPayload => true,
        _ => throw new ArgumentOutOfRangeException(nameof(profile.UploadSigningMode), "上传签名模式无效。")
    };

    private static async Task<string> ContentMd5Async(ConnectionProfile profile, Stream source,
        CancellationToken cancellationToken)
    {
        // Some S3-compatible services reject streaming SigV4. Keep request authentication
        // and HTTPS; explicit Content-MD5 replaces the SDK streaming checksum, not integrity checks.
        ValidateEndpoint(profile);
        if (!source.CanSeek)
            throw new ArgumentException("UnsignedPayload 上传源必须支持定位，以便校验后重新读取。", nameof(source));
        var position = source.Position;
        try
        {
            return Convert.ToBase64String(await MD5.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
        }
        finally { source.Position = position; }
    }
}
