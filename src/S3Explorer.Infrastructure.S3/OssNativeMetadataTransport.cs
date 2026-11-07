using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aliyun.OSS;
using Amazon.Runtime;
using S3Explorer.Core;
using OssMetadata = Aliyun.OSS.ObjectMetadata;

namespace S3Explorer.Infrastructure.S3;

// OSS4 algorithm follows Alibaba's published V4 specification and is tested against
// the pinned official SDK's signer. No SDK transport is constructed: its legacy
// transport changes a process-wide certificate callback, which this path must avoid.
internal sealed class OssNativeMetadataTransport(HttpClient http, string endpoint, string region,
    ImmutableCredentials credentials, CancellationToken cancellationToken) : S3StorageService.IOssMetadataClient
{
    public VersioningStatus Versioning(string bucket)
    {
        using var response = Send(HttpMethod.Get, bucket, null, "versioning");
        var root = ReadXml(response, "VersioningConfiguration");
        var status = root.Element("Status")?.Value;
        return status switch
        {
            null or "" => VersioningStatus.Off,
            "Enabled" => VersioningStatus.Enabled,
            "Suspended" => VersioningStatus.Suspended,
            _ => throw new NotSupportedException("OSS 返回未知版本控制状态，未写入。")
        };
    }

    public void RequireNoBucketWorm(string bucket)
    {
        using var response = Send(HttpMethod.Get, bucket, null, "worm", permitError: true);
        if (response.StatusCode == HttpStatusCode.NotFound && ReadXml(response, "Error").Element("Code")?.Value == "NoSuchWORMConfiguration") return;
        EnsureSuccess(response);
        _ = ReadXml(response, "WormConfiguration");
        throw new NotSupportedException("OSS BucketWorm 已配置，未写入 Metadata。");
    }

    public OssMetadata Head(string bucket, string key)
    {
        using var response = Send(HttpMethod.Head, bucket, key);
        var value = new OssMetadata();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var text = string.Join(", ", header.Value);
            if (header.Key.StartsWith("x-oss-meta-", StringComparison.OrdinalIgnoreCase))
                value.UserMetadata.Add(header.Key[11..], text);
            else if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                value.HttpMetadata["Content-Length"] = long.Parse(text, CultureInfo.InvariantCulture);
            else if (header.Key.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase))
                value.AddHeader("Last-Modified", DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime);
            else value.AddHeader(CanonicalHeaderName(header.Key), text);
        }
        return value;
    }

    public CannedAccessControlList Acl(string bucket, string key)
    {
        using var response = Send(HttpMethod.Get, bucket, key, "acl");
        var root = ReadXml(response, "AccessControlPolicy");
        if (string.IsNullOrEmpty(root.Element("Owner")?.Element("ID")?.Value))
            throw new NotSupportedException("OSS 未返回完整 ACL 所有者，未写入。");
        return root.Element("AccessControlList")?.Element("Grant")?.Value switch
        {
            "default" => CannedAccessControlList.Default, "private" => CannedAccessControlList.Private,
            "public-read" => CannedAccessControlList.PublicRead, "public-read-write" => CannedAccessControlList.PublicReadWrite,
            _ => throw new NotSupportedException("OSS 返回未知或不完整 ACL，未写入。")
        };
    }

    public void Copy(CopyObjectRequest request)
    {
        var headers = request.NewObjectMetadata.HttpMetadata.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()!, StringComparer.OrdinalIgnoreCase);
        headers.Remove("Content-Length");
        headers["x-oss-metadata-directive"] = "REPLACE";
        headers["x-oss-copy-source"] = "/" + request.SourceBucketName + "/" + OssV4MetadataSigner.EncodePath(request.SourceKey);
        headers["x-oss-copy-source-if-match"] = request.MatchingETagConstraints.Single();
        foreach (var pair in request.NewObjectMetadata.UserMetadata) headers["x-oss-meta-" + pair.Key] = pair.Value;
        using var response = Send(HttpMethod.Put, request.DestinationBucketName, request.DestinationKey, headers: headers);
        var root = ReadXml(response, "CopyObjectResult");
        if (string.IsNullOrEmpty(root.Element("ETag")?.Value))
            throw new InvalidOperationException("OSS CopyObject 未返回完整成功响应，请重新读取核对。");
    }

    private HttpResponseMessage Send(HttpMethod method, string bucket, string? key, string? query = null,
        Dictionary<string, string>? headers = null, bool permitError = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Validate before adding credentials or dispatching: snapshot bucket names
        // must never be interpreted as URI user-info, a path, or another authority.
        static bool AlphaNumeric(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
        if (bucket.Length is < 3 or > 63 || !AlphaNumeric(bucket[0]) || !AlphaNumeric(bucket[^1]) ||
            bucket.Any(value => !AlphaNumeric(value) && value != '-'))
            throw new ArgumentException("OSS Bucket 名称必须为 3–63 位小写 ASCII 字母、数字或连字符，首尾为字母或数字。", nameof(bucket));
        if (query is not (null or "acl" or "versioning" or "worm"))
            throw new ArgumentException("OSS 原生请求包含未知操作。", nameof(query));
        headers ??= new(StringComparer.OrdinalIgnoreCase);
        var host = bucket + "." + new Uri(endpoint).Host;
        var path = "/" + (key is null ? "" : OssV4MetadataSigner.EncodePath(key));
        var uri = new Uri("https://" + host + path + (query is null ? "" : "?" + query),
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, host, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            uri.Query != (query is null ? "" : "?" + query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("OSS 原生请求 URI 与预期目标不一致，未发送凭据。");
        headers["Host"] = host;
        OssV4MetadataSigner.Sign(method.Method, bucket, key, query ?? "", region, credentials, headers, DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(method, uri);
        if (method == HttpMethod.Put) request.Content = new ByteArrayContent([]);
        foreach (var pair in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
            {
                request.Content ??= new ByteArrayContent([]);
                if (!request.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                    throw new ArgumentException($"OSS Header 无法发送：{pair.Key}");
            }
        }
        // No retries and no redirects. Dispatch cancellation throws into the workflow's
        // uncertain-outcome error, rather than claiming a rollback.
        var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!permitError)
        {
            try { EnsureSuccess(response); }
            catch { response.Dispose(); throw; }
        }
        return response;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OSS 原生请求被服务端拒绝（HTTP {(int)response.StatusCode}）；未进行无条件重试。");
    }

    private XElement ReadXml(HttpResponseMessage response, string expectedRoot)
    {
        const int maximumBytes = 256 * 1024;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(http.Timeout);
        using var stream = response.Content.ReadAsStream(timeout.Token);
        using var limited = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult()) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limited.Length + count > maximumBytes) throw new InvalidOperationException("OSS 响应超过安全读取上限，停止。");
            limited.Write(buffer, 0, count);
        }
        limited.Position = 0;
        using var reader = XmlReader.Create(limited, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != expectedRoot) throw new InvalidOperationException("OSS 响应结构不完整或操作未成功，停止。");
        return root;
    }

    private static string CanonicalHeaderName(string name) => name.ToLowerInvariant() switch
    {
        "etag" => "ETag", "content-type" => "Content-Type", "content-encoding" => "Content-Encoding",
        "content-disposition" => "Content-Disposition", "cache-control" => "Cache-Control", "expires" => "Expires",
        _ => name.ToLowerInvariant()
    };
}

