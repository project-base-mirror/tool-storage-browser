using System.Text;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class FolderSyncExecutionDialog : Form
{
    private readonly PersistentTransferQueue _queue;
    private readonly FolderSyncJob _job;
    private readonly Guid _executionId;
    private readonly ListView _items = new()
    {
        Name = "SyncExecutionItems",
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
    };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button _retry = new() { Text = "重试失败项", AutoSize = true, MinimumSize = new Size(110, 32) };
    private readonly Button _exportJson = new() { Text = "导出 JSON", AutoSize = true, MinimumSize = new Size(100, 32) };
    private readonly Button _exportCsv = new() { Text = "导出 CSV", AutoSize = true, MinimumSize = new Size(100, 32) };
    private readonly Button _close = new() { Text = "关闭", AutoSize = true, MinimumSize = new Size(88, 32), DialogResult = DialogResult.Cancel };
    private FolderSyncExecutionReport? _report;

    public FolderSyncExecutionDialog(PersistentTransferQueue queue, FolderSyncJob job, Guid executionId)
    {
        _queue = queue;
        _job = job;
        _executionId = executionId;
        Text = $"同步执行结果 - {job.Name}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 480);
        ClientSize = new Size(1040, 620);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _items.Columns.Add("相对路径", 300);
        _items.Columns.Add("方向", 80);
        _items.Columns.Add("状态", 100);
        _items.Columns.Add("总大小", 100, HorizontalAlignment.Right);
        _items.Columns.Add("已传输", 100, HorizontalAlignment.Right);
        _items.Columns.Add("可重试", 70);
        _items.Columns.Add("错误", 300);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(10),
            ColumnCount = 2
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_summary, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.AddRange([_close, _exportCsv, _exportJson, _retry]);
        footer.Controls.Add(buttons, 1, 0);

        Controls.Add(_items);
        Controls.Add(footer);
        CancelButton = _close;

        _retry.Click += async (_, _) => await RetryFailuresAsync();
        _exportJson.Click += (_, _) => Export("JSON 文件 (*.json)|*.json", "json", FolderSyncReportProjector.ExportJson);
        _exportCsv.Click += (_, _) => Export("CSV 文件 (*.csv)|*.csv", "csv", FolderSyncReportProjector.ExportCsv);
        _queue.Changed += QueueChanged;
        Shown += (_, _) => RefreshData();
        FormClosed += (_, _) => _queue.Changed -= QueueChanged;
        UpdateButtons();
    }

    private void QueueChanged(object? sender, TransferQueueChangedEventArgs args)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke(RefreshData);
    }

    private void RefreshData()
    {
        if (IsDisposed) return;
        try
        {
            _report = FolderSyncReportProjector.Project(_job, _executionId, _queue.Snapshot);
        }
        catch (InvalidOperationException)
        {
            _report = null;
        }

        _items.BeginUpdate();
        try
        {
            _items.Items.Clear();
            foreach (var row in _report?.Items ?? [])
            {
                var item = new ListViewItem(row.RelativePath);
                item.SubItems.Add(DirectionText(row.Direction));
                item.SubItems.Add(StateText(row.State));
                item.SubItems.Add(FileSizeFormatter.Format(row.TotalBytes));
                item.SubItems.Add(FileSizeFormatter.Format(row.TransferredBytes));
                item.SubItems.Add(row.Retryable ? "是" : "否");
                item.SubItems.Add(row.Error ?? string.Empty);
                if (row.State is TransferTaskState.Failed or TransferTaskState.CleanupPending)
                    item.ForeColor = Color.DarkRed;
                _items.Items.Add(item);
            }
        }
        finally
        {
            _items.EndUpdate();
        }

        if (_report is null)
            _summary.Text = "执行记录已不存在。";
        else
            _summary.Text = $"{(_report.IsFinished ? "已结束" : "执行中")} · 总计 {_report.TotalFiles:N0} · " +
                $"成功 {_report.CompletedFiles:N0} · 失败 {_report.FailedFiles:N0} · " +
                $"取消 {_report.CancelledFiles:N0} · 进行中 {_report.ActiveFiles:N0}";
        UpdateButtons();
    }

    private async Task RetryFailuresAsync()
    {
        if (_report is null) return;
        var batchIds = _report.Items
            .Where(item => item.Retryable && item.State is TransferTaskState.Failed or TransferTaskState.CleanupPending)
            .Select(item => item.BatchId)
            .Distinct()
            .ToArray();
        try
        {
            var retried = 0;
            foreach (var batchId in batchIds)
                retried += await _queue.RetryBatchFailuresAsync(batchId);
            MessageBox.Show(this, $"已重新排队 {retried:N0} 个可重试失败项。", "同步重试",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "同步重试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Export(string filter, string extension, Func<FolderSyncExecutionReport, string> serialize)
    {
        if (_report is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = $"{filter}|所有文件 (*.*)|*.*",
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"s3explorer-sync-{SafeFileName(_job.Name)}-{_executionId:N}.{extension}",
            Title = "导出同步执行报告"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, serialize(_report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            MessageBox.Show(this, "同步执行报告已导出。错误信息中的凭据和预签名查询参数已脱敏。", "导出完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateButtons()
    {
        _retry.Enabled = _report?.Items.Any(item =>
            item.Retryable && item.State is TransferTaskState.Failed or TransferTaskState.CleanupPending) == true;
        _exportJson.Enabled = _exportCsv.Enabled = _report is not null;
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static string DirectionText(TransferDirection direction) => direction switch
    {
        TransferDirection.Upload => "上传",
        TransferDirection.Download => "下载",
        TransferDirection.DeleteRemote => "删除远端",
        TransferDirection.DeleteLocal => "删除本地",
        _ => direction.ToString()
    };

    private static string StateText(TransferTaskState state) => state switch
    {
        TransferTaskState.Queued => "等待中",
        TransferTaskState.Running => "进行中",
        TransferTaskState.Paused => "已暂停",
        TransferTaskState.RetryPending => "等待重试",
        TransferTaskState.Interrupted => "已中断",
        TransferTaskState.Completed => "成功",
        TransferTaskState.Failed => "失败",
        TransferTaskState.Cancelled => "已取消",
        TransferTaskState.CleanupPending => "待清理",
        _ => state.ToString()
    };
}
