using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Internal;
using Amazon.S3.Model;
using Amazon.Util;
using S3Explorer.Core;
using System.Runtime.CompilerServices;

namespace S3Explorer.Infrastructure.S3;

// Use the SDK's supported pipeline extension point before signing. Never repair
// HttpRequestMessage.RequestUri afterwards: that would invalidate the signature.
internal sealed class ExactObjectKeyS3Client : AmazonS3Client, IAmazonS3
{
    private readonly bool _validateCompatibleKeys;
    private readonly S3ServiceType _serviceType;

    public ExactObjectKeyS3Client(AWSCredentials credentials, AmazonS3Config config,
        bool followTemporaryRedirects, S3ServiceType serviceType)
        : base(credentials, DisableTransportRedirects(config))
    {
        _serviceType = serviceType;
        _validateCompatibleKeys = MinioObjectKeyPolicy.AppliesTo(serviceType);
        if (_validateCompatibleKeys)
            RuntimePipeline.AddHandlerBefore<Marshaller>(new CompatibleObjectKeyValidationHandler(serviceType));
        RuntimePipeline.AddHandlerBefore<Signer>(new ExactObjectKeyPipelineHandler());
        RuntimePipeline.ReplaceHandler<AmazonS3RedirectHandler>(new ExactObjectKeyRedirectHandler(followTemporaryRedirects));
    }

    // The SDK's presign methods are nonvirtual and bypass the runtime pipeline.
    // Explicit interface implementations protect the interface returned by our
    // factory, while the new methods protect callers using this concrete type.
    string IAmazonS3.GetPreSignedURL(GetPreSignedUrlRequest request) => GetPreSignedURL(request);
    Task<string> IAmazonS3.GetPreSignedURLAsync(GetPreSignedUrlRequest request) => GetPreSignedURLAsync(request);

    public new string GetPreSignedURL(GetPreSignedUrlRequest request)
    {
        ValidatePresignedUrl(request);
        return base.GetPreSignedURL(request);
    }

    public new Task<string> GetPreSignedURLAsync(GetPreSignedUrlRequest request)
    {
        ValidatePresignedUrl(request);
        return base.GetPreSignedURLAsync(request);
    }

    private void ValidatePresignedUrl(GetPreSignedUrlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_validateCompatibleKeys) MinioObjectKeyPolicy.Validate(_serviceType, request.Key);
        if (request.Key?.StartsWith('/') == true)
            throw new NotSupportedException("当前 SDK 无法安全生成以 / 开头的对象 Key 的预签名 URL；请使用应用内下载。");
    }

    private static AmazonS3Config DisableTransportRedirects(AmazonS3Config config)
    {
        // HttpClient must return 307 to the SDK guard, not follow Location behind
        // its back. Allowed ordinary redirects are re-signed by the SDK instead.
        config.AllowAutoRedirect = false;
        return config;
    }
}

internal sealed class CompatibleObjectKeyValidationHandler(S3ServiceType serviceType) : PipelineHandler
{
    public override void InvokeSync(IExecutionContext executionContext)
    {
        MinioObjectKeyPolicy.ValidateRequest(executionContext.RequestContext.OriginalRequest, serviceType);
        base.InvokeSync(executionContext);
    }

    public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        MinioObjectKeyPolicy.ValidateRequest(executionContext.RequestContext.OriginalRequest, serviceType);
        return base.InvokeAsync<T>(executionContext);
    }
}

internal sealed class ExactObjectKeyPipelineHandler : PipelineHandler
{
    private const string KeyResource = "{Key+}";
    private const string EndpointLabelPrefix = "{S3ExplorerEndpoint";
    private static readonly ConditionalWeakTable<IRequest, PathState> States = new();

    private sealed record PathState(string OriginalTemplate, string[] Labels, Uri RootEndpoint);

    public override void InvokeSync(IExecutionContext executionContext)
    {
        PreserveLeadingSlash(executionContext.RequestContext.Request);
        base.InvokeSync(executionContext);
    }

