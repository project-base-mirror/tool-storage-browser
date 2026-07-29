using System.Text.Json;

namespace S3Explorer.Core;

public sealed record BucketFeatureSupport(bool Supported, string Reason)
{
    public static BucketFeatureSupport Yes(string reason = "支持") => new(true, reason);
    public static BucketFeatureSupport No(string reason) => new(false, reason);
}

public sealed record BucketCapabilities(
    BucketFeatureSupport Policy,
    BucketFeatureSupport Acl,
    BucketFeatureSupport PublicAccessBlock,
    BucketFeatureSupport ObjectOwnership,
    BucketFeatureSupport Cors,
    BucketFeatureSupport Versioning,
    BucketFeatureSupport Encryption,
    BucketFeatureSupport KmsEncryption,
    BucketFeatureSupport Tagging,
    BucketFeatureSupport EmptyBucket);

public static class BucketCapabilityMatrix
{
    public static BucketCapabilities For(S3ServiceType serviceType) => serviceType switch
    {
        S3ServiceType.AmazonS3 => new(
            BucketFeatureSupport.Yes(), BucketFeatureSupport.Yes(),
            BucketFeatureSupport.Yes(), BucketFeatureSupport.Yes(),
            BucketFeatureSupport.Yes(), BucketFeatureSupport.Yes(),
            BucketFeatureSupport.Yes(), BucketFeatureSupport.Yes(),
            BucketFeatureSupport.Yes(),
            BucketFeatureSupport.Yes()),
        S3ServiceType.MinIO => new(
            BucketFeatureSupport.Yes("MinIO 支持 Bucket Policy"),
            BucketFeatureSupport.Yes("MinIO 支持 S3 ACL；服务端策略可能限制公开 ACL"),
            BucketFeatureSupport.No("MinIO 不提供 AWS Public Access Block API"),
            BucketFeatureSupport.No("MinIO 不提供 AWS Object Ownership API"),
            BucketFeatureSupport.No("MinIO Community 不支持 Bucket 级 CORS；请使用服务端全局 CORS，AIStor 用户可改用自定义兼容类型"),
            BucketFeatureSupport.Yes("MinIO 支持 S3 Versioning API"),
            BucketFeatureSupport.Yes("MinIO 支持 Bucket 默认加密；服务端必须已配置密钥管理"),
            BucketFeatureSupport.Yes("MinIO 支持 SSE-KMS；保存前请确认服务端 KMS 与 Key 已就绪"),
            BucketFeatureSupport.Yes("MinIO 支持 S3 Bucket Tagging API"),
            BucketFeatureSupport.Yes()),
        _ => new(
            BucketFeatureSupport.No("该兼容服务的 Bucket Policy API 尚未验证"),
            BucketFeatureSupport.No("该兼容服务的 ACL API 尚未验证"),
            BucketFeatureSupport.No("仅 AWS S3 支持此控制项"),
            BucketFeatureSupport.No("仅 AWS S3 支持此控制项"),
            BucketFeatureSupport.No("该兼容服务的 CORS API 尚未验证"),
            BucketFeatureSupport.No("该兼容服务的版本 API 尚未验证"),
            BucketFeatureSupport.No("该兼容服务的加密配置 API 尚未验证"),
            BucketFeatureSupport.No("该兼容服务的 SSE-KMS 配置 API 尚未验证"),
            BucketFeatureSupport.No("该兼容服务的 Tagging API 尚未验证"),
            BucketFeatureSupport.Yes("普通对象与未完成分片可安全清理；版本能力需扫描确认"))
    };
}

public enum BucketAclMode
{
    Private,
    PublicRead
}

public sealed record BucketAclGrant(string Grantee, string Permission);

public sealed record BucketAclSnapshot(
    string Owner,
    BucketAclMode Mode,
    IReadOnlyList<BucketAclGrant> Grants)
{
    public string Summary => Grants.Count == 0
        ? $"{Mode}（无显式 Grant）"
        : $"{Mode}，{Grants.Count:N0} 条 Grant";
}

public sealed record BucketPublicAccessBlockSnapshot(
    bool BlockPublicAcls,
    bool IgnorePublicAcls,
    bool BlockPublicPolicy,
    bool RestrictPublicBuckets)
{
    public bool FullyBlocked =>
        BlockPublicAcls && IgnorePublicAcls && BlockPublicPolicy && RestrictPublicBuckets;
}

