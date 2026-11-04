using System.Globalization;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.S3;
using Amazon.S3.Internal;
using Amazon.S3.Model;
using Amazon.Util;

namespace S3Explorer.Infrastructure.S3;

// AWSSDK.S3 4.0.102's nonvirtual presigner bypasses RuntimePipeline. For the
// download-only case, use the SDK's public endpoint resolver and SigV4 signer
// with the same pre-sign path repair as ordinary requests. No custom crypto or
// editing of a signed URL. Advanced signing modes remain with the SDK or fail
// explicitly rather than silently omitting options.
internal static class ExactObjectKeyPresigner
{
    internal static void ValidatePortableKey(string? key)
    {
        // A browser resolves literal dot path segments before sending the URL.
        // .NET's non-canonicalizing request URI cannot make such links portable.
        if (key?.Split('/').Any(segment => segment is "." or "..") == true)
            throw new NotSupportedException("含 . 或 .. 路径段的对象 Key 无法生成可安全打开的预签名链接；请使用应用内下载。");
    }

    internal static void ValidateRequest(GetPreSignedUrlRequest request)
    {
        if (request.Verb is not (HttpVerb.GET or HttpVerb.HEAD) ||
            !string.IsNullOrEmpty(request.UploadId) || request.PartNumber.HasValue ||
            request.Headers.Keys.Any() || request.Metadata.Keys.Any() ||
            request.ServerSideEncryptionMethod is not null && request.ServerSideEncryptionMethod != ServerSideEncryptionMethod.None ||
            request.ServerSideEncryptionCustomerMethod is not null && request.ServerSideEncryptionCustomerMethod != ServerSideEncryptionCustomerMethod.None ||
            !string.IsNullOrEmpty(request.ServerSideEncryptionKeyManagementServiceKeyId))
            throw new NotSupportedException("以 / 开头的 Key 仅支持不附带上传或加密 Header 的 GET/HEAD 预签名下载链接。");
        if (string.IsNullOrEmpty(request.BucketName) || request.BucketName.StartsWith("arn:", StringComparison.OrdinalIgnoreCase) ||
            request.BucketName.EndsWith("--x-s3", StringComparison.OrdinalIgnoreCase) ||
            request.BucketName.EndsWith("-s3alias", StringComparison.OrdinalIgnoreCase) ||
            request.BucketName.EndsWith("--ol-s3", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("此特殊 Key 的预签名下载仅支持普通 Bucket，不支持访问点、ARN 或目录 Bucket。");
        if (!request.Expires.HasValue)
            throw new ArgumentException("预签名链接必须指定有效期。", nameof(request));
        if (request.Protocol is not (Protocol.HTTP or Protocol.HTTPS))
            throw new ArgumentException("预签名链接协议无效。", nameof(request));
        foreach (var name in request.Parameters.Keys)
            if (name.StartsWith("X-Amz-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AWSAccessKeyId", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Signature", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Expires", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("自定义查询参数不能覆盖签名参数。", nameof(request));
    }

    internal static string Sign(GetPreSignedUrlRequest original, AmazonS3Config config, ImmutableCredentials credentials)
    {
        ValidateRequest(original);
        var request = new DefaultRequest(original, "Amazon.S3")
        {
            HttpMethod = original.Verb.ToString(), ResourcePath = "/{Key+}", UseQueryString = true
        };
        request.AddPathResource("{Key+}", original.Key);
        if (!string.IsNullOrEmpty(original.VersionId)) request.AddSubResource("versionId", original.VersionId);
        if (original.RequestPayer == RequestPayer.Requester)
            request.Parameters.Add("x-amz-request-payer", RequestPayer.Requester.Value);
        var overrides = original.ResponseHeaderOverrides;
        AddParameter("response-cache-control", overrides.CacheControl);
        AddParameter("response-content-type", overrides.ContentType);
        AddParameter("response-content-language", overrides.ContentLanguage);
        AddParameter("response-expires", overrides.Expires);
        AddParameter("response-content-disposition", overrides.ContentDisposition);
        AddParameter("response-content-encoding", overrides.ContentEncoding);
        foreach (var name in original.Parameters.Keys) request.Parameters.Add(name, original.Parameters[name]);
        if (credentials.UseToken) request.Parameters.Add("X-Amz-Security-Token", credentials.Token);

        var context = new Amazon.Runtime.Internal.ExecutionContext(new RequestContext(true, new NullSigner())
        {
            Request = request, ClientConfig = config, OriginalRequest = original
        }, null);
        new AmazonS3EndpointResolver().ProcessRequestHandlers(context);
        // Match the requested protocol before signing, including any explicit
        // nonstandard port. This never modifies the shared client configuration.
        var scheme = original.Protocol == Protocol.HTTP ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        if (request.Endpoint.Scheme != scheme)
        {
            var endpoint = new UriBuilder(request.Endpoint) { Scheme = scheme };
            if (request.Endpoint.IsDefaultPort) endpoint.Port = -1;
            request.Endpoint = endpoint.Uri;
        }
        var expires = Convert.ToInt64((original.Expires!.Value.ToUniversalTime() -
            CorrectClockSkew.GetCorrectedUtcNowForEndpoint(request.Endpoint.ToString())).TotalSeconds);
        if (expires <= 0 || expires > AWS4PreSignedUrlSigner.MaxAWS4PreSignedUrlExpiry)
            throw new ArgumentOutOfRangeException(nameof(original), "有效期必须大于 0 且不超过 7 天。");
        request.Parameters.Add("X-Amz-Expires", expires.ToString(CultureInfo.InvariantCulture));
        ExactObjectKeyPipelineHandler.PreserveLeadingSlash(request);
        var signature = new AWS4PreSignedUrlSigner().SignRequest(request, config, new RequestMetrics(),
            credentials.AccessKey, credentials.SecretKey);
        return AmazonServiceClient.ComposeUrl(request).AbsoluteUri + "&" + signature.ForQueryParameters;

        void AddParameter(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) request.Parameters.Add(name, value);
        }
    }
}
