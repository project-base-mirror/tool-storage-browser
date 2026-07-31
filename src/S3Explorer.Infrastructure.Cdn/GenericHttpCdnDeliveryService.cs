using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.Cdn;

public sealed class GenericHttpCdnDeliveryService : ICdnDeliveryService
{
    private readonly Func<CdnProfile, HttpMessageHandler> _handlerFactory;

    public GenericHttpCdnDeliveryService(
        Func<CdnProfile, HttpMessageHandler>? handlerFactory = null)
    {
        _handlerFactory = handlerFactory ?? (profile => new HttpClientHandler
        {
            AllowAutoRedirect = profile.FollowRedirects,
            AutomaticDecompression = DecompressionMethods.All
        });
    }

    public async Task<CdnProbeResult> ProbeAsync(
        CdnProfile profile,
        CdnCredential? credential,
        Uri url,
        long sampleBytes,
        CancellationToken cancellationToken)
    {
        ValidateUrl(url);
        if (sampleBytes is < 1 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(
                nameof(sampleBytes),
                "下载测试样本必须在 1 字节到 1 GiB 之间。");

        using var client = CreateClient(profile);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, sampleBytes - 1);
        ApplyCredential(request, credential);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var timeToHeaders = stopwatch.Elapsed;
        var bytesRead = await ReadUpToAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            sampleBytes,
            cancellationToken);
        stopwatch.Stop();