public enum BucketObjectOwnershipMode
{
    BucketOwnerEnforced,
    BucketOwnerPreferred,
    ObjectWriter
}

public sealed record BucketPropertiesSnapshot(
    string Bucket,
    string Endpoint,
    S3ServiceType ServiceType,
    string Region,
    string VersioningStatus,
    string EncryptionSummary,
    bool HasPolicy,
    BucketAclSnapshot Acl,
    BucketPublicAccessBlockSnapshot? PublicAccessBlock,
    BucketObjectOwnershipMode? ObjectOwnership,
    BucketCapabilities Capabilities);

public sealed record BucketEmptySummary(
    long ObjectCount,
    long VersionCount,
    long DeleteMarkerCount,
    long MultipartUploadCount,
    long TotalBytes,
    bool VersionListingSupported)
{
    public long TotalRemoteEntries => ObjectCount + VersionCount + DeleteMarkerCount + MultipartUploadCount;
    public bool IsEmpty => TotalRemoteEntries == 0;
}

public sealed record BucketEmptyResult(
    long DeletedObjects,
    long DeletedVersions,
    long DeletedDeleteMarkers,
    long AbortedMultipartUploads);

public static class BucketPolicyDocument
{
    public static string ValidateAndNormalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Bucket Policy 不能为空。", nameof(json));
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Bucket Policy 必须是 JSON 对象。", nameof(json));
            if (!document.RootElement.TryGetProperty("Statement", out var statements) ||
                statements.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                throw new ArgumentException("Bucket Policy 必须包含 Statement 对象或数组。", nameof(json));
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Bucket Policy JSON 无效：{exception.Message}", nameof(json), exception);
        }
    }

    public static bool AreSemanticallyEquivalent(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(ValidateAndNormalize(left));
        using var rightDocument = JsonDocument.Parse(ValidateAndNormalize(right));
        return ElementsEquivalent(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static bool ElementsEquivalent(
        JsonElement left, JsonElement right, string? propertyName = null)
    {
        if (string.Equals(propertyName, "Principal", StringComparison.Ordinal) &&
            IsWildcardPrincipal(left) && IsWildcardPrincipal(right))
            return true;

        if (left.ValueKind != right.ValueKind)
        {
            if (left.ValueKind == JsonValueKind.Array)
            {
                var items = left.EnumerateArray().ToArray();
                return items.Length == 1 && ElementsEquivalent(items[0], right, propertyName);
            }
            if (right.ValueKind == JsonValueKind.Array)
            {
                var items = right.EnumerateArray().ToArray();
                return items.Length == 1 && ElementsEquivalent(left, items[0], propertyName);
            }
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject()
                    .Where(property => !string.Equals(property.Name, "Sid", StringComparison.Ordinal))
                    .ToArray();
                var rightProperties = right.EnumerateObject()
                    .Where(property => !string.Equals(property.Name, "Sid", StringComparison.Ordinal))
                    .ToArray();
                if (leftProperties.Length != rightProperties.Length)
                    return false;
                foreach (var property in leftProperties)
                {
                    if (!right.TryGetProperty(property.Name, out var rightValue) ||
                        !ElementsEquivalent(property.Value, rightValue, property.Name))
                        return false;
                }
                return true;

            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                if (leftItems.Length != rightItems.Length)
                    return false;
                var matched = new bool[rightItems.Length];
                foreach (var leftItem in leftItems)
                {
                    var matchingIndex = -1;
                    for (var index = 0; index < rightItems.Length; index++)
                    {
                        if (!matched[index] &&
                            ElementsEquivalent(leftItem, rightItems[index], propertyName))
                        {
                            matchingIndex = index;
                            break;
                        }
                    }
                    if (matchingIndex < 0)
                        return false;
                    matched[matchingIndex] = true;
                }
                return true;

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static bool IsWildcardPrincipal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return string.Equals(element.GetString(), "*", StringComparison.Ordinal);
        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            return items.Length == 1 && IsWildcardPrincipal(items[0]);
        }
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var properties = element.EnumerateObject().ToArray();
        return properties.Length == 1 &&
            string.Equals(properties[0].Name, "AWS", StringComparison.Ordinal) &&
            IsWildcardPrincipal(properties[0].Value);
    }
}
