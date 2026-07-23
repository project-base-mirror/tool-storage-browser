using System.Collections.Concurrent;
using System.Diagnostics;
using S3Explorer.Core;

namespace S3Explorer.App;

internal enum TransferState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

internal sealed class TransferItem
{
    public required string Name { get; init; }
    public required string Direction { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public long Size { get; init; }
    public TransferState State { get; set; } = TransferState.Queued;
    public long Transferred { get; set; }
    public double BytesPerSecond { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? Error { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public Func<CancellationToken, IProgress<TransferProgress>, Task>? Operation { get; init; }
}

internal sealed class TransferQueueControl : UserControl
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _running = CreateList();
    private readonly ListView _completed = CreateList();
    private readonly ListView _failed = CreateList();
    private readonly ConcurrentDictionary<TransferItem, ListViewItem> _items = new();
    private SemaphoreSlim _semaphore = new(4);
    private int _maxConcurrency = 4;

    public event EventHandler? TransferCompleted;

    public TransferQueueControl()
    {
        Dock = DockStyle.Fill;
        var strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        var cancelAll = new ToolStripButton("取消全部", UiIcons.Create("■"));
        cancelAll.ToolTipText = "取消所有进行中的任务";
        cancelAll.Click += (_, _) =>
        {
            foreach (var task in _items.Keys.Where(item => item.State is TransferState.Queued or TransferState.Running))
                task.Cancellation.Cancel();
        };
        var clear = new ToolStripButton("清除已完成", UiIcons.Create("×"));
        clear.ToolTipText = "清除已完成和已取消任务";
        clear.Click += (_, _) =>
        {
            foreach (var pair in _items.Where(pair => pair.Key.State is TransferState.Completed or TransferState.Cancelled).ToArray())
            {
                pair.Value.Remove();
                _items.TryRemove(pair.Key, out _);
            }
        };
        strip.Items.AddRange([cancelAll, clear]);

        _tabs.TabPages.Add(new TabPage("进行中") { Controls = { _running } });
        _tabs.TabPages.Add(new TabPage("已完成") { Controls = { _completed } });
        _tabs.TabPages.Add(new TabPage("失败") { Controls = { _failed } });

        Controls.Add(_tabs);
        Controls.Add(strip);
        strip.Dock = DockStyle.Top;
    }

    public int ActiveCount => _items.Keys.Count(item => item.State is TransferState.Queued or TransferState.Running);
    public double UploadBytesPerSecond => _items.Keys.Where(item => item.Direction == "上传" && item.State == TransferState.Running).Sum(item => item.BytesPerSecond);
    public double DownloadBytesPerSecond => _items.Keys.Where(item => item.Direction == "下载" && item.State == TransferState.Running).Sum(item => item.BytesPerSecond);

    public void SetConcurrency(int value)
    {
        value = Math.Clamp(value, 1, 32);
        if (value == _maxConcurrency)
            return;
        _maxConcurrency = value;
        if (ActiveCount == 0)
        {
            _semaphore.Dispose();
            _semaphore = new SemaphoreSlim(value);
        }
    }

    public void Enqueue(TransferItem item)
    {
        var view = CreateViewItem(item);
        _items[item] = view;
        _running.Items.Add(view);
        _ = ExecuteAsync(item, view);
    }

    private async Task ExecuteAsync(TransferItem item, ListViewItem view)
    {
        try
        {
            await _semaphore.WaitAsync(item.Cancellation.Token);
            item.State = TransferState.Running;
            item.StartedAt = DateTimeOffset.Now;
            UpdateView(item, view, "传输中");

            var stopwatch = Stopwatch.StartNew();
            long previousBytes = 0;
            long previousTicks = 0;
            var progress = new Progress<TransferProgress>(value =>
            {
                item.Transferred = value.TransferredBytes;
                var ticks = stopwatch.ElapsedTicks;
                var elapsed = (ticks - previousTicks) / (double)Stopwatch.Frequency;
                if (elapsed >= 0.25)
                {
                    item.BytesPerSecond = Math.Max(0, (value.TransferredBytes - previousBytes) / elapsed);
                    previousBytes = value.TransferredBytes;
                    previousTicks = ticks;
                }
                UpdateView(item, view, "传输中");
            });

            if (item.Operation is null)
                throw new InvalidOperationException("传输任务没有执行操作。");
            await item.Operation(item.Cancellation.Token, progress);
            item.State = TransferState.Completed;
            item.Transferred = item.Size > 0 ? item.Size : item.Transferred;
            UpdateView(item, view, "已完成");
            MoveView(view, _completed);
            TransferCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            item.State = TransferState.Cancelled;
            UpdateView(item, view, "已取消");
            MoveView(view, _completed);
        }
        catch (Exception exception)
        {
            item.State = TransferState.Failed;
            item.Error = exception.Message;
            UpdateView(item, view, $"失败：{exception.Message}");
            MoveView(view, _failed);
        }
        finally
        {
            if (item.State != TransferState.Queued)
            {
                try { _semaphore.Release(); } catch (SemaphoreFullException) { }
            }
        }
    }

    private void UpdateView(TransferItem item, ListViewItem view, string status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateView(item, view, status));
            return;
        }
        var percent = item.Size > 0 ? item.Transferred * 100d / item.Size : 0;
        var remaining = item.BytesPerSecond > 0 && item.Size > item.Transferred
            ? TimeSpan.FromSeconds((item.Size - item.Transferred) / item.BytesPerSecond).ToString(@"hh\:mm\:ss")
            : "—";
        view.SubItems[4].Text = FileSizeFormatter.Format(item.Size);
        view.SubItems[5].Text = $"{Math.Clamp(percent, 0, 100):N0}%";
        view.SubItems[6].Text = $"{FileSizeFormatter.Format((long)item.BytesPerSecond)}/s";
        view.SubItems[7].Text = remaining;
        view.SubItems[8].Text = status;
        view.SubItems[9].Text = item.StartedAt == default ? "—" : item.StartedAt.LocalDateTime.ToString("G");
    }

