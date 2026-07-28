using System.Text;
using S3Explorer.Core;

namespace S3Explorer.Cli;

internal sealed class CliCancellationScope : IDisposable
{
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenSource _monitorStop = new();
    private readonly Task? _monitor;

    private CliCancellationScope(CancellationToken parent, TimeSpan? timeout, string? cancelFile)
    {
        _linked = CancellationTokenSource.CreateLinkedTokenSource(parent);
        if (timeout is not null) _linked.CancelAfter(timeout.Value);
        if (!string.IsNullOrWhiteSpace(cancelFile))
            _monitor = MonitorCancelFileAsync(Path.GetFullPath(cancelFile), _linked, _monitorStop.Token);
    }

    public CancellationToken Token => _linked.Token;

    public static CliCancellationScope Create(CliArguments args, CancellationToken parent)
    {
        TimeSpan? timeout = null;
        if (args.Optional("timeout") is { Length: > 0 } value)
        {
            if (!int.TryParse(value, out var seconds) || seconds is < 1 or > 86400)
                throw new CliUsageException("--timeout 必须是 1–86400 秒的整数。");
            timeout = TimeSpan.FromSeconds(seconds);
        }
        return new CliCancellationScope(parent, timeout, args.Optional("cancel-file"));
    }

    public void Dispose()
    {
        _monitorStop.Cancel();
        try { _monitor?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _monitorStop.Dispose();
        _linked.Dispose();
    }

    private static async Task MonitorCancelFileAsync(
        string path,
        CancellationTokenSource target,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !target.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                target.Cancel();
                return;
            }
            await Task.Delay(250, cancellationToken);
        }
    }
}

internal sealed class CliFileLog : IDisposable
{
    private readonly StreamWriter? _writer;

    private CliFileLog(StreamWriter? writer) => _writer = writer;

    public static CliFileLog Create(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new CliFileLog(null);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        return new CliFileLog(new StreamWriter(
            new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true });
    }

    public void Write(string message)
    {
        if (_writer is null) return;
        var safe = SensitiveDataRedactor.Redact(message.Replace('\r', ' ').Replace('\n', ' '));
        _writer.WriteLine($"{DateTimeOffset.UtcNow:O}\t{safe}");
    }

    public void Dispose() => _writer?.Dispose();
}