    public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        PreserveLeadingSlash(executionContext.RequestContext.Request);
        return base.InvokeAsync<T>(executionContext);
    }

    internal static void PreserveLeadingSlash(IRequest request)
    {
        if (request.PathResources is null || !request.PathResources.TryGetValue(KeyResource, out var key) || !key.StartsWith('/'))
            return;

        // A region-correction retry may replace Endpoint but retain the previous
        // template. Rebuild from the immutable original, never prefix it twice.
        if (States.TryGetValue(request, out var previous) && !ReferenceEquals(request.Endpoint, previous.RootEndpoint))
        {
            request.ResourcePath = previous.OriginalTemplate;
            foreach (var label in previous.Labels) request.PathResources.Remove(label);
            States.Remove(request);
        }

        // ComposeUrl removes the template's first slash, resolves {Key+}, then
        // omits the separator whenever the resolved key starts with '/'. With a
        // path-style endpoint ending in /bucket, /key is therefore sent as key.
        // The signer *does* retain that separator, so adding a slash alone is not
        // a fix. Move the endpoint path into the template: both SDK algorithms
        // then produce exactly /bucket//key using the same unmodified key value.
        var endpointPath = request.Endpoint.AbsolutePath;
        if (endpointPath != "/")
        {
            if (request.ResourcePath != "/{Key+}")
                throw new NotSupportedException("无法确认特殊对象 Key 的请求路径，已停止操作以防止访问其他对象。");

            var segments = endpointPath[1..].Split('/', StringSplitOptions.None);
            // Endpoint's final slash, when present, is the structural separator.
            if (segments.Length > 0 && segments[^1].Length == 0)
                segments = segments[..^1];
            var labels = new string[segments.Length];
            for (var index = 0; index < segments.Length; index++)
            {
                var label = EndpointLabelPrefix + index + "}";
                if (request.PathResources.ContainsKey(label))
                    throw new InvalidOperationException("特殊对象请求路径发生冲突，已停止操作。");
                labels[index] = label;
                // Each nongreedy label encodes the endpoint segment once, even
                // when it contains an escaped slash or percent sign.
                request.PathResources.Add(label, Uri.UnescapeDataString(segments[index]));
            }
            var originalTemplate = request.ResourcePath;
            request.ResourcePath = "/" + string.Join('/', labels) + "/{Key+}";
            request.Endpoint = new Uri(request.Endpoint.GetLeftPart(UriPartial.Authority) + "/");
            States.Add(request, new PathState(originalTemplate, labels, request.Endpoint));
        }

        // Also check on retries. The endpoint is already root after the first
        // pass, so no prefix is added twice. A future SDK algorithm change must
        // fail closed instead of sending a differently signed or aliased path.
        var wirePath = AmazonServiceClient.ComposeUrl(request).AbsolutePath;
        var canonicalPath = AWSSDKUtils.CanonicalizeResourcePathV2(
            request.Endpoint, request.ResourcePath, false, request.PathResources);
        if (!string.Equals(wirePath, canonicalPath, StringComparison.Ordinal))
            throw new NotSupportedException("无法无损签名特殊对象 Key，已停止操作以防止访问其他对象。");
    }
}

internal sealed class ExactObjectKeyRedirectHandler(bool followTemporaryRedirects) : AmazonS3RedirectHandler
{
    protected override void FinalizeForRedirect(IExecutionContext executionContext, string redirectedLocation)
    {
        if (!followTemporaryRedirects)
            RefuseRedirect(executionContext, "当前连接已禁用临时重定向；请配置正确的 Endpoint 后重试。");
        // S3's redirect handler signs directly, bypassing the runtime pre-signer.
        // A redirected authority/path can require a different bucket boundary;
        // refuse this unsupported route rather than silently target an alias.
        var request = executionContext.RequestContext.Request;
        if (request.PathResources is not null &&
            request.PathResources.TryGetValue("{Key+}", out var key) && key.StartsWith('/'))
            RefuseRedirect(executionContext, "以 / 开头的对象 Key 不支持自动重定向；请配置正确的区域 Endpoint 后重试。");
        base.FinalizeForRedirect(executionContext, redirectedLocation);
    }

    private static void RefuseRedirect(IExecutionContext executionContext, string message)
    {
        // The base handler normally disposes this after FinalizeForRedirect.
        // Refusal throws before that point, so release the body/connection here.
        executionContext.ResponseContext.HttpResponse?.ResponseBody?.Dispose();
        executionContext.ResponseContext.HttpResponse = null;
        throw new NotSupportedException(message);
    }
}
