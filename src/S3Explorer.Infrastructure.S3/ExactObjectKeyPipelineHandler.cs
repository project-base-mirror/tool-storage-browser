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
    private readonly AWSCredentials _credentials;
    private readonly S3ServiceType _serviceType;

    public ExactObjectKeyS3Client(AWSCredentials credentials, AmazonS3Config config,
        bool followTemporaryRedirects, S3ServiceType serviceType, bool hasCustomHostHeader = false)
        : base(credentials, DisableTransportRedirects(config))
    {
        _serviceType = serviceType;
        _credentials = credentials;
        _validateCompatibleKeys = MinioObjectKeyPolicy.AppliesTo(serviceType);
        if (_validateCompatibleKeys)
            RuntimePipeline.AddHandlerBefore<Marshaller>(new CompatibleObjectKeyValidationHandler(serviceType));
        if (serviceType == S3ServiceType.GoogleCloudStorage)
            RuntimePipeline.AddHandlerBefore<Unmarshaller>(new GcsMetadataResponseHandler());
        RuntimePipeline.AddHandlerBefore<Signer>(new ExactObjectKeyPipelineHandler());
        if (serviceType is S3ServiceType.AliyunOss or S3ServiceType.TencentCos)
            RuntimePipeline.AddHandlerBefore<Amazon.Runtime.Internal.Unmarshaller>(new NativeProviderMetadataResponseHandler(
                serviceType == S3ServiceType.AliyunOss ? "x-oss-" : "x-cos-"));
        RuntimePipeline.ReplaceHandler<AmazonS3RedirectHandler>(new ExactObjectKeyRedirectHandler(followTemporaryRedirects, serviceType, hasCustomHostHeader));
    }

    // The SDK's presign methods are nonvirtual and bypass the runtime pipeline.
    // Explicit interface implementations protect the interface returned by our
    // factory, while the new methods protect callers using this concrete type.
    string IAmazonS3.GetPreSignedURL(GetPreSignedUrlRequest request) => GetPreSignedURL(request);
    Task<string> IAmazonS3.GetPreSignedURLAsync(GetPreSignedUrlRequest request) => GetPreSignedURLAsync(request);

    public new string GetPreSignedURL(GetPreSignedUrlRequest request)
    {
        ValidatePresignedUrl(request);
        return request.Key?.StartsWith('/') == true
            ? ExactObjectKeyPresigner.Sign(request, (AmazonS3Config)Config, _credentials.GetCredentials())
            : base.GetPreSignedURL(request);
    }

    public new async Task<string> GetPreSignedURLAsync(GetPreSignedUrlRequest request)
    {
        ValidatePresignedUrl(request);
        if (request.Key?.StartsWith('/') != true)
            return await base.GetPreSignedURLAsync(request).ConfigureAwait(false);
        var credentials = await _credentials.GetCredentialsAsync().ConfigureAwait(false);
        return ExactObjectKeyPresigner.Sign(request, (AmazonS3Config)Config, credentials);
    }

    private void ValidatePresignedUrl(GetPreSignedUrlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_validateCompatibleKeys) MinioObjectKeyPolicy.Validate(_serviceType, request.Key);
        ExactObjectKeyPresigner.ValidatePortableKey(request.Key);
        if (request.Key?.StartsWith('/') == true) ExactObjectKeyPresigner.ValidateRequest(request);
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

    private sealed record PathState(string OriginalTemplate, string[] Labels, Uri OriginalEndpoint, Uri RootEndpoint);

    public override void InvokeSync(IExecutionContext executionContext)
    {
        var request = executionContext.RequestContext.Request;
        PreserveLeadingSlash(request);
        try { base.InvokeSync(executionContext); }
        catch { RestoreForRetry(request); throw; }
    }

    public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        var request = executionContext.RequestContext.Request;
        PreserveLeadingSlash(request);
        try { return await base.InvokeAsync<T>(executionContext).ConfigureAwait(false); }
        catch { RestoreForRetry(request); throw; }
    }

    private static void RestoreForRetry(IRequest request)
    {
        // BucketRegionDetector parses Endpoint while an exception unwinds.
        // Give it the unmodified bucket boundary before the next attempt is
        // resolved and signed. Otherwise path-style requests look bucketless.
        if (!States.TryGetValue(request, out var previous)) return;
        if (ReferenceEquals(request.Endpoint, previous.RootEndpoint)) request.Endpoint = previous.OriginalEndpoint;
        RestoreEndpointPath(request);
    }

    internal static void RestoreEndpointPath(IRequest request)
    {
        if (!States.TryGetValue(request, out var previous)) return;
        request.ResourcePath = previous.OriginalTemplate;
        foreach (var label in previous.Labels) request.PathResources.Remove(label);
        States.Remove(request);
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
            var originalEndpoint = request.Endpoint;
            request.Endpoint = new Uri(request.Endpoint.GetLeftPart(UriPartial.Authority) + "/");
            States.Add(request, new PathState(originalTemplate, labels, originalEndpoint, request.Endpoint));
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

// The S3 unmarshaller drops Google-only revision and protection headers. Capture
// only an explicit allowlist after unmarshalling; never retain credentials.
internal sealed class GcsMetadataResponseHandler : PipelineHandler
{
    public override void InvokeSync(IExecutionContext context)
    {
        base.InvokeSync(context);
        Capture(context);
    }

    public override async Task<T> InvokeAsync<T>(IExecutionContext context)
    {
        var result = await base.InvokeAsync<T>(context).ConfigureAwait(false);
        Capture(context);
        return result;
    }

    private static void Capture(IExecutionContext context)
    {
        if (context.RequestContext.OriginalRequest is not GetObjectMetadataRequest ||
            context.ResponseContext.Response is not GetObjectMetadataResponse response ||
            context.ResponseContext.HttpResponse is not { } raw) return;
        foreach (var name in raw.GetHeaderNames())
        {
            if (name.StartsWith("x-goog-meta-", StringComparison.OrdinalIgnoreCase))
                response.Metadata[name[12..]] = raw.GetHeaderValue(name);
            else if (PreservedHeaders.Contains(name))
                response.Headers[name.ToLowerInvariant()] = raw.GetHeaderValue(name);
        }
    }

    private static readonly HashSet<string> PreservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-goog-generation", "x-goog-metageneration", "x-goog-custom-time",
        "x-goog-storage-class", "x-goog-encryption-kms-key-name",
        "x-goog-encryption-algorithm", "x-goog-encryption-key-sha256",
        "x-goog-object-lock-mode", "x-goog-object-lock-retain-until-date"
    };
}

