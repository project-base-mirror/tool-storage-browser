using System.Text;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class BatchFailureDialog : Form
{
    private readonly PersistentTransferQueue _queue;
    private readonly Guid _batchId;
    private readonly ListView _failures = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = true,
        GridLines = true,
        VirtualMode = true
    };
    private readonly Label _summary = new() { AutoSize = true, Text = "正在读取批次..." };
    private readonly Button _retrySelected = new() { Text = "重试所选", Size = new Size(104, 32) };
    private readonly Button _retryAll = new() { Text = "重试全部可重试项", Size = new Size(144, 32) };
    private readonly Button _export = new() { Text = "导出失败清单", Size = new Size(120, 32) };
    private readonly Button _close = new() { Text = "关闭", Size = new Size(92, 32), DialogResult = DialogResult.Cancel };
    private TransferFailureDetail[] _rows = [];
    private TransferBatchRecord? _batch;

    public BatchFailureDialog(PersistentTransferQueue queue, Guid batchId)
    {
        _queue = queue;
        _batchId = batchId;
        Text = "批次失败明细";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 460);
        ClientSize = new Size(1040, 620);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _failures.Columns.Add("相对路径", 300);
        _failures.Columns.Add("分类", 120);
        _failures.Columns.Add("HTTP", 70);
        _failures.Columns.Add("服务错误码", 130);
        _failures.Columns.Add("可重试", 80);
        _failures.Columns.Add("消息", 300);
        _failures.RetrieveVirtualItem += RetrieveVirtualItem;
        _failures.SelectedIndexChanged += (_, _) => UpdateButtons();

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
        buttons.Controls.AddRange([_close, _export, _retryAll, _retrySelected]);
        footer.Controls.Add(buttons, 1, 0);

        Controls.Add(_failures);
        Controls.Add(footer);
        CancelButton = _close;

        _retrySelected.Click += async (_, _) => await RetrySelectedAsync();
        _retryAll.Click += async (_, _) => await RetryAllAsync();
        _export.Click += (_, _) => ExportFailures();
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
        var snapshot = _queue.Snapshot;
        _batch = snapshot.Batches.FirstOrDefault(batch => batch.Id == _batchId);
        if (_batch is null)
        {
            _rows = [];
            _summary.Text = "批次已不存在。";
            _failures.VirtualListSize = 0;
            UpdateButtons();
            return;
        }

        _rows = TransferBatchProjector.Failures(_batch, snapshot.Tasks).ToArray();
        var retryable = _rows.Count(row => row.Retryable);
        _summary.Text = $"批次：{_batch.Name}　失败 {_rows.Length:N0} 项，其中可重试 {retryable:N0} 项";
        _failures.VirtualListSize = _rows.Length;
        _failures.Invalidate();
        UpdateButtons();
    }

    private void RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs args)
    {
        if ((uint)args.ItemIndex >= (uint)_rows.Length)
        {
            args.Item = new ListViewItem(string.Empty);
            return;
        }

        var row = _rows[args.ItemIndex];
        var item = new ListViewItem(row.RelativePath);
        item.SubItems.Add(row.Category.ToString());
        item.SubItems.Add(row.HttpStatusCode?.ToString() ?? string.Empty);
        item.SubItems.Add(row.ServiceCode ?? string.Empty);
        item.SubItems.Add(row.Retryable ? "是" : "否");
        item.SubItems.Add(row.Message);
        args.Item = item;
    }

    private async Task RetrySelectedAsync()
    {
        var ids = _failures.SelectedIndices
            .Cast<int>()
            .Where(index => (uint)index < (uint)_rows.Length)
            .Select(index => _rows[index].TaskId)
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return;
        try
        {
            var retried = await _queue.RetryBatchFailuresAsync(_batchId, ids);
            MessageBox.Show(this, $"已重新排队 {retried:N0} 个可重试失败项。", "批次重试", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "批次重试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RetryAllAsync()
    {
        try
        {
            var retried = await _queue.RetryBatchFailuresAsync(_batchId);
            MessageBox.Show(this, $"已重新排队 {retried:N0} 个可重试失败项。", "批次重试", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "批次重试失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportFailures()
    {
        if (_batch is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            DefaultExt = "csv",
            AddExtension = true,
            FileName = $"s3explorer-failures-{_batch.Id:N}.csv",
            Title = "导出脱敏失败清单"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var csv = TransferBatchProjector.ExportFailuresCsv(_batch, _queue.Snapshot.Tasks);
            File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            MessageBox.Show(this, "失败清单已导出。凭据与预签名查询参数已脱敏。", "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateButtons()
    {
        _retrySelected.Enabled = _failures.SelectedIndices.Count > 0;
        _retryAll.Enabled = _rows.Any(row => row.Retryable);
        _export.Enabled = _batch is not null && _rows.Length > 0;
    }
}
