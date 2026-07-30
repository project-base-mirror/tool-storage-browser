using System.Text.RegularExpressions;

namespace S3Explorer.Core;

public static class FileSizeFormatter
{
    private static readonly string[] IecUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
            return "-" + Format(-bytes);
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < IecUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.##} {IecUnits[unit]}";
    }
}

public static partial class SensitiveDataRedactor
{
    [GeneratedRegex(@"(?i)(secret(?:access)?key|sessiontoken|authorization|access[_-]?token|refresh[_-]?token|id[_-]?token|web[_-]?identity[_-]?token|external[_-]?id|client[_-]?secret|device[_-]?code|user[_-]?code)\s*[:=]\s*([^\s,;]+)")]
    private static partial Regex SensitivePattern();

    [GeneratedRegex(@"(?i)(X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token)=([^&\s]+)")]
    private static partial Regex QueryPattern();

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var result = SensitivePattern().Replace(value, "$1=***");
        return QueryPattern().Replace(result, "$1=***");
    }
}

public static class RetryClassifier
{
    private static readonly HashSet<string> RetryableCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SlowDown", "RequestTimeout", "InternalError", "ServiceUnavailable", "Throttling"
    };

    public static bool ShouldRetry(string? errorCode, int? statusCode, Exception? exception = null)
    {
        if (exception is OperationCanceledException)
            return false;
        if (exception is IOException or TimeoutException)
            return true;
        if (statusCode is 408 or 429 or >= 500)
            return true;
        return errorCode is not null && RetryableCodes.Contains(errorCode);
    }
}

public static class ObjectTypeDetector
{
    public static string Detect(string name, bool isDirectory)
    {
        if (isDirectory)
            return "Folder";
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".zip" or ".7z" or ".rar" => "ZIP File",
            ".json" => "JSON File",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => "Image",
            ".exe" or ".msi" => "Executable",
            ".txt" or ".log" or ".md" => "Text File",
            _ => "Unknown"
        };
    }
}
