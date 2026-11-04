using Amazon.Runtime;
using Amazon.S3.Model;
using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

// The MinIO HTTP API normalizes leading '/' keys before validation and even
// rewrites DeleteObjects XML keys. Sending an exact signed URI cannot fix that
// server behavior. Unknown/custom services have no verified contrary contract;
// known non-MinIO providers keep their original key behavior.
internal static class MinioObjectKeyPolicy
{
    internal static bool AppliesTo(S3ServiceType serviceType) =>
        serviceType is S3ServiceType.MinIO or S3ServiceType.Custom || !Enum.IsDefined(serviceType);

    internal static void Validate(string? key) => Validate(S3ServiceType.MinIO, key);

    internal static void Validate(S3ServiceType serviceType, string? key)
    {
        if (!AppliesTo(serviceType) || key is null) return; // Let the SDK perform ordinary required-field validation.
        var invalidComponent = key.Split(new[] { '/', '\\' }).Any(component => component.Trim(' ', '\t', '\r', '\n', '\v', '\f') is "." or "..");
        if (key.StartsWith('/') || key.Contains("//", StringComparison.Ordinal) || invalidComponent)
        {
            var reason = serviceType == S3ServiceType.MinIO
                ? "MinIO 无法无损处理以 / 开头、包含 // 或含 . / .. 路径段的对象 Key，服务端可能将其改写为其他对象。已在发送请求前拒绝操作；不会自动规范化或访问同名别名。"
                : "此自定义服务尚未验证以 / 开头、包含 // 或含 . / .. 路径段的对象 Key 能否无损访问。为避免读写或删除其他对象，已在发送请求前拒绝操作；不会自动规范化 Key。";
            throw new TransferExecutionException(new TransferFailureInfo(reason, TransferFailureCategory.Validation, Retryable: false));
        }
    }

    internal static void ValidateRequest(AmazonWebServiceRequest request, S3ServiceType serviceType = S3ServiceType.MinIO)
    {
        if (!AppliesTo(serviceType)) return;
        switch (request)
        {
            case CopyObjectRequest copy:
                Validate(serviceType, copy.SourceKey);
                Validate(serviceType, copy.DestinationKey);
                return;
            case CopyPartRequest copyPart:
                Validate(serviceType, copyPart.SourceKey);
                Validate(serviceType, copyPart.DestinationKey);
                return;
            case DeleteObjectsRequest delete:
                foreach (var item in delete.Objects ?? []) Validate(serviceType, item.Key);
                return;
        }

        // Prefixes on list requests deliberately remain untouched: discovery is
        // distinct from addressing one object for reading or mutation.
        var key = request switch
        {
            GetObjectRequest value => value.Key,
            GetObjectMetadataRequest value => value.Key,
            GetObjectAttributesRequest value => value.Key,
            PutObjectRequest value => value.Key,
            DeleteObjectRequest value => value.Key,
            GetObjectAclRequest value => value.Key,
            PutObjectAclRequest value => value.Key,
            GetObjectTaggingRequest value => value.Key,
            PutObjectTaggingRequest value => value.Key,
            DeleteObjectTaggingRequest value => value.Key,
            GetObjectRetentionRequest value => value.Key,
            PutObjectRetentionRequest value => value.Key,
            GetObjectLegalHoldRequest value => value.Key,
            PutObjectLegalHoldRequest value => value.Key,
            InitiateMultipartUploadRequest value => value.Key,
            UploadPartRequest value => value.Key,
            CompleteMultipartUploadRequest value => value.Key,
            AbortMultipartUploadRequest value => value.Key,
            ListPartsRequest value => value.Key,
            RestoreObjectRequest value => value.Key,
            SelectObjectContentRequest value => value.Key,
            GetPreSignedUrlRequest value => value.Key,
            _ => null
        };
        Validate(serviceType, key);
    }
}
