namespace S3Explorer.Core;

/// <summary>
/// A clipboard owns this session; individual streams own independent read positions. Object bytes
/// are never exposed until the ordinary download pipeline has verified and committed a private file.
/// No credentials, remote keys or user-controlled names are written into cache paths.
/// </summary>
public sealed class WindowsClipboardDownloadSession : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<WindowsFileClipboardEntry> _entries;
    private readonly Func<WindowsFileClipboardEntry, string, TransferOperationContext, CancellationToken, Task> _download;
    private readonly TransferOperationContext _template;
    private readonly Action<int, TransferProgress>? _progress;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, Task<string>> _downloads = [];
    private readonly string _parent;
    private string? _directory;
    private bool _disposed;
    private Task _cleanup = Task.CompletedTask;

    public WindowsClipboardDownloadSession(
        IReadOnlyList<WindowsFileClipboardEntry> entries,
        Func<WindowsFileClipboardEntry, string, TransferOperationContext, CancellationToken, Task> download,
        TransferOperationContext template,
        Action<int, TransferProgress>? progress = null,
        string? cacheParent = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(template);
        _entries = entries.ToArray();
        _download = download;
        _template = template;
        _progress = progress;
        _parent = Path.GetFullPath(cacheParent ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "S3Explorer", "ClipboardCache"));
    }

    /// <summary>Called by a background Shell stream on its first Read, never by descriptor discovery.</summary>
    public Stream OpenRead(int index)
    {
        Task<string> download;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((uint)index >= (uint)_entries.Count || _entries[index].IsDirectory)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (!_downloads.TryGetValue(index, out download!))
            {
                // Task.Run also handles storage implementations that synchronously do work before
                // returning their first awaitable. It must never inherit an STA synchronization context.
                download = Task.Run(() => DownloadAsync(index), _stop.Token);
                _downloads.Add(index, download);
            }
        }
        var path = download.GetAwaiter().GetResult();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LocalObjectPath.EnsureNoReparsePoints(_parent, path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                128 * 1024, FileOptions.SequentialScan);
        }
    }

    private async Task<string> DownloadAsync(int index)
    {
        var entry = _entries[index];
        var token = _stop.Token;
        token.ThrowIfCancellationRequested();
        string target;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_directory is null)
            {
                var root = Path.GetPathRoot(_parent)!;
                LocalObjectPath.EnsureNoReparsePoints(root, _parent);
                Directory.CreateDirectory(_parent);
                _directory = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                LocalObjectPath.EnsureNoReparsePoints(root, _directory);
            }
            target = Path.Combine(_directory, $"{index:D8}.bin");
        }
        var identitySeen = false;
        var transfer = _template with
        {
            Options = _template.Options with
            {
                MaximumDownloadBytes = Math.Max(1, entry.Size),
                AllowDestinationOverwrite = false,
                RetainFailedTransferState = false
            },
            DownloadCheckpoint = null,
            MultipartCheckpoint = null,
            ReportProgress = progress => _progress?.Invoke(index, progress),
            UpdateCheckpointAsync = (_, checkpoint, _, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (checkpoint is null || checkpoint.RemoteLength != entry.Size ||
                    (!string.IsNullOrWhiteSpace(entry.ETag) && checkpoint.ETag != entry.ETag) ||
                    (!string.IsNullOrWhiteSpace(entry.VersionId) && checkpoint.VersionId != entry.VersionId))
                    throw new IOException("远端对象在复制后发生变化；请重新复制，未向资源管理器提供文件内容。");
                identitySeen = true;
                return Task.CompletedTask;
            }
        };
        try
        {
            await _download(entry, target, transfer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!identitySeen || !File.Exists(target) || new FileInfo(target).Length != entry.Size)
                throw new IOException("下载没有完成身份或长度校验；未向资源管理器提供文件内容。");
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            var pending = _downloads.Values.ToArray();
            // Cancellation callbacks and filesystem cleanup must not run on the UI thread.
            _cleanup = Task.Run(() => CleanupAsync(pending));
        }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task cleanup;
        lock (_sync) cleanup = _cleanup;
        await cleanup.ConfigureAwait(false);
    }

    private async Task CleanupAsync(Task[] pending)
    {
        try { await _stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* A faulty subscriber must not suppress owned-cache cleanup. */ }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception) { /* The stream caller receives the original transfer error. */ }
        if (_directory is { } directory)
        {
            try
            {
                LocalObjectPath.EnsureNoReparsePoints(_parent, directory);
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { /* A crashed/locked cache is never advertised as a source file. */ }
            catch (UnauthorizedAccessException) { }
        }
        _stop.Dispose();
    }
}