internal static class OssV4MetadataSigner
{
    internal static string EncodePath(string value) => string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    internal static void Sign(string method, string bucket, string? key, string canonicalQuery, string region,
        ImmutableCredentials credentials, IDictionary<string, string> headers, DateTimeOffset now)
    {
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        headers["x-oss-date"] = timestamp;
        headers["x-oss-content-sha256"] = "UNSIGNED-PAYLOAD";
        if (!string.IsNullOrEmpty(credentials.Token)) headers["x-oss-security-token"] = credentials.Token;
        // Sign every supplied semantic header, including Host and HTTP metadata.
        var additional = headers.Keys.Select(value => value.ToLowerInvariant())
            .Where(value => !IsDefault(value) && value != "authorization").Order(StringComparer.Ordinal).ToArray();
        var sorted = headers.Where(pair => pair.Key != "Authorization")
            .OrderBy(pair => pair.Key.ToLowerInvariant(), StringComparer.Ordinal);
        var canonicalHeaders = string.Concat(sorted.Select(pair => pair.Key.ToLowerInvariant() + ":" + pair.Value.Trim() + "\n"));
        var canonical = method + "\n" + EncodePath("/" + bucket + "/" + (key ?? "")) + "\n" + canonicalQuery + "\n" +
            canonicalHeaders + "\n" + string.Join(';', additional) + "\nUNSIGNED-PAYLOAD";
        var scope = date + "/" + region + "/oss/aliyun_v4_request";
        var stringToSign = "OSS4-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var signingKey = Hmac(Encoding.UTF8.GetBytes("aliyun_v4" + credentials.SecretKey), date);
        signingKey = Hmac(signingKey, region);
        signingKey = Hmac(signingKey, "oss");
        signingKey = Hmac(signingKey, "aliyun_v4_request");
        var signature = Convert.ToHexStringLower(Hmac(signingKey, stringToSign));
        headers["Authorization"] = "OSS4-HMAC-SHA256 Credential=" + credentials.AccessKey + "/" + scope +
            (additional.Length == 0 ? "" : ",AdditionalHeaders=" + string.Join(';', additional)) + ",Signature=" + signature;
    }
    private static bool IsDefault(string name) => name is "content-type" or "content-md5" || name.StartsWith("x-oss-", StringComparison.Ordinal);
    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}