    private static void MoveView(ListViewItem view, ListView target)
    {
        var clone = (ListViewItem)view.Clone();
        view.Remove();
        target.Items.Add(clone);
    }

    private static ListViewItem CreateViewItem(TransferItem item)
    {
        var view = new ListViewItem(item.Name);
        view.SubItems.Add(item.Direction);
        view.SubItems.Add(item.Source);
        view.SubItems.Add(item.Target);
        view.SubItems.Add(FileSizeFormatter.Format(item.Size));
        view.SubItems.Add("0%");
        view.SubItems.Add("0 B/s");
        view.SubItems.Add("—");
        view.SubItems.Add("等待中");
        view.SubItems.Add("—");
        view.Tag = item;
        return view;
    }

    private static ListView CreateList()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false
        };
        list.Columns.Add("文件名", 180);
        list.Columns.Add("方向", 65);
        list.Columns.Add("来源", 240);
        list.Columns.Add("目标", 240);
        list.Columns.Add("大小", 95);
        list.Columns.Add("进度", 70);
        list.Columns.Add("速度", 95);
        list.Columns.Add("剩余时间", 85);
        list.Columns.Add("状态", 210);
        list.Columns.Add("开始时间", 145);

        var menu = new ContextMenuStrip();
        var cancel = menu.Items.Add("取消");
        cancel.Click += (_, _) =>
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is TransferItem item)
                item.Cancellation.Cancel();
        };
        var copy = menu.Items.Add("复制错误");
        copy.Click += (_, _) =>
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is TransferItem item && !string.IsNullOrEmpty(item.Error))
                Clipboard.SetText(item.Error);
        };
        list.ContextMenuStrip = menu;
        return list;
    }
}
