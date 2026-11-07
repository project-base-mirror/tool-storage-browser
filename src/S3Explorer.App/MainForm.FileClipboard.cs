using System.Runtime.InteropServices;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    private ShellFileDataObject? _shellClipboard;
    private readonly List<WeakReference<ShellFileDataObject>> _clipboardSessions = [];
    private readonly List<WeakReference<WindowsClipboardDownloadSession>> _clipboardDownloads = [];
    private CancellationTokenSource? _clipboardPreparation;
    private long _clipboardRevision;

    private async Task CopySelectionToObjectClipboardAsync(bool move)
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries().ToArray();
        if (selected.Length == 0) return;
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var prefix = _currentPrefix;
        _clipboardPreparation?.Cancel();
        using var preparation = new CancellationTokenSource();
        _clipboardPreparation = preparation;
        var revision = ++_clipboardRevision;
        var token = preparation.Token;
        var payload = new ObjectClipboardPayload(profile.Id, profile.Name, bucket,
            selected.Select(entry => new ObjectClipboardEntry(entry.Key, entry.Name, entry.IsDirectory, entry.Size)).ToArray(), move);
        var marker = new ShellFileDataObject([], _ => throw new NotSupportedException(), Guid.NewGuid().ToString("N"));
        try
        {
            await ShellClipboardPublisher.SetAsync(marker, token);
            if (revision != _clipboardRevision || token.IsCancellationRequested)
            {
                await ShellClipboardPublisher.ClearAsync(marker);
                marker.Dispose();
                return;
            }
            _shellClipboard = marker;
            TrackClipboardSession(marker);
            _objectClipboard = payload;
            UpdateCommandStates();
            if (move)
            {
                _requestStatus.Text = $"已剪切 {selected.Length:N0} 项，仅支持当前连接内粘贴；复制可粘贴到本地";
                return;
            }

            _requestStatus.Text = "已复制对象，正在准备资源管理器文件清单（编辑菜单可取消）...";
            var storage = _storage;
            var manifest = await Task.Run(() => FreezeClipboardManifestAsync(storage, profile, bucket, prefix, selected, token), token);
            token.ThrowIfCancellationRequested();
            if (revision != _clipboardRevision || !ShellClipboardPublisher.IsCurrent(marker)) return;
            var weakForm = new WeakReference<MainForm>(this);
            var logger = _logger;
            long lastReport = 0;
            var session = new WindowsClipboardDownloadSession(manifest,
                async (entry, destination, transfer, cancellation) =>
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(entry.VersionId))
                            await storage.DownloadFileAsync(profile, bucket, entry.Key, destination, transfer, cancellation).ConfigureAwait(false);
                        else
                            await storage.DownloadObjectVersionAsync(profile, bucket, entry.Key, entry.VersionId, destination, transfer, cancellation).ConfigureAwait(false);
                        PostClipboardStatus(weakForm, revision, $"剪贴板文件已下载并校验：{entry.RelativePath}");
                    }
                    catch (Exception exception)
                    {
                        logger.Error("Clipboard download failed", exception);
                        PostClipboardStatus(weakForm, revision, "剪贴板下载失败或已取消；请重新复制后重试");
                        throw;
                    }
                }, _transferRuntime.CreateClipboardContext(), (index, progress) =>
                {
                    var now = Environment.TickCount64;
                    if (now - Interlocked.Read(ref lastReport) < 250) return;
                    Interlocked.Exchange(ref lastReport, now);
                    PostClipboardStatus(weakForm, revision,
                        $"正在准备本地粘贴：{manifest[index].RelativePath} {progress.Percentage:F0}%（编辑菜单可取消）");
                });
            _clipboardDownloads.RemoveAll(reference => !reference.TryGetTarget(out _));
            _clipboardDownloads.Add(new WeakReference<WindowsClipboardDownloadSession>(session));
            var data = new ShellFileDataObject(manifest.Select(entry => new ShellClipboardFile(
                entry.RelativePath, entry.IsDirectory, entry.Size, entry.LastModified)).ToArray(),
                session.OpenRead, Guid.NewGuid().ToString("N"), session.Dispose);
            try
            {
                await ShellClipboardPublisher.SetAsync(data, token, marker);
                if (revision != _clipboardRevision || token.IsCancellationRequested)
                {
                    await ShellClipboardPublisher.ClearAsync(data);
                    data.Dispose();
                    return;
                }
                _shellClipboard = data;
                _objectClipboard = payload;
                TrackClipboardSession(data);
                _requestStatus.Text = $"已复制 {selected.Length:N0} 项，可粘贴到其他连接或资源管理器；请保持程序运行";
            }
            catch { data.Dispose(); throw; }
        }
        catch (OperationCanceledException)
        {
            if (revision == _clipboardRevision && !IsDisposed)
                _requestStatus.Text = "资源管理器复制准备已取消";
        }
        catch (Exception exception)
        {
            _logger.Error("Clipboard preparation failed", exception);
            if (revision == _clipboardRevision && !IsDisposed)
                ErrorDialog.ShowException(this, "无法准备资源管理器粘贴", "复制文件",
                    exception, "对象间复制仍可用（如果剪贴板未被替换）。特殊对象名可使用下载功能。");
        }
        finally
        {
            if (ReferenceEquals(_clipboardPreparation, preparation)) _clipboardPreparation = null;
            if (!IsDisposed) UpdateCommandStates();
        }
    }

    internal static async Task<IReadOnlyList<WindowsFileClipboardEntry>> FreezeClipboardManifestAsync(
        IS3StorageService storage, ConnectionProfile profile, string bucket, string prefix,
        IReadOnlyCollection<S3ObjectEntry> selection, CancellationToken cancellationToken)
    {
        var manifest = (await WindowsFileClipboardManifest.BuildAsync(prefix, selection,
            // The ordinary UI listing hides the prefix's own marker. Lossless export must see
            // raw objects, including a trailing-slash object that has nonzero content.
            (path, continuation, token) => storage.ListObjectsForDeletionAsync(profile, bucket, path, continuation, 1000, token),
            cancellationToken, flatListing: true).ConfigureAwait(false)).ToArray();
        await Parallel.ForEachAsync(Enumerable.Range(0, manifest.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (index, token) =>
            {
                var entry = manifest[index];
                if (entry.IsDirectory) return;
                var properties = await storage.GetObjectPropertiesAsync(profile, bucket, entry.Key, token).ConfigureAwait(false);
                var version = S3ProviderCapabilityRegistry.For(profile.ServiceType).Object.VersionOperations.Supported &&
                    !string.IsNullOrWhiteSpace(properties.VersionId) && properties.VersionId != "null" ? properties.VersionId : null;
                if (properties.Size < 0 || (string.IsNullOrWhiteSpace(properties.ETag) && version is null))
                    throw new IOException("服务端未提供可验证的对象身份，不能安全地准备文件剪贴板。");
                manifest[index] = entry with
                {
                    Size = properties.Size, LastModified = properties.LastModified,
                    ETag = properties.ETag, VersionId = version
                };
            }).ConfigureAwait(false);
        return Array.AsReadOnly(manifest);
    }

    private void TrackClipboardSession(ShellFileDataObject data)
    {
        _clipboardSessions.RemoveAll(reference => !reference.TryGetTarget(out _));
        _clipboardSessions.Add(new WeakReference<ShellFileDataObject>(data));
    }

    private static void PostClipboardStatus(WeakReference<MainForm> weakForm, long revision, string text)
    {
        if (!weakForm.TryGetTarget(out var form) || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            form.BeginInvoke(() =>
            {
                if (!form.IsDisposed && form._clipboardRevision == revision) form._requestStatus.Text = text;
            });
        }
        catch (InvalidOperationException) { /* Closing the window wins over a late progress callback. */ }
    }

    private bool OwnsObjectClipboard()
    {
        if (_shellClipboard is null) return false;
        if (ShellClipboardPublisher.IsCurrent(_shellClipboard)) return true;
        // Clipboard replacement releases only our root, never a live Explorer stream. Its COM
        // reference retains the old owner until copying finishes; finalization then cleans cache.
        _shellClipboard = null;
        _objectClipboard = null;
        return false;
    }

    private bool CanPasteFiles()
    {
        if (_currentBucket is null || _currentProfile is null) return false;
        if (OwnsObjectClipboard()) return EnsureClipboardProfile(false);
        try { return Clipboard.ContainsFileDropList(); }
        catch (ExternalException) { return false; }
    }

    private async Task CancelFileClipboardAsync()
    {
        _clipboardPreparation?.Cancel();
        var revision = ++_clipboardRevision;
        var current = _shellClipboard;
        var sessions = _clipboardSessions.ToArray();
        // Detach before yielding. A newer copy during ClearAsync owns different state and must
        // not be canceled or erased by this operation's eventual continuation.
        _clipboardSessions.Clear();
        _shellClipboard = null;
        _objectClipboard = null;
        foreach (var reference in sessions)
            if (reference.TryGetTarget(out var data)) data.Dispose();
        if (current is not null) await ClearClipboardSafelyAsync(current);
        if (!IsDisposed && _clipboardRevision == revision)
        {
            _requestStatus.Text = "文件剪贴板已取消，正在清理临时下载";
            UpdateCommandStates();
        }
    }

    private void DisposeFileClipboard()
    {
        _clipboardPreparation?.Cancel();
        ++_clipboardRevision;
        if (_shellClipboard is { } current) _ = ClearClipboardSafelyAsync(current);
        foreach (var reference in _clipboardSessions)
            if (reference.TryGetTarget(out var data)) data.Dispose();
        _clipboardSessions.Clear();
        _shellClipboard = null;
    }

    private async Task ShutdownFileClipboardAsync()
    {
        var downloads = _clipboardDownloads.Select(reference =>
            reference.TryGetTarget(out var session) ? session : null).OfType<WindowsClipboardDownloadSession>().ToArray();
        DisposeFileClipboard();
        await Task.WhenAll(downloads.Select(session => session.DisposeAsync().AsTask()));
        _clipboardDownloads.Clear();
    }

    private async Task ClearClipboardSafelyAsync(ShellFileDataObject data)
    {
        try { await ShellClipboardPublisher.ClearAsync(data); }
        catch (ExternalException exception) { _logger.Error("Could not clear expired file clipboard", exception); }
    }
}
