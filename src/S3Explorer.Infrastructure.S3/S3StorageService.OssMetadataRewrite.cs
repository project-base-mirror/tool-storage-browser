using System.Globalization;
using Aliyun.OSS;
using S3Explorer.Core;
using OssMetadata = Aliyun.OSS.ObjectMetadata;

namespace S3Explorer.Infrastructure.S3;

public sealed partial class S3StorageService
{
    // Native OSS preserves the four-state object ACL (including inherited/default),
    // which cannot be reconstructed losslessly from the S3 compatibility ACL response.
    internal static async Task RewriteOssMetadataAsync(ConnectionProfile profile, ObjectProperties expected,
        ObjectWriteHeaders headers, Amazon.Runtime.ImmutableCredentials credentials, CancellationToken cancellationToken)
    {
        var endpoint = GetOssNativeEndpoint(profile);
        ValidateOssNativeKey(expected.Key);
        cancellationToken.ThrowIfCancellationRequested();
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(profile.RequestTimeoutSeconds) };
        // Do not construct OssClient: the pinned SDK transport modifies global TLS
        // validation. Only pure SDK request/metadata model classes are used below.
        var client = new OssNativeMetadataTransport(http, endpoint.Endpoint, endpoint.Region, credentials, cancellationToken);
        await Task.Run(() => RewriteOssMetadata(client, expected, headers, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    internal interface IOssMetadataClient
    {
        VersioningStatus Versioning(string bucket);
        void RequireNoBucketWorm(string bucket);
        OssMetadata Head(string bucket, string key);
        CannedAccessControlList Acl(string bucket, string key);
        void Copy(CopyObjectRequest request);
    }

    internal static void RewriteOssMetadata(IOssMetadataClient client, ObjectProperties expected,
        ObjectWriteHeaders headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOssNativeKey(expected.Key);
        if (headers.Tags is { Count: > 0 }) throw new ArgumentException("OSS Metadata 编辑由服务端复制 Tags，不能同时替换 Tags。");
        var normalized = headers.ValidateForMetadataCopy();
        if (string.IsNullOrEmpty(normalized.ContentType))
            throw new NotSupportedException("OSS 原地复制需要明确 Content-Type，避免缺失值被服务端替换为默认类型。");
        // ObjectWorm requires Enabled versioning and cannot later disable it. Off,
        // not Suspended, excludes ObjectWorm on this narrow native metadata-only path.
        RequireOssUnversioned(client.Versioning(expected.Bucket));
        client.RequireNoBucketWorm(expected.Bucket);
        cancellationToken.ThrowIfCancellationRequested();
        var first = client.Head(expected.Bucket, expected.Key);
        ValidateOssSource(first);
        if (!OssMatchesExpected(first, expected))
            throw new InvalidOperationException("OSS 当前内容或 Header / Metadata 与打开时不同；请刷新后重试。");
        var acl = client.Acl(expected.Bucket, expected.Key);
        var request = BuildOssMetadataRequest(expected, first, acl, normalized);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOssUnversioned(client.Versioning(expected.Bucket));
        client.RequireNoBucketWorm(expected.Bucket);
        var latest = client.Head(expected.Bucket, expected.Key);
        ValidateOssSource(latest);
        if (!OssSnapshotsMatch(first, latest) || client.Acl(expected.Bucket, expected.Key) != acl)
            throw new InvalidOperationException("OSS 保存前检测到内容、属性或 ACL 冲突，已停止。");
        cancellationToken.ThrowIfCancellationRequested();
        try { client.Copy(request); }
        catch (Exception exception)
        {
            // Copy may have committed before a transport failure. Never retry or use
            // ModifyObjectMeta/CopyBigObject, which can silently choose multipart copy.
            throw new InvalidOperationException("OSS Metadata 保存未确认；可能已写入或被条件/保留策略拒绝。请重新读取核对，不要直接重试。", exception);
        }
    }

    internal static (string Endpoint, string Region) GetOssNativeEndpoint(ConnectionProfile profile)
    {
        var uri = profile.NormalizedEndpoint;
        var host = uri.Host;
        if (host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase)) host = host[3..];
        if (uri.Scheme != "https" || !uri.IsDefaultPort || !host.StartsWith("oss-", StringComparison.OrdinalIgnoreCase) ||
            !host.EndsWith(".aliyuncs.com", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(profile.CustomHostHeader) || profile.IgnoreCertificateErrors)
            throw new NotSupportedException("OSS 原生 Metadata 编辑需要官方区域 HTTPS Endpoint；暂不支持 CNAME、自定义 Host、代理路径或忽略 TLS 校验。");
        var region = host[4..^".aliyuncs.com".Length];
        if (region.EndsWith("-internal", StringComparison.OrdinalIgnoreCase)) region = region[..^9];
        if (region is "accelerate" or "accelerate-overseas" || !region.Contains('-'))
            throw new NotSupportedException("OSS 原生 Metadata 编辑需要明确区域 Endpoint，不使用全局加速域名。");
        var configuredRegion = profile.EffectiveSignatureRegion;
        // Existing OSS presets use the S3 spelling oss-cn-hangzhou; native OSS4
        // uses cn-hangzhou for the same region, not an unrelated default region.
        if (configuredRegion.StartsWith("oss-", StringComparison.OrdinalIgnoreCase)) configuredRegion = configuredRegion[4..];
        if (!string.Equals(configuredRegion, region, StringComparison.Ordinal))
            throw new NotSupportedException("OSS Endpoint 区域与连接的签名 Region 不一致；请核对连接配置后重试。");
        return ($"https://{host}", region);
    }

    internal static void ValidateOssNativeKey(string key)
    {
        // Reject path spellings outside the native adapter's verified key contract.
        // Ordinary repeated slashes, spaces and Unicode use exact escaped paths.
        if (string.IsNullOrEmpty(key) || key.StartsWith('/') || key.Contains('\\') ||
            key.Split('/').Any(part => part is "." or "..") || key.Any(char.IsControl))
            throw new NotSupportedException("此特殊 Key 尚不能通过 OSS 原生路径保证精确身份，未执行 Metadata 写入。");
    }

    private static void RequireOssUnversioned(VersioningStatus status)
    {
        if (status != VersioningStatus.Off)
            throw new NotSupportedException("OSS 原生 Metadata 编辑目前要求从未启用版本控制的普通 Bucket；Enabled/Suspended 与 ObjectWorm 不走此路径。");
    }

    private static string? OssHeader(OssMetadata metadata, string name) =>
        metadata.HttpMetadata.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();

    private static void ValidateOssSource(OssMetadata source)
    {
        if (source.ContentLength is < 0 or > ObjectMetadataCopyPolicy.MaximumBytes || string.IsNullOrEmpty(source.ETag))
            throw new NotSupportedException("OSS 原生 Metadata 编辑需要 ETag，且对象不超过 5 GiB。");
        if (!string.Equals(source.ObjectType, "Normal", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("OSS 原生 Metadata 编辑只支持 Normal 对象；Appendable、Symlink 或未知对象类型不能无损保存。");
        if (!string.IsNullOrEmpty(source.VersionId) && source.VersionId != "null")
            throw new NotSupportedException("OSS 对象已出现固定版本，停止无版本 Metadata 编辑。");
        _ = ObjectMetadataValidator.ValidatePreservingValues(new Dictionary<string, string>(source.UserMetadata));
        if (int.TryParse(OssHeader(source, "x-oss-missing-meta"), out var missing) && missing > 0)
            throw new NotSupportedException("OSS HEAD 未返回完整 Metadata，停止保存。");
        if (!string.IsNullOrEmpty(source.ServerSideEncryption) && source.ServerSideEncryption != "AES256" ||
            source.HttpMetadata.Any(pair => pair.Key.Contains("worm", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("customer", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("key-id", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("OSS 对象含 KMS、客户密钥或 WORM 属性，当前路径不能无损保留。");
        if (NormalizeOssStorageClass(OssHeader(source, "x-oss-storage-class")) is null)
            throw new NotSupportedException("OSS HEAD 未明确返回存储类型，停止以避免改写为默认类型。");
    }

    internal static CopyObjectRequest BuildOssMetadataRequest(ObjectProperties expected, OssMetadata source,
        CannedAccessControlList acl, ObjectWriteHeaders headers)
    {
        var metadata = new OssMetadata();
        void Add(string name, string? value) { if (value is not null) metadata.AddHeader(name, value); }
        Add("Content-Type", headers.ContentType);
        Add("Cache-Control", headers.CacheControl);
        Add("Content-Encoding", headers.ContentEncoding);
        Add("Content-Disposition", headers.ContentDisposition);
        Add("Expires", headers.ExpiresUtc?.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        Add("Content-Language", OssHeader(source, "Content-Language"));
        Add("x-oss-website-redirect-location", OssHeader(source, "x-oss-website-redirect-location"));
        Add("x-oss-storage-class", OssHeader(source, "x-oss-storage-class"));
        Add("x-oss-server-side-encryption", source.ServerSideEncryption);
        Add("x-oss-object-acl", acl switch
        {
            CannedAccessControlList.Default => "default", CannedAccessControlList.Private => "private",
            CannedAccessControlList.PublicRead => "public-read", CannedAccessControlList.PublicReadWrite => "public-read-write",
            _ => throw new NotSupportedException("OSS ACL 返回未知模式，未写入。")
        });
        Add("x-oss-tagging-directive", "COPY");
        foreach (var pair in headers.Metadata ?? new Dictionary<string, string>()) metadata.UserMetadata.Add(pair.Key, pair.Value);
        var request = new CopyObjectRequest(expected.Bucket, expected.Key, expected.Bucket, expected.Key) { NewObjectMetadata = metadata };
        request.MatchingETagConstraints.Add(source.ETag);
        return request;
    }

    private static string? NormalizeOssStorageClass(string? value) => value switch
    {
        "Standard" or "STANDARD" => "Standard",
        "IA" or "STANDARD_IA" => "IA",
        "Archive" => "Archive",
        "ColdArchive" => "ColdArchive",
        "DeepColdArchive" => "DeepColdArchive",
        _ => null
    };

    private static string? OssNullable(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static string? OssVersion(string? value) => value == "null" ? null : OssNullable(value);

    internal static bool OssMatchesExpected(OssMetadata source, ObjectProperties expected) =>
        source.ETag.Trim('"') == expected.ETag?.Trim('"') && source.ContentLength == expected.Size &&
        (expected.LastModified is null || source.LastModified.ToUniversalTime() == expected.LastModified.Value.UtcDateTime) &&
        source.ContentType == expected.ContentType && source.CacheControl == expected.CacheControl &&
        source.ContentEncoding == expected.ContentEncoding && source.ContentDisposition == expected.ContentDisposition &&
        NormalizeOssStorageClass(OssHeader(source, "x-oss-storage-class")) is { } storage &&
        storage == NormalizeOssStorageClass(expected.StorageClass) &&
        OssNullable(source.ServerSideEncryption) == OssNullable(expected.ServerSideEncryptionMethod) &&
        OssNullable(OssHeader(source, "x-oss-server-side-encryption-key-id")) == OssNullable(expected.EncryptionKeyId) &&
        OssNullable(OssHeader(source, "x-oss-server-side-encryption-customer-algorithm")) == OssNullable(expected.CustomerEncryptionMethod) &&
        OssVersion(source.VersionId) == OssVersion(expected.VersionId) &&
        OssHeader(source, "x-oss-website-redirect-location") == expected.WebsiteRedirectLocation &&
        string.Equals(source.ObjectType, expected.ProviderObjectType, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(expected.ObjectLockMode) && string.IsNullOrEmpty(expected.ProviderRetentionUntil) &&
        expected.ObjectLockRetainUntilDate is null &&
        (string.IsNullOrEmpty(expected.ObjectLockLegalHoldStatus) || expected.ObjectLockLegalHoldStatus == "OFF") &&
        expected.MissingMetadataCount is null or 0 && expected.BucketKeyEnabled is not true &&
        OssHeader(source, "Content-Language") == expected.ContentLanguage &&
        ExpiresEqual(OssHeader(source, "Expires"), expected.ExpiresUtc) &&
        source.UserMetadata.Count == expected.Metadata.Count &&
        source.UserMetadata.All(pair => expected.Metadata.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool ExpiresEqual(string? raw, DateTimeOffset? expected) => string.IsNullOrEmpty(raw) ? expected is null :
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) && value == expected;

    private static bool OssSnapshotsMatch(OssMetadata left, OssMetadata right)
    {
        string[] fields = ["ETag", "Last-Modified", "Content-Length", "Content-Type", "Cache-Control", "Content-Encoding",
            "Content-Disposition", "Content-Language", "Expires", "x-oss-storage-class", "x-oss-server-side-encryption",
            "x-oss-version-id", "x-oss-object-type", "x-oss-website-redirect-location"];
        return fields.All(name => OssHeader(left, name) == OssHeader(right, name)) &&
            left.UserMetadata.Count == right.UserMetadata.Count &&
            left.UserMetadata.All(pair => right.UserMetadata.TryGetValue(pair.Key, out var value) && pair.Value == value);
    }
}
