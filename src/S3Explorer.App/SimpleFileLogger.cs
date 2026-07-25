using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class SimpleFileLogger
{
    private readonly object _sync = new();
    private readonly string _directory;

    public SimpleFileLogger(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "S3Explorer",
            "logs");
    }

    public string CurrentLogPath => Path.Combine(_directory, $"s3explorer-{DateTime.Now:yyyy-MM-dd}.log");

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
                File.AppendAllText(CurrentLogPath, $"{DateTimeOffset.Now:O} [{level}] {safe}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never terminate the UI.
        }
    }
}
