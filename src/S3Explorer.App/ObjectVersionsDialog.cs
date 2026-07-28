using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class ObjectVersionsDialog : Form
{
    private readonly IS3StorageService _storage;
    private readonly ConnectionProfile _profile;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly TransferQueueControl _transfers;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ListView _versions = new()
    {
        Name = "ObjectVersionsList",
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        GridLines = true
    };
    private readonly Label _location = new()
    {
        Dock = DockStyle.Top, Height = 42, Padding = new Padding(8, 12, 8, 4), AutoEllipsis = true
    };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(4, 9, 8, 0) };
    private readonly Button _reload = Button("ReloadObjectVersionsButton", "重新读取");
    private readonly Button _next = Button("NextObjectVersionsPageButton", "下一页");
    private readonly Button _download = Button("DownloadObjectVersionButton", "下载版本");
    private readonly Button _restore = Button("RestoreObjectVersionButton", "恢复为当前版本");
    private readonly Button _delete = Button("DeleteObjectVersionButton", "永久删除版本");
    private readonly Button _cleanMarkers = Button("CleanDeleteMarkersButton", "清理 Delete Marker");
    private readonly Button _close = Button("CloseObjectVersionsButton", "关闭");
    private string? _nextKeyMarker;
    private string? _nextVersionMarker;
    private int _pageNumber;
    private bool _busy;

    public ObjectVersionsDialog(
        IS3StorageService storage,
        ConnectionProfile profile,
        string bucket,
        string prefix,
        TransferQueueControl transfers)
    {
        _storage = storage;
        _profile = profile;
        _bucket = bucket;
        _prefix = prefix;
        _transfers = transfers;
        Text = $"对象版本 - {bucket}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 620);
        MinimumSize = new Size(1040, 480);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _versions.Columns.Add("对象 Key", 330);
        _versions.Columns.Add("类型", 105);
        _versions.Columns.Add("Version ID", 230);
        _versions.Columns.Add("当前", 70);
        _versions.Columns.Add("大小", 100, HorizontalAlignment.Right);
        _versions.Columns.Add("修改时间", 155);
        _versions.Columns.Add("存储类型", 105);
        _location.Text = $"s3://{profile.Name}/{bucket}/{prefix}";

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.AddRange([
            _close, _delete, _restore, _download, _cleanMarkers, _next, _reload, _status
        ]);
        Controls.Add(_versions);
        Controls.Add(_location);
        Controls.Add(actions);
        CancelButton = _close;

        Shown += async (_, _) => await LoadPageAsync(reset: true);
        FormClosed += (_, _) => _cancellation.Cancel();
        _versions.SelectedIndexChanged += (_, _) => UpdateActions();
        _reload.Click += async (_, _) => await LoadPageAsync(reset: true);
        _next.Click += async (_, _) => await LoadPageAsync(reset: false);
        _download.Click += async (_, _) => await DownloadSelectedAsync();
        _restore.Click += async (_, _) => await RestoreSelectedAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _cleanMarkers.Click += async (_, _) => await CleanDeleteMarkersAsync();
        _close.Click += (_, _) => Close();
        UpdateActions();
    }

    public bool RemoteChanged { get; private set; }

    private ObjectVersionEntry? SelectedVersion =>
        _versions.SelectedItems.Count == 1
            ? _versions.SelectedItems[0].Tag as ObjectVersionEntry
            : null;

    private async Task LoadPageAsync(bool reset)
    {
        if (_busy) return;
        if (reset)
        {
            _nextKeyMarker = null;
            _nextVersionMarker = null;
            _pageNumber = 0;
        }
        await ExecuteAsync("读取对象版本", async token =>
        {
            var page = await _storage.ListObjectVersionsAsync(
                _profile, _bucket, _prefix,
                reset ? null : _nextKeyMarker,
                reset ? null : _nextVersionMarker,
                500, token);
            _versions.BeginUpdate();
            try
            {
                _versions.Items.Clear();
                foreach (var version in page.Items)
                {
                    var item = new ListViewItem(version.Key) { Tag = version };
                    item.SubItems.Add(version.IsDeleteMarker ? "Delete Marker" : "对象版本");
                    item.SubItems.Add(version.VersionId);
                    item.SubItems.Add(version.IsLatest ? "是" : "否");
                    item.SubItems.Add(version.IsDeleteMarker ? "—" : FileSizeFormatter.Format(version.Size));
                    item.SubItems.Add(version.LastModified?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "—");
                    item.SubItems.Add(version.IsDeleteMarker ? "—" : version.StorageClass);
                    if (version.IsDeleteMarker) item.ForeColor = Color.DarkOrange;
                    _versions.Items.Add(item);
                }
            }
            finally { _versions.EndUpdate(); }
            _pageNumber = reset ? 1 : _pageNumber + 1;
            _nextKeyMarker = page.NextKeyMarker;
            _nextVersionMarker = page.NextVersionIdMarker;
            _next.Enabled = page.HasMore;
            _status.Text = $"第 {_pageNumber:N0} 页，{page.Items.Count:N0} 项";
        });
    }

    private async Task DownloadSelectedAsync()
    {
        var version = SelectedVersion;
        if (version is null || version.IsDeleteMarker) return;
        using var save = new SaveFileDialog
        {
            FileName = S3Path.DisplayName(version.Key, false),
            Title = $"下载版本 {version.VersionId}",
            OverwritePrompt = true
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        await _transfers.EnqueueDownloadAsync(
            _profile, _bucket, version.Key,
            LocalObjectPath.ToExtendedLengthPath(save.FileName), version.Size, version.VersionId);
        _status.Text = $"已加入下载队列：{version.VersionId}";
    }

    private async Task RestoreSelectedAsync()
    {
        var version = SelectedVersion;
        if (version is null || version.IsDeleteMarker || version.IsLatest) return;
        if (MessageBox.Show(this,
                $"将历史版本复制为同一对象的新当前版本：\r\n\r\nKey：{version.Key}\r\nVersion ID：{version.VersionId}\r\n\r\n原历史版本不会被删除。",
                "恢复历史版本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await ExecuteAsync("恢复历史版本", async token =>
        {
            await _storage.RestoreObjectVersionAsync(
                _profile, _bucket, version.Key, version.VersionId, token);
            RemoteChanged = true;
        });
        if (!_busy) await LoadPageAsync(reset: true);
    }

    private async Task DeleteSelectedAsync()
    {
        var version = SelectedVersion;
        if (version is null) return;
        var kind = version.IsDeleteMarker ? "Delete Marker" : "对象版本";
        if (MessageBox.Show(this,
                $"即将永久删除{kind}，此操作不可撤销：\r\n\r\nKey：{version.Key}\r\nVersion ID：{version.VersionId}",
                "确认永久删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (MessageBox.Show(this,
                "这是最后一次确认。确定永久删除这个 Version ID 吗？",
                "再次确认", MessageBoxButtons.YesNo, MessageBoxIcon.Stop,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await ExecuteAsync("永久删除对象版本", async token =>
        {
            await _storage.DeleteObjectVersionAsync(
                _profile, _bucket, version.Key, version.VersionId, token);
            RemoteChanged = true;
        });
        if (!_busy) await LoadPageAsync(reset: true);
    }

    private async Task CleanDeleteMarkersAsync()
    {
        if (MessageBox.Show(this,
                $"将扫描并永久删除当前范围内的全部 Delete Marker：\r\n\r\ns3://{_profile.Name}/{_bucket}/{_prefix}\r\n\r\n删除 Marker 可能让旧版本重新变为可见对象。是否继续？",
                "清理 Delete Marker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await ExecuteAsync("清理 Delete Marker", async token =>
        {
            var markers = new List<ObjectVersionIdentity>();
            string? keyMarker = null;
            string? versionMarker = null;
            bool hasMore;
            do
            {
                var page = await _storage.ListObjectVersionsAsync(
                    _profile, _bucket, _prefix, keyMarker, versionMarker, 1000, token);
                markers.AddRange(page.Items.Where(item => item.IsDeleteMarker)
                    .Select(item => new ObjectVersionIdentity(item.Key, item.VersionId)));
                hasMore = page.HasMore;
                keyMarker = hasMore ? page.NextKeyMarker : null;
                versionMarker = hasMore ? page.NextVersionIdMarker : null;
            } while (hasMore);
            if (markers.Count == 0)
            {
                _status.Text = "当前范围没有 Delete Marker";
                return;
            }
            await _storage.DeleteObjectVersionsAsync(_profile, _bucket, markers, token);
            _status.Text = $"已删除 {markers.Count:N0} 个 Delete Marker";
            RemoteChanged = true;
        });
        if (!_busy) await LoadPageAsync(reset: true);
    }

    private async Task ExecuteAsync(string operation, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        UpdateActions();
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, $"{operation}失败", operation, exception,
                $"s3://{_profile.Name}/{_bucket}/{_prefix}");
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            UpdateActions();
        }
    }

    private void UpdateActions()
    {
        var selected = SelectedVersion;
        _reload.Enabled = !_busy;
        _next.Enabled = !_busy && _nextKeyMarker is not null;
        _download.Enabled = !_busy && selected is { IsDeleteMarker: false };
        _restore.Enabled = !_busy && selected is { IsDeleteMarker: false, IsLatest: false };
        _delete.Enabled = !_busy && selected is not null;
        _cleanMarkers.Enabled = !_busy;
        _close.Enabled = !_busy;
    }

    private static Button Button(string name, string text) => new()
    {
        Name = name,
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(110, 32),
        Margin = new Padding(4)
    };
}
