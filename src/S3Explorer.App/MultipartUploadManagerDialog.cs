using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class MultipartUploadManagerDialog : Form
{
    private readonly ConnectionProfile _profile;
    private readonly string _bucket;
    private readonly IS3StorageService _storage;
    private readonly PersistentTransferQueue _queue;
    private readonly SimpleFileLogger _logger;
    private readonly TextBox _prefix = new() { Width = 260, PlaceholderText = "Key 前缀（可选）" };
    private readonly DateTimePicker _initiatedBefore = new()
    {
        Width = 175,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm",
        ShowCheckBox = true,
        Checked = false
    };
    private readonly ListView _uploads = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        CheckBoxes = true,
        GridLines = true
    };
    private readonly Label _summary = new() { AutoSize = true, Text = "尚未扫描" };
    private readonly Button _refresh = new() { Text = "扫描", Size = new Size(92, 32) };
    private readonly Button _selectAll = new() { Text = "全选", Size = new Size(92, 32) };
    private readonly Button _cleanup = new() { Text = "清理所选", Size = new Size(104, 32) };
    private readonly Button _close = new() { Text = "关闭", Size = new Size(92, 32), DialogResult = DialogResult.Cancel };

    public MultipartUploadManagerDialog(
        ConnectionProfile profile,
        string bucket,
        IS3StorageService storage,
        PersistentTransferQueue queue,
        SimpleFileLogger logger)
    {
        _profile = profile;
        _bucket = bucket;
        _storage = storage;
        _queue = queue;
        _logger = logger;

        Text = $"未完成的分片上传 - {profile.Name}/{bucket}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 480);
        ClientSize = new Size(980, 590);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _uploads.Columns.Add("对象 Key", 310);
        _uploads.Columns.Add("Upload ID", 280);
        _uploads.Columns.Add("创建时间", 150);
        _uploads.Columns.Add("分片", 70);
        _uploads.Columns.Add("已知大小", 100);

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), WrapContents = true
        };
        filters.Controls.Add(new Label { Text = "Key 前缀：", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        filters.Controls.Add(_prefix);
        filters.Controls.Add(new Label { Text = "创建时间不晚于：", AutoSize = true, Margin = new Padding(12, 8, 3, 3) });
        filters.Controls.Add(_initiatedBefore);
        filters.Controls.Add(_refresh);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10), ColumnCount = 2
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_summary, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        buttons.Controls.AddRange([_close, _cleanup, _selectAll]);
        footer.Controls.Add(buttons, 1, 0);

        Controls.Add(_uploads);
        Controls.Add(filters);
        Controls.Add(footer);
        CancelButton = _close;

        _refresh.Click += async (_, _) => await RefreshAsync();
        _selectAll.Click += (_, _) =>
        {
            var check = _uploads.CheckedItems.Count != _uploads.Items.Count;
            foreach (ListViewItem item in _uploads.Items) item.Checked = check;
        };
        _cleanup.Click += async (_, _) => await CleanupSelectedAsync();
        Shown += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        SetBusy(true, "正在扫描未完成的分片上传...");
        try
        {
            DateTimeOffset? before = _initiatedBefore.Checked
                ? new DateTimeOffset(_initiatedBefore.Value.ToUniversalTime())
                : null;
            var uploads = await _storage.ListIncompleteMultipartUploadsAsync(
                _profile, _bucket, _prefix.Text, before, CancellationToken.None);
            Populate(uploads);
            _logger.Info($"Multipart scan profile={_profile.Name} bucket={_bucket} count={uploads.Count}");
        }
        catch (Exception exception)
        {
            _logger.Error($"Multipart scan failed profile={_profile.Name} bucket={_bucket}", exception);
            ErrorDialog.ShowException(this, "扫描失败", "列出未完成的分片上传", exception, $"s3://{_bucket}");
        }
        finally
        {
            SetBusy(false, _summary.Text);
        }
    }

    private async Task CleanupSelectedAsync()
    {
        var selected = _uploads.CheckedItems.Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<IncompleteMultipartUpload>()
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请先勾选要清理的上传。", "分片上传清理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var knownBytes = selected.Sum(item => item.KnownUploadedBytes);
        var answer = MessageBox.Show(
            this,
            $"将中止 {selected.Length:N0} 个未完成上传，已知分片大小共 {FormatBytes(knownBytes)}。\n\n" +
            "此操作只处理中选的 Upload ID，不会删除已完成对象。是否继续？",
            "确认清理未完成上传",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        SetBusy(true, "正在清理所选上传...");
        try
        {
            var result = await _storage.CleanupMultipartUploadsAsync(_profile, selected, CancellationToken.None);
            foreach (var upload in selected.Where(upload => !result.FailedUploads.Any(failed => SameUpload(failed, upload))))
            {
                await _queue.MarkMultipartCleanedAsync(
                    _profile.Id, upload.Bucket, upload.ObjectKey, upload.UploadId);
            }

            _logger.Info($"Multipart cleanup profile={_profile.Name} bucket={_bucket} requested={result.RequestedCount} cleaned={result.CleanedCount} failed={result.FailedUploads.Count}");
            if (result.FailedUploads.Count > 0)
            {
                MessageBox.Show(
                    this,
                    $"已清理 {result.CleanedCount:N0} 个，失败 {result.FailedUploads.Count:N0} 个。失败项仍保留在列表中，可稍后重试。",
                    "分片上传清理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger.Error($"Multipart cleanup failed profile={_profile.Name} bucket={_bucket}", exception);
            ErrorDialog.ShowException(this, "清理失败", "中止未完成的分片上传", exception, $"s3://{_bucket}");
        }
        finally
        {
            SetBusy(false, _summary.Text);
        }
    }

    private void Populate(IReadOnlyList<IncompleteMultipartUpload> uploads)
    {
        _uploads.BeginUpdate();
        try
        {
            _uploads.Items.Clear();
            foreach (var upload in uploads)
            {
                var item = new ListViewItem(upload.ObjectKey) { Tag = upload };
                item.SubItems.Add(upload.UploadId);
                item.SubItems.Add(upload.InitiatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(upload.PartCount.ToString("N0"));
                item.SubItems.Add(FormatBytes(upload.KnownUploadedBytes));
                _uploads.Items.Add(item);
            }
        }
        finally
        {
            _uploads.EndUpdate();
        }
        _summary.Text = $"共 {uploads.Count:N0} 个未完成上传，已知分片大小 {FormatBytes(uploads.Sum(item => item.KnownUploadedBytes))}";
    }

    private void SetBusy(bool busy, string text)
    {
        UseWaitCursor = busy;
        _refresh.Enabled = !busy;
        _selectAll.Enabled = !busy;
        _cleanup.Enabled = !busy;
        _summary.Text = text;
    }

    private static bool SameUpload(IncompleteMultipartUpload left, IncompleteMultipartUpload right) =>
        string.Equals(left.Bucket, right.Bucket, StringComparison.Ordinal) &&
        string.Equals(left.ObjectKey, right.ObjectKey, StringComparison.Ordinal) &&
        string.Equals(left.UploadId, right.UploadId, StringComparison.Ordinal);

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = Math.Max(0, (double)value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:N1} {units[unit]}";
    }
}