        var headers = CollectHeaders(response);
        return new CdnProbeResult(
            url,
            response.RequestMessage?.RequestUri ?? url,
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            timeToHeaders,
            stopwatch.Elapsed,
            bytesRead,
            response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength,
            response.Content.Headers.ContentType?.ToString(),
            DetectCacheStatus(headers),
            headers);
    }

    public async Task<CdnProbeResult> ProbeHeadAsync(
        CdnProfile profile,
        CdnCredential? credential,
        Uri url,
        CancellationToken cancellationToken)
    {
        ValidateUrl(url);
        using var client = CreateClient(profile);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        ApplyCredential(request, credential);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var timeToHeaders = stopwatch.Elapsed;
        stopwatch.Stop();
        var headers = CollectHeaders(response);
        return new CdnProbeResult(
            url,
            response.RequestMessage?.RequestUri ?? url,
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            timeToHeaders,
            stopwatch.Elapsed,
            0,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentType?.ToString(),
            DetectCacheStatus(headers),
            headers);
    }

    public async Task<CdnDownloadResult> DownloadAsync(
        CdnProfile profile,
        CdnCredential? credential,
        Uri url,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ValidateUrl(url);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("CDN 下载目标流不可写。", nameof(destination));

        using var client = CreateClient(profile);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCredential(request, credential);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"CDN 下载失败：{(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[128 * 1024];
        long bytesWritten = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesWritten += read;
        }
        await destination.FlushAsync(cancellationToken);

        return new CdnDownloadResult(
            url,
            response.RequestMessage?.RequestUri ?? url,
            (int)response.StatusCode,
            bytesWritten,
            response.Content.Headers.ContentType?.ToString());
    }

    public async Task<CdnOperationResult> WarmupAsync(
        CdnProfile profile,
        CdnCredential? credential,
        Uri url,
        CancellationToken cancellationToken)
    {
        ValidateUrl(url);
        if (profile.WarmupRangeBytes is < 1 or > 1024L * 1024 * 1024)
            throw new InvalidOperationException("预热 Range 大小必须在 1 字节到 1 GiB 之间。");
        using var client = CreateClient(profile);
        using var request = new HttpRequestMessage(
            profile.WarmupMode == CdnWarmupMode.Head ? HttpMethod.Head : HttpMethod.Get,
            url);
        if (profile.WarmupMode == CdnWarmupMode.RangeGet)
            request.Headers.Range = new RangeHeaderValue(0, profile.WarmupRangeBytes - 1);
        ApplyCredential(request, credential);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        long bytesRead = 0;
        if (profile.WarmupMode != CdnWarmupMode.Head)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            bytesRead = profile.WarmupMode == CdnWarmupMode.RangeGet
                ? await ReadUpToAsync(stream, profile.WarmupRangeBytes, cancellationToken)
                : await CopyToNullAsync(stream, cancellationToken);
        }
        stopwatch.Stop();

        var success = (int)response.StatusCode is >= 200 and < 400;
        return new CdnOperationResult(
            success,
            (int)response.StatusCode,
            stopwatch.Elapsed,
            bytesRead,
            success
                ? "HTTP 预热请求已完成。"
                : $"HTTP 预热失败：{(int)response.StatusCode} {response.ReasonPhrase}");
    }

    public async Task<CdnOperationResult> PurgeAsync(
        CdnProfile profile,
        CdnCredential? credential,
        Uri url,
        CancellationToken cancellationToken)
    {
        ValidateUrl(url);
        if (string.IsNullOrWhiteSpace(profile.PurgeEndpointTemplate))
            return new CdnOperationResult(
                false,
                null,
                TimeSpan.Zero,
                0,
                "此 CDN 配置没有设置通用刷新端点。");

        var endpoint = ExpandEndpointTemplate(profile.PurgeEndpointTemplate, url);
        using var client = CreateClient(profile);
        using var request = new HttpRequestMessage(
            new HttpMethod(profile.PurgeHttpMethod.ToUpperInvariant()),
            endpoint);
        if (!string.IsNullOrWhiteSpace(profile.PurgeBodyTemplate))
        {
            request.Content = new StringContent(
                ExpandBodyTemplate(profile.PurgeBodyTemplate, url),
                Encoding.UTF8,
                string.IsNullOrWhiteSpace(profile.PurgeContentType)
                    ? "application/json"
                    : profile.PurgeContentType);
        }
        ApplyCredential(request, credential);

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var snippet = await ReadSnippetAsync(response.Content, cancellationToken);
        stopwatch.Stop();
        var success = (int)response.StatusCode is >= 200 and < 300;
        return new CdnOperationResult(
            success,
            (int)response.StatusCode,
            stopwatch.Elapsed,
            0,
            success
                ? "刷新请求已提交。"
                : $"刷新请求失败：{(int)response.StatusCode} {response.ReasonPhrase}",
            snippet);
    }

    private HttpClient CreateClient(CdnProfile profile) =>
        new(_handlerFactory(profile), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 1, 3600))
        };

    private static void ApplyCredential(
        HttpRequestMessage request,
        CdnCredential? credential)
    {
        if (credential is null ||
            credential.AuthenticationType == CdnAuthenticationType.None)
            return;
        if (string.IsNullOrEmpty(credential.Secret))
            throw new InvalidOperationException("CDN 凭据缺少秘密值。");
        if (credential.Secret.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidOperationException("CDN 凭据秘密值不能包含换行符。");

        if (credential.AuthenticationType == CdnAuthenticationType.BearerToken)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credential.Secret);
            return;
        }

        if (!CdnConfigurationValidator.IsValidHttpHeaderName(credential.HeaderName))
            throw new InvalidOperationException("自定义 Header 凭据缺少有效的 HTTP Header 名称。");
        if (!request.Headers.TryAddWithoutValidation(
                credential.HeaderName,
                credential.Secret))
            throw new InvalidOperationException(
                $"无法添加 CDN 凭据 Header：{credential.HeaderName}");
    }

    private static Uri ExpandEndpointTemplate(string template, Uri url)
    {
        var expanded = template
            .Replace(
                "{url}",
                Uri.EscapeDataString(url.AbsoluteUri),
                StringComparison.Ordinal)
            .Replace(
                "{path}",
                Uri.EscapeDataString(url.AbsolutePath),
                StringComparison.Ordinal);
        if (!Uri.TryCreate(expanded, UriKind.Absolute, out var endpoint) ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException(
                "刷新端点模板没有生成有效的 HTTP/HTTPS 地址。");
        return endpoint;
    }

    private static string ExpandBodyTemplate(string template, Uri url) =>
        template
            .Replace("{url}", JsonString(url.AbsoluteUri), StringComparison.Ordinal)
            .Replace("{path}", JsonString(url.AbsolutePath), StringComparison.Ordinal);

    private static string JsonString(string value)
    {
        var json = JsonSerializer.Serialize(value);
        return json.Length >= 2 ? json[1..^1] : string.Empty;
    }

    private static IReadOnlyDictionary<string, string> CollectHeaders(
        HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            result[header.Key] = string.Join(", ", header.Value);
        foreach (var header in response.Content.Headers)
            result[header.Key] = string.Join(", ", header.Value);
        return result;
    }

    private static string DetectCacheStatus(
        IReadOnlyDictionary<string, string> headers)
    {
        foreach (var name in new[]
                 {
                     "CF-Cache-Status",
                     "X-Cache",
                     "X-Cache-Status",
                     "Age",
                     "Via"
                 })
            if (headers.TryGetValue(name, out var value) &&
                !string.IsNullOrWhiteSpace(value))
                return $"{name}: {value}";
        return "未识别";
    }

    private static async Task<long> ReadUpToAsync(
        Stream stream,
        long limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (total < limit)
        {
            var count = (int)Math.Min(buffer.Length, limit - total);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, count),
                cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task<long> CopyToNullAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return total;
            total += read;
        }
    }

    private static async Task<string> ReadSnippetAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(
            stream,
            ResolveEncoding(content),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: false);
        var buffer = new char[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return new string(buffer, 0, total);
    }

    private static Encoding ResolveEncoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Fall back to UTF-8 for invalid or unsupported response charsets.
            }
        }
        return Encoding.UTF8;
    }

    private static void ValidateUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri ||
            (!string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Fragment))
            throw new ArgumentException(
                "CDN URL 必须是 HTTP/HTTPS 绝对地址。",
                nameof(url));
    }
}