// Native providers may return their own header names even for an S3-signed HEAD.
// Preserve only object metadata/protection fields, never arbitrary/auth headers.
internal sealed class NativeProviderMetadataResponseHandler(string prefix) : PipelineHandler
{
    public override void InvokeSync(IExecutionContext context)
    {
        base.InvokeSync(context);
        Capture(context);
    }

    public override async Task<T> InvokeAsync<T>(IExecutionContext context)
    {
        var result = await base.InvokeAsync<T>(context).ConfigureAwait(false);
        Capture(context);
        return result;
    }

    private void Capture(IExecutionContext context)
    {
        if (context.RequestContext.OriginalRequest is not GetObjectMetadataRequest ||
            context.ResponseContext.Response is not GetObjectMetadataResponse response ||
            context.ResponseContext.HttpResponse is not { } raw) return;
        foreach (var name in raw.GetHeaderNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = name[prefix.Length..].ToLowerInvariant();
            if (suffix.StartsWith("meta-", StringComparison.Ordinal))
            {
                var key = suffix[5..];
                var value = raw.GetHeaderValue(name);
                if (response.Metadata.Keys.Any(existing =>
                    (existing.Equals(key, StringComparison.OrdinalIgnoreCase) || existing.Equals("x-amz-meta-" + key, StringComparison.OrdinalIgnoreCase)) &&
                    response.Metadata[existing] != value))
                    response.MissingMeta = Math.Max(1, response.MissingMeta ?? 0);
                response.Metadata[key] = value;
            }
            else if (PreservedSuffixes.Contains(suffix))
                response.Headers[name.ToLowerInvariant()] = raw.GetHeaderValue(name);
        }
    }

    private static readonly HashSet<string> PreservedSuffixes = new(StringComparer.Ordinal)
    {
        "storage-class", "version-id", "object-type", "website-redirect-location", "missing-meta",
        "server-side-encryption", "server-side-encryption-key-id", "server-side-encryption-kms-key-id",
        "server-side-encryption-cos-kms-key-id", "server-side-encryption-context",
        "server-side-encryption-customer-algorithm", "server-side-encryption-customer-key-md5",
        "object-lock-mode", "object-lock-retain-until-date", "object-lock-legal-hold",
        "object-worm-mode", "object-worm-retain-until-date"
    };
}
