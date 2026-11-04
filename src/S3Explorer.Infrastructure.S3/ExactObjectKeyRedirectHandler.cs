using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3.Internal;
using Amazon.Util;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

// Replace the S3 redirect handler, whose direct signing bypasses our pre-signer
// and whose URI parser does not establish that an authority belongs to AWS.
internal sealed class ExactObjectKeyRedirectHandler(
    bool followTemporaryRedirects, S3ServiceType serviceType, bool hasCustomHostHeader) : RedirectHandler
{
    private readonly ConditionalWeakTable<IRequest, RedirectCount> _redirects = new();
    private sealed class RedirectCount { public int Value; }

    protected override void FinalizeForRedirect(IExecutionContext executionContext, string redirectedLocation)
    {
        try
        {
            if (!followTemporaryRedirects)
                throw new NotSupportedException("当前连接已禁用临时重定向；请配置正确的 Endpoint 后重试。");
            if (hasCustomHostHeader)
                throw new NotSupportedException("自定义 Host Header 的连接不支持自动重定向；请直接配置目标 Endpoint。");
            var request = executionContext.RequestContext.Request;
            if (++_redirects.GetValue(request, static _ => new RedirectCount()).Value > 3)
                throw new NotSupportedException("临时重定向次数过多；请配置正确的 Endpoint 后重试。");
            if (request.SignatureVersion != SignatureVersion.SigV4) throw UnsafeRedirect();
            // HttpClientResponseData reads the typed Location header. System.Uri
            // resolves dot-only segments before the SDK exposes its string, so
            // the original redirect path cannot be verified here. The current
            // request still addresses the exact key; never redirect to its alias.
            if (request.PathResources is not null && request.PathResources.TryGetValue("{Key+}", out var redirectKey) &&
                redirectKey.Split('/').Any(segment => segment is "." or ".."))
                throw new NotSupportedException("含 . 或 .. 路径段的对象 Key 无法安全自动重定向；请直接配置正确的区域 Endpoint 后重试。");
            var target = ParseLocation(redirectedLocation);
            var current = AmazonServiceClient.ComposeUrl(request);
            if (target.Scheme != current.Scheme || target.Port != current.Port ||
                !string.Equals(target.Query, current.Query, StringComparison.Ordinal))
                throw UnsafeRedirect();

            string? region = null;
            Uri? endpoint = null;
            if (!string.Equals(target.Authority, current.Authority, StringComparison.OrdinalIgnoreCase))
            {
                // Cross-authority redirects are supported only between verified
                // AWS regional S3 endpoints for this same bucket and exact key.
                // A Custom/R2/etc. connection never forwards credentials to a
                // different host just because that host appeared in Location.
                var bucket = request.CanonicalResourcePrefix?.TrimStart('/');
                if (serviceType != S3ServiceType.AmazonS3 || string.IsNullOrEmpty(bucket) || bucket.Contains('/') ||
                    request.PathResources is null || !request.PathResources.TryGetValue("{Key+}", out var key) ||
                    !TryAwsAddress(current, bucket, out var oldPathStyle, out var oldRegion) ||
                    !TryAwsAddress(target, bucket, out var pathStyle, out region) ||
                    Partition(oldRegion) != Partition(region) ||
                    current.AbsolutePath != ObjectPath(bucket, key, oldPathStyle) ||
                    target.AbsolutePath != ObjectPath(bucket, key, pathStyle))
                    throw UnsafeRedirect();
                endpoint = new Uri(target.GetLeftPart(UriPartial.Authority) +
                    (pathStyle ? "/" + AWSSDKUtils.UrlEncode(bucket, false) : "/"));
            }
            else if (!string.Equals(target.AbsolutePath, current.AbsolutePath, StringComparison.Ordinal))
                throw UnsafeRedirect();

            // All identity/authority checks precede any re-signing. Prepare the
            // SDK request, not the final HTTP URI. Keep the body replay rules and
            // SDK signature implementation used by AmazonS3RedirectHandler.
            if (request.UseChunkEncoding && request.Headers.TryGetValue("x-amz-decoded-content-length", out var length))
                request.Headers["Content-Length"] = length;
            request.Headers.Remove("Host");
            if (endpoint is not null)
            {
                ExactObjectKeyPipelineHandler.RestoreEndpointPath(request);
                request.Endpoint = endpoint;
            }
            var preparedEndpoint = request.Endpoint;
            base.FinalizeForRedirect(executionContext, redirectedLocation);
            request.Endpoint = preparedEndpoint;
            if (region is not null) request.AuthenticationRegion = region;
            ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);

            Signer.SignRequest(executionContext.RequestContext);
        }
        catch
        {
            // The base loop disposes the body only after this method returns.
            // Refusal or signing failure must release it as well.
            executionContext.ResponseContext.HttpResponse?.ResponseBody?.Dispose();
            executionContext.ResponseContext.HttpResponse = null;
            throw;
        }
    }

    private static Uri ParseLocation(string location)
    {
        if (string.IsNullOrEmpty(location) || location.Contains('#') || location.Contains('\\') ||
            location.Any(char.IsControl) || location.Any(char.IsWhiteSpace)) throw UnsafeRedirect();
        for (var index = 0; index < location.Length; index++)
            if (location[index] == '%' && (index + 2 >= location.Length ||
                !Uri.IsHexDigit(location[index + 1]) || !Uri.IsHexDigit(location[index + 2]))) throw UnsafeRedirect();
        Uri target;
        try
        {
            target = new Uri(location, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        }
        catch (UriFormatException) { throw UnsafeRedirect(); }
        if (!target.IsAbsoluteUri || target.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(target.UserInfo)) throw UnsafeRedirect();
        return target;
    }

    private static string ObjectPath(string bucket, string key, bool pathStyle) =>
        "/" + (pathStyle ? AWSSDKUtils.UrlEncode(bucket, false) + "/" : "") +
        // Greedy SDK labels encode each segment, not legacy UrlEncode(path:true),
        // which would leave characters such as '+' unescaped in the signed path.
        string.Join('/', key.Split('/').Select(segment => AWSSDKUtils.UrlEncode(segment, false)));

    private static bool TryAwsAddress(Uri uri, string bucket, out bool pathStyle, out string region)
    {
        pathStyle = true;
        region = "";
        if (!uri.IsDefaultPort || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.IdnHost;
        var suffix = host.EndsWith(".amazonaws.com.cn", StringComparison.Ordinal) ? ".amazonaws.com.cn" : ".amazonaws.com";
        if (!host.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var service = host[..^suffix.Length];
        if (service.StartsWith(bucket + ".", StringComparison.Ordinal))
        {
            service = service[(bucket.Length + 1)..];
            pathStyle = false;
        }
        if (service == "s3") region = "us-east-1";
        else if (service.StartsWith("s3.dualstack.", StringComparison.Ordinal)) region = service[13..];
        else if (service.StartsWith("s3.", StringComparison.Ordinal) || service.StartsWith("s3-", StringComparison.Ordinal)) region = service[3..];
        else return false;
        var resolvedRegion = region;
        return RegionEndpoint.EnumerableAllRegions.Any(value => value.SystemName == resolvedRegion) &&
            (region.StartsWith("cn-", StringComparison.Ordinal) == (suffix == ".amazonaws.com.cn"));
    }

    private static string Partition(string region) => region.StartsWith("cn-", StringComparison.Ordinal) ? "china" :
        region.StartsWith("us-gov-", StringComparison.Ordinal) ? "gov" : "commercial";

    private static NotSupportedException UnsafeRedirect() => new(
        "重定向无法确认保持原 Bucket、对象 Key、查询参数及可信 Endpoint，已在转发凭据前停止；请直接配置正确的 Endpoint。");
}
