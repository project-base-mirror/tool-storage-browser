using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class SimpleFileLogger
{
    private const int DefaultRetentionDays = 30;
    private const long DefaultMaximumFileBytes = 10L * 1024 * 1024;
    private const int MaximumRotatedFilesPerDay = 5;
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly long _maximumFileBytes;
    private readonly Func<DateTimeOffset> _clock;
    private DateOnly? _lastCleanupDate;

    public SimpleFileLogger(
        string? directory = null,
        int retentionDays = DefaultRetentionDays,
        long maximumFileBytes = DefaultMaximumFileBytes,
        Func<DateTimeOffset>? clock = null)
    {
        if (retentionDays < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (maximumFileBytes < 256) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "S3Explorer",
            "logs");
        _retentionDays = retentionDays;
        _maximumFileBytes = maximumFileBytes;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string CurrentLogPath => Path.Combine(_directory, $"s3explorer-{_clock():yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");

    private void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var safe = SensitiveDataRedactor.Redact(message);
            lock (_sync)
            {
                var now = _clock();
                CleanupExpiredLogs(now);
                var entry = $"{now:O} [{level}] {safe}{Environment.NewLine}";
                var path = Path.Combine(_directory, $"s3explorer-{now:yyyy-MM-dd}.log");
                RotateIfNeeded(path, System.Text.Encoding.UTF8.GetByteCount(entry));
                File.AppendAllText(path, entry);
            }
        }
        catch
        {
            // Logging must never terminate the UI.
        }
    }

    private void CleanupExpiredLogs(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (_lastCleanupDate == today) return;
        _lastCleanupDate = today;
        var cutoff = now.UtcDateTime.AddDays(-_retentionDays);
        foreach (var path in Directory.EnumerateFiles(_directory, "s3explorer-*.log*"))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
                File.Delete(path);
        }
    }

    private void RotateIfNeeded(string path, int incomingBytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= _maximumFileBytes)
            return;

        var oldest = path + $".{MaximumRotatedFilesPerDay}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = MaximumRotatedFilesPerDay - 1; index >= 1; index--)
        {
            var source = path + $".{index}";
            if (File.Exists(source)) File.Move(source, path + $".{index + 1}", overwrite: true);
        }
        File.Move(path, path + ".1", overwrite: true);
    }
}
