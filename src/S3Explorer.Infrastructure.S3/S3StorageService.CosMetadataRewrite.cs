using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using COSXML;
using COSXML.Auth;
using COSXML.CosException;
using COSXML.Model;
using COSXML.Model.Bucket;
using COSXML.Model.Object;
using COSXML.Model.Tag;
using S3Explorer.Core;
using CosCopyRequest = COSXML.Model.Object.CopyObjectRequest;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    private static async Task RewriteCosMetadataAsync(IAmazonS3 s3, ConnectionProfile profile,
        ImmutableCredentials credentials, ObjectProperties expected, ObjectWriteHeaders headers, CancellationToken token)
    {
        var region = ValidateCosMetadataEndpoint(profile, expected.Bucket);
        // The official SDK network stack installs a process-wide accept-all TLS callback.
        // Use ONLY its offline request builders / credential provider / signer. Our handler
        // keeps platform certificate verification and never follows credential-bearing redirects.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(profile.RequestTimeoutSeconds) };
        await RewriteCosMetadataAsync(s3, profile, expected, headers,
            (request, cancellation) => ExecuteCosMetadataRequestAsync(http, region, expected.Bucket, credentials, request, cancellation), token).ConfigureAwait(false);
    }

    internal static HttpRequestMessage CreateSignedCosMetadataRequest(string region, string bucket,
        ImmutableCredentials credentials, CosRequest request)
    {
        var host = bucket + ".cos." + region + ".myqcloud.com";
        if (request is COSXML.Model.Object.ObjectRequest objectRequest) ValidateCosObjectKey(objectRequest.Key);
        var path = request.RequestPath;
        var parameters = request.GetRequestParamters();
        var encoded = parameters.ToDictionary(p => Uri.EscapeDataString(p.Key).ToLowerInvariant(),
            p => Uri.EscapeDataString(p.Value ?? ""), StringComparer.Ordinal);
        var query = string.Join("&", encoded.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        var escapedPath = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        var exactPathAndQuery = escapedPath + (query.Length == 0 ? "" : "?" + query);
        var uri = new Uri("https://" + host + exactPathAndQuery,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        if (uri.PathAndQuery != exactPathAndQuery || uri.Host != host ||
            Uri.UnescapeDataString(escapedPath) != path)
            throw new NotSupportedException("COS Key 无法精确保留在请求 URI 中，已停止。");
        var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (request.Method == "PUT") message.Content = new ByteArrayContent([]);
        var headers = request.GetRequestHeaders().ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value, StringComparer.Ordinal);
        headers["host"] = host;
        if (!string.IsNullOrEmpty(credentials.Token)) headers["x-cos-security-token"] = credentials.Token;
        foreach (var pair in headers)
        {
            if (pair.Value.Any(c => c > 127 || c == '\r' || c == '\n'))
            {
                message.Dispose();
                throw new NotSupportedException("COS 原生 HTTP Header 目前仅支持可无损传输的 ASCII 值。");
            }
            if (!message.Headers.TryAddWithoutValidation(pair.Key, pair.Value) &&
                !(message.Content?.Headers.TryAddWithoutValidation(pair.Key, pair.Value) ?? false))
            {
                message.Dispose();
                throw new NotSupportedException("COS Header 不能无损传输：" + pair.Key);
            }
        }
        QCloudCredentialProvider provider = string.IsNullOrEmpty(credentials.Token)
            ? new DefaultQCloudCredentialProvider(credentials.AccessKey, credentials.SecretKey, 600)
            : new DefaultSessionQCloudCredentialProvider(credentials.AccessKey, credentials.SecretKey, 600, credentials.Token);
        var authorization = CosXmlSigner.GenerateSign(request.Method, path, encoded, headers,
            null, null, provider.GetQCloudCredentials());
        message.Headers.TryAddWithoutValidation("Authorization", authorization);
        return message;
    }

    internal static async Task<CosResult> ExecuteCosMetadataRequestAsync(HttpClient http, string region,
        string bucket, ImmutableCredentials credentials, CosRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (http.Timeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(http.Timeout);
        token = timeout.Token;
        using var message = CreateSignedCosMetadataRequest(region, bucket, credentials, request);
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var result = new CosResult
        {
            httpCode = (int)response.StatusCode,
            responseHeaders = response.Headers.Concat(response.Content.Headers)
                .ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase)
        };
        if (request.Method != "HEAD")
        {
            // ACL, Tags and copy responses are tiny. Bound reads without the SDK's
            // content-type/10KB raw-XML capture limitation; do not accept truncation.
            await response.Content.LoadIntoBufferAsync(1024 * 1024, token).ConfigureAwait(false);
            result.RawContentBodyString = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try { code = XElement.Parse(result.RawContentBodyString ?? "").Element("Code")?.Value; }
            catch (System.Xml.XmlException) { }
            throw new CosServerException((int)response.StatusCode, "COS request failed") { errorCode = code };
        }
        return result;
    }

    internal static string ValidateCosMetadataEndpoint(ConnectionProfile profile, string bucket)
    {
        var endpoint = profile.NormalizedEndpoint;
        var match = Regex.Match(endpoint.Host, @"^cos\.([a-z0-9-]+)\.myqcloud\.com$", RegexOptions.CultureInvariant);
        if (!match.Success || endpoint.Scheme != "https" || !endpoint.IsDefaultPort || endpoint.AbsolutePath != "/" ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0 ||
            !profile.UseHttps || profile.IgnoreCertificateErrors || !string.IsNullOrEmpty(profile.CustomHostHeader) ||
            profile.AddressingStyle == AddressingStyle.PathStyle)
            throw new NotSupportedException("COS 原生 Metadata 编辑需要标准区域 HTTPS Endpoint 与 Auto/VirtualHosted 寻址；自定义域名、Host、路径及证书例外尚不能安全映射，不会绕过连接设置。");
        if (!Regex.IsMatch(bucket, @"^[a-z0-9][a-z0-9-]*-[0-9]+$", RegexOptions.CultureInvariant))
            throw new NotSupportedException("COS Bucket 必须包含完整 APPID 后缀。");
        var region = match.Groups[1].Value;
        if (profile.EffectiveSignatureRegion != region)
            throw new NotSupportedException("COS Endpoint 与签名区域不一致，已停止原生 Metadata 编辑。");
        return region;
    }

    internal static async Task RewriteCosMetadataAsync(IAmazonS3 s3, ConnectionProfile profile,
        ObjectProperties expected, ObjectWriteHeaders headers,
        Func<CosRequest, CancellationToken, Task<CosResult>> execute, CancellationToken token)
    {
        var region = ValidateCosMetadataEndpoint(profile, expected.Bucket);
        ValidateCosObjectKey(expected.Key);
        var normalized = headers.ValidateForMetadataCopy();
        await CheckSnapshot().ConfigureAwait(false);
        var first = CosHeadHeaders(await Run(new HeadObjectRequest(expected.Bucket, expected.Key)).ConfigureAwait(false));
        ValidateCosHead(first, expected);
        var acl = ParseCosAcl((await Run(new GetObjectACLRequest(expected.Bucket, expected.Key)).ConfigureAwait(false)).RawContentBodyString);
        var tags = ParseCosTags((await Run(new COSXML.Model.Object.GetObjectTaggingRequest(expected.Bucket, expected.Key)).ConfigureAwait(false)).RawContentBodyString);
        var lockRequest = new CosObjectLockRequest(expected.Bucket);
        lockRequest.SetQueryParameter("object-lock", null);
        try
        {
            await Run(lockRequest).ConfigureAwait(false);
            throw new NotSupportedException("COS Bucket 配置了 Object Lock，不能无损原地编辑 Metadata。");
        }
        catch (CosServerException e) when (e.statusCode == 404 && e.errorCode == "NoSuchObjectLockConfiguration") { }
        var latestAcl = ParseCosAcl((await Run(new GetObjectACLRequest(expected.Bucket, expected.Key)).ConfigureAwait(false)).RawContentBodyString);
        var latestTags = ParseCosTags((await Run(new COSXML.Model.Object.GetObjectTaggingRequest(expected.Bucket, expected.Key)).ConfigureAwait(false)).RawContentBodyString);
        if (!SameCosMap(acl, latestAcl) || tags != latestTags)
            throw new InvalidOperationException("COS ACL / Tags 已改变；请刷新对象后重试。");
        await CheckSnapshot().ConfigureAwait(false);
        var latest = CosHeadHeaders(await Run(new HeadObjectRequest(expected.Bucket, expected.Key)).ConfigureAwait(false));
        if (!SameCosMap(first, latest))
            throw new InvalidOperationException("COS 对象属性已改变；请刷新对象后重试。");
        var copy = BuildCosMetadataCopy(region, expected, normalized, first, acl, tags);
        token.ThrowIfCancellationRequested();
        try
        {
            var result = await Run(copy).ConfigureAwait(false);
            var xml = ReadCosXml(result.RawContentBodyString, "CopyObjectResult");
            if (string.IsNullOrEmpty(xml.Element("ETag")?.Value))
                throw new IOException("COS CopyObject 未返回可验证的结果。");
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("COS Metadata 保存结果未确认，可能已写入或发生冲突；请重新读取对象核对，不要直接重试。", e);
        }

        async Task CheckSnapshot()
        {
            var current = CreateObjectProperties(expected.Bucket, expected.Key,
                await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = expected.Bucket, Key = expected.Key }, token).ConfigureAwait(false));
            if (!ObjectMetadataRewritePolicy.Matches(expected, current))
                throw new InvalidOperationException("对象内容或属性已改变；请重新打开属性后编辑。");
        }
        Task<CosResult> Run(CosRequest request)
        {
            token.ThrowIfCancellationRequested();
            request.MaxRetry = 0;
            request.RetryKeepDefaultDomain = true;
            request.RetryUseBackupDomain = false;
            return execute(request, token);
        }
    }

    internal static Dictionary<string, string> CosHeadHeaders(CosResult result)
    {
        // Exclude transport/request bookkeeping, retain every object-bearing COS header.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in result.responseHeaders ?? [])
        {
            var name = pair.Key.ToLowerInvariant();
            if (!(name.StartsWith("x-cos-", StringComparison.Ordinal) && name is not
                    ("x-cos-request-id" or "x-cos-trace-id")) &&
                name is not ("etag" or "last-modified" or "content-length" or "content-type" or
                    "content-language" or "content-encoding" or "content-disposition" or "cache-control" or "expires")) continue;
            if (pair.Value.Count != 1) throw new NotSupportedException("COS 返回了重复属性 Header，不能无损保存。");
            headers.Add(name, pair.Value[0]);
        }
        return headers;
    }

    private static void ValidateCosHead(IReadOnlyDictionary<string, string> head, ObjectProperties expected)
    {
        if (head.GetValueOrDefault("etag") != expected.ETag ||
            head.GetValueOrDefault("x-cos-version-id") != expected.VersionId ||
            !long.TryParse(head.GetValueOrDefault("content-length"), out var size) || size != expected.Size)
            throw new InvalidOperationException("COS 原生对象快照与编辑快照不一致，请刷新后重试。");
        if (head.TryGetValue("x-cos-object-type", out var type) && !type.Equals("normal", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("COS 非普通对象不能通过自复制无损修改 Metadata。");
        if (head.Any(p => p.Key.StartsWith("x-cos-object-lock-", StringComparison.OrdinalIgnoreCase)) ||
            head.ContainsKey("x-cos-server-side-encryption-customer-algorithm") ||
            head.ContainsKey("x-cos-server-side-encryption-customer-key-md5") ||
            head.ContainsKey("x-cos-server-side-encryption-cos-kms-key-id") ||
            (head.TryGetValue("x-cos-server-side-encryption", out var sse) && sse != "AES256"))
            throw new NotSupportedException("COS 受保护对象、KMS 或 SSE-C 不能无损原地修改 Metadata。");
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "x-cos-version-id", "x-cos-storage-class", "x-cos-object-type", "x-cos-server-side-encryption",
          "x-cos-hash-crc64ecma", "x-cos-hash-sha1", "x-cos-hash-sha256", "x-cos-tagging-count" };
        if (head.Keys.Any(k => k.StartsWith("x-cos-", StringComparison.OrdinalIgnoreCase) &&
            !k.StartsWith("x-cos-meta-", StringComparison.OrdinalIgnoreCase) && !known.Contains(k)))
            throw new NotSupportedException("COS 对象含尚不能无损保留的扩展 Header，请使用原生控制台修改。");
        // ETag is a content token, not a metadata revision. Compare the native
        // editable header snapshot as well: S3-compatible HEAD can omit a native
        // field, and accepting that snapshot would erase it with Replaced.
        if (head.GetValueOrDefault("content-type") != expected.ContentType ||
            head.GetValueOrDefault("cache-control") != expected.CacheControl ||
            head.GetValueOrDefault("content-encoding") != expected.ContentEncoding ||
            head.GetValueOrDefault("content-disposition") != expected.ContentDisposition ||
            head.GetValueOrDefault("content-language") != expected.ContentLanguage ||
            (head.GetValueOrDefault("x-cos-storage-class") ?? "STANDARD") != (expected.StorageClass ?? "STANDARD") ||
            head.GetValueOrDefault("x-cos-server-side-encryption") != expected.ServerSideEncryptionMethod ||
            ParseCosDate(head.GetValueOrDefault("last-modified")) != expected.LastModified ||
            ParseCosDate(head.GetValueOrDefault("expires")) != expected.ExpiresUtc ||
            (expected.ExpiresHeader is not null && head.GetValueOrDefault("expires") != expected.ExpiresHeader) ||
            (head.GetValueOrDefault("x-cos-object-type") ?? "normal") != (expected.ProviderObjectType ?? "normal"))
            throw new InvalidOperationException("COS 原生 Header 与编辑快照不一致；请刷新后重试，不会覆盖遗漏或并发修改的属性。");
        var metadata = head.Where(p => p.Key.StartsWith("x-cos-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Key[11..], p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (!SameCosMap(metadata, expected.Metadata))
            throw new NotSupportedException("COS 编辑快照未包含完整原生 Metadata，已停止以避免丢失属性。");
    }

    private static DateTimeOffset? ParseCosDate(string? raw)
    {
        if (raw is null) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return date;
        throw new NotSupportedException("COS 日期 Header 无法无损解析，已停止 Metadata 编辑。");
    }

    internal static CosCopyRequest BuildCosMetadataCopy(string region, ObjectProperties source,
        ObjectWriteHeaders edits, IReadOnlyDictionary<string, string> native,
        IReadOnlyDictionary<string, string> acl, string tags)
    {
        ValidateCosObjectKey(source.Key);
        ValidateCosHead(native, source);
        var request = new CosCopyRequest(source.Bucket, source.Key) { MaxRetry = 0, RetryUseBackupDomain = false };
        var appid = source.Bucket[(source.Bucket.LastIndexOf('-') + 1)..];
        request.SetCopySource(new CopySourceStruct(appid, source.Bucket, region, source.Key));
        request.SetCopyMetaDataDirective(COSXML.Common.CosMetaDataDirective.Replaced);
        request.SetRequestHeader("x-cos-copy-source-If-Match", source.ETag);
        request.SetCosStorageClass(native.GetValueOrDefault("x-cos-storage-class") ?? "STANDARD");
        foreach (var pair in acl) request.SetRequestHeader(pair.Key, pair.Value);
        request.SetRequestHeader("x-cos-tagging-directive", "Replaced");
        request.SetRequestHeader("x-cos-tagging", tags);
        if (native.GetValueOrDefault("x-cos-server-side-encryption") == "AES256") request.SetCosServerSideEncryption();
        Add("Content-Type", edits.ContentType);
        Add("Cache-Control", edits.CacheControl);
        Add("Content-Encoding", edits.ContentEncoding);
        Add("Content-Disposition", edits.ContentDisposition);
        Add("Content-Language", native.GetValueOrDefault("content-language"));
        Add("Expires", edits.ExpiresUtc?.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        foreach (var pair in edits.Metadata ?? new Dictionary<string, string>()) Add("x-cos-meta-" + pair.Key, pair.Value);
        return request;
        void Add(string name, string? value) { if (value is not null) request.SetRequestHeader(name, value); }
    }

    private sealed class CosObjectLockRequest : BucketRequest
    {
        public CosObjectLockRequest(string bucket) : base(bucket) { Method = "GET"; }
    }

    private static void ValidateCosObjectKey(string key)
    {
        // The official SDK treats an initial slash as a URL delimiter and relies on
        // System.Uri for path handling. Do not operate on a different normalized key.
        if (key.StartsWith('/') || key.Contains('\\') || key.Split('/').Any(p => p is "." or ".."))
            throw new NotSupportedException("此 COS Key 含原生 SDK 会规范化的路径形式，不能保证同 Key 编辑。");
    }

    private static bool SameCosMap(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && p.Value == v);

    private static XElement ReadCosXml(string? raw, string root)
    {
        if (string.IsNullOrEmpty(raw))
            throw new NotSupportedException("COS 未返回完整 XML，不能验证或无损保留属性。");
        var xml = XElement.Parse(raw);
        if (xml.Name != root) throw new NotSupportedException("COS 返回了无法确认的 XML 格式。");
        return xml;
    }

    internal static string ParseCosTags(string? raw)
    {
        var root = ReadCosXml(raw, "Tagging");
        var set = root.Element("TagSet") ?? throw new NotSupportedException("COS Tags 响应不完整。");
        var tags = set.Elements().Select(t =>
        {
            if (t.Name != "Tag" || t.Element("Key") is not { } key || t.Element("Value") is not { } value || t.Elements().Count() != 2)
                throw new NotSupportedException("COS Tag 格式不能无损保留。");
            return (Key: key.Value, Value: value.Value);
        }).OrderBy(t => t.Key, StringComparer.Ordinal).ToArray();
        if (tags.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != tags.Length)
            throw new NotSupportedException("COS Tags 包含重复 Key。");
        return string.Join("&", tags.Select(t => Uri.EscapeDataString(t.Key) + "=" + Uri.EscapeDataString(t.Value)));
    }

    internal static Dictionary<string, string> ParseCosAcl(string? raw)
    {
        var root = ReadCosXml(raw, "AccessControlPolicy");
        if (string.IsNullOrEmpty(root.Element("Owner")?.Element("ID")?.Value))
            throw new NotSupportedException("COS ACL 缺少 Owner，不能确认完整授权。");
        var list = root.Element("AccessControlList") ?? throw new NotSupportedException("COS ACL 响应不完整。");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var grants = list.Elements().ToArray();
        if (grants.Length == 0)
        {
            result.Add("x-cos-acl", "default");
            return result;
        }
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            var grantee = grant.Element("Grantee");
            var permission = grant.Element("Permission")?.Value;
            var header = permission switch
            {
                "READ" => "x-cos-grant-read", "FULL_CONTROL" => "x-cos-grant-full-control",
                "READ_ACP" => "x-cos-grant-read-acp", "WRITE_ACP" => "x-cos-grant-write-acp",
                _ => throw new NotSupportedException("COS ACL 含不能用于对象 Copy 的授权类型。")
            };
            if (grant.Name != "Grant" || grantee is null) throw new NotSupportedException("COS ACL Grant 不完整。");
            var id = grantee.Element("ID")?.Value;
            var uri = grantee.Element("URI")?.Value;
            string target;
            if (id is not null && uri is null)
            {
                var match = Regex.Match(id, @"^qcs::cam::uin/[0-9]+:uin/([0-9]+)$", RegexOptions.CultureInvariant);
                if (!match.Success) throw new NotSupportedException("COS ACL 用户 ID 不能无损转换。");
                target = "id=\"" + match.Groups[1].Value + "\"";
            }
            else if (id is null && uri == "http://cam.qcloud.com/groups/global/AllUsers")
                target = "id=\"anyone\"";
            else throw new NotSupportedException("COS ACL 组授权不能无损转换。");
            if (!grouped.TryGetValue(header, out var values)) grouped.Add(header, values = []);
            values.Add(target);
        }
        foreach (var pair in grouped) result.Add(pair.Key, string.Join(",", pair.Value.Order(StringComparer.Ordinal)));
        return result;
    }
}
