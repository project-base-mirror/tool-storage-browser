using System.Net;
using Amazon.S3;

namespace S3Explorer.Infrastructure.S3;

public static class S3CompatibilityPolicy
{
    private const long MinimumCopyPartBytes = 64L * 1024 * 1024;
    private const long MaximumCopyPartBytes = 5L * 1024 * 1024 * 1024;
    private const long AlignmentBytes = 8L * 1024 * 1024;

    public static bool IsRestrictedListBuckets(AmazonS3Exception exception) =>
        exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ||
        IsCode(exception, "AccessDenied", "AllAccessDisabled", "UnauthorizedOperation");

    public static bool ShouldFallbackToSingleDelete(AmazonS3Exception exception) =>
        exception.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented or HttpStatusCode.Forbidden ||
        IsCode(exception,
            "AccessDenied",
            "InvalidRequest",
            "MalformedXML",
            "MethodNotAllowed",
            "NotImplemented",
            "XNotImplemented");

    public static bool RequiresMultipartCopy(AmazonS3Exception exception) =>
        exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented ||
        IsCode(exception, "EntityTooLarge", "InvalidRequest", "NotImplemented", "XNotImplemented");

    public static long CalculateCopyPartSize(long objectSize)
    {
        if (objectSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(objectSize));

        var required = (objectSize + 9_999) / 10_000;
        var aligned = ((required + AlignmentBytes - 1) / AlignmentBytes) * AlignmentBytes;
        return Math.Clamp(Math.Max(MinimumCopyPartBytes, aligned), MinimumCopyPartBytes, MaximumCopyPartBytes);
    }

    private static bool IsCode(AmazonS3Exception exception, params string[] codes) =>
        codes.Any(code => string.Equals(exception.ErrorCode, code, StringComparison.OrdinalIgnoreCase));
}
