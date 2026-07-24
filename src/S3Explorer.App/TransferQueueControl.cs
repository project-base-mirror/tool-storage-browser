using System.Collections.Concurrent;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class TransferCompletedEventArgs(TransferTaskRecord task) : EventArgs
{
    public TransferTaskRecord Task { get; } = task;
}

internal sealed class TransferQueueControl : UserControl
{
    private sealed record ProgressSample(long Bytes, long Total, double BytesPerSecond, DateTimeOffset At);

    private readonly PersistentTransferQueue _queue;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _active = CreateList();
    private readonly ListView _completed = CreateList();
    private readonly ListView _failed = CreateList();
    private readonly ConcurrentDictionary<Guid, ProgressSample> _progress = new();
    private readonly Dictionary<Guid, TransferTaskState> _knownStates = [];
    private bool _initializing;

    public TransferQueueControl(PersistentTransferQueue queue)
    {
        _queue = queue;
        Dock = DockStyle.Fill;
        BuildUi();
        _queue.Changed += QueueChanged;
        _queue.ProgressChanged += QueueProgressChanged;
    }

    public event EventHandler<TransferCompletedEventArgs>? TransferCompleted;
    public TransferStoreSnapshot Snapshot => _queue.Snapshot;
    public int ActiveCount => _queue.ActiveCount;
    public double UploadBytesPerSecond => CurrentSpeed(TransferDirection.Upload);
    public double DownloadBytesPerSecond => CurrentSpeed(TransferDirection.Download);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initializing = true;
        await _queue.InitializeAsync(cancellationToken);
        foreach (var task in _queue.Snapshot.Tasks) _knownStates[task.Id] = task.State;
        _initializing = false;
        RefreshViews(_queue.Snapshot);
    }

    public Task SetConcurrencyAsync(int value, CancellationToken cancellationToken = default) => _queue.SetConcurrencyAsync(value, cancellationToken);
    public Task PauseAllAsync(CancellationToken cancellationToken = default) => _queue.PauseAllAsync(cancellationToken);
    public Task CancelAllAsync(CancellationToken cancellationToken = default) => _queue.CancelAllAsync(cancellationToken);
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default) => _queue.WaitForIdleAsync(cancellationToken);

    public Task EnqueueUploadAsync(ConnectionProfile profile, string bucket, string key, string localPath, long size, string storageClass, CancellationToken cancellationToken = default) =>
        _queue.EnqueueAsync(new TransferTaskRecord
        {
            ProfileId = profile.Id, ProfileName = profile.Name, Direction = TransferDirection.Upload, Bucket = bucket,
            ObjectKey = key, LocalPath = localPath, StorageClass = storageClass, TotalBytes = Math.Max(0, size), MaxAttempts = 3
        }, cancellationToken);

    public Task EnqueueDownloadAsync(ConnectionProfile profile, string bucket, string key, string localPath, long size, CancellationToken cancellationToken = default) =>
        _queue.EnqueueAsync(new TransferTaskRecord
        {
            ProfileId = profile.Id, ProfileName = profile.Name, Direction = TransferDirection.Download, Bucket = bucket,
            ObjectKey = key, LocalPath = localPath, TotalBytes = Math.Max(0, size), MaxAttempts = 3
        }, cancellationToken);

    private void BuildUi()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        toolbar.Items.Add(ActionButton("暂停", UiIconKind.Transfers, task => _queue.PauseAsync(task.Id)));
        toolbar.Items.Add(ActionButton("继续", UiIconKind.Upload, task => _queue.ResumeAsync(task.Id)));
        toolbar.Items.Add(ActionButton("取消", UiIconKind.Delete, task => _queue.CancelAsync(task.Id)));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ActionButton("重试", UiIconKind.Refresh, task => _queue.RetryAsync(task.Id)));
        toolbar.Items.Add(GlobalButton("重试全部失败", UiIconKind.Refresh, () => _queue.RetryAllFailedAsync()));
        toolbar.Items.Add(GlobalButton("暂停全部", UiIconKind.Transfers, () => _queue.PauseAllAsync()));
        toolbar.Items.Add(GlobalButton("取消全部", UiIconKind.Delete, () => _queue.CancelAllAsync()));
        toolbar.Items.Add(GlobalButton("清除已完成", UiIconKind.Delete, () => _queue.RemoveCompletedAsync()));
        AddTab("进行中", _active);
        AddTab("已完成", _completed);
        AddTab("失败", _failed);
        Controls.Add(_tabs);
        Controls.Add(toolbar);
    }

    private ToolStripButton ActionButton(string text, UiIconKind icon, Func<TransferTaskRecord, Task> action)
    {
        var button = new ToolStripButton(text, UiIcons.Create(icon, 16));
        button.Click += async (_, _) =>
        {
            var task = SelectedTask();
            if (task is not null) await ExecuteActionAsync(() => action(task));
        };
        return button;
    }

    private ToolStripButton GlobalButton(string text, UiIconKind icon, Func<Task> action)
    {
        var button = new ToolStripButton(text, UiIcons.Create(icon, 16));
        button.Click += async (_, _) => await ExecuteActionAsync(action);
        return button;
    }

    private void AddTab(string text, ListView list)
    {
        var page = new TabPage(text);
        page.Controls.Add(list);
        _tabs.TabPages.Add(page);
    }

    private static ListView CreateList()
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, GridLines = true };
        list.Columns.Add("文件名", 190);
        list.Columns.Add("方向", 60);
        list.Columns.Add("来源", 240);
        list.Columns.Add("目标", 240);
        list.Columns.Add("大小", 80);
        list.Columns.Add("进度", 80);
        list.Columns.Add("速度", 90);
        list.Columns.Add("剩余时间", 90);
        list.Columns.Add("状态", 110);
        list.Columns.Add("错误", 260);
        return list;
    }

    private void QueueChanged(object? sender, TransferQueueChangedEventArgs args) => InvokeOnUi(() =>
    {
        if (!_initializing)
        {
            foreach (var task in args.Snapshot.Tasks)
            {
                _knownStates.TryGetValue(task.Id, out var previous);
                if (task.State == TransferTaskState.Completed && previous != TransferTaskState.Completed)
                    TransferCompleted?.Invoke(this, new TransferCompletedEventArgs(task));
                _knownStates[task.Id] = task.State;
            }
        }
        RefreshViews(args.Snapshot);
    });

    private void QueueProgressChanged(object? sender, TransferTaskProgressEventArgs args)
    {
        var now = DateTimeOffset.UtcNow;
        var speed = 0d;
        if (_progress.TryGetValue(args.TaskId, out var previous))
        {
            var seconds = (now - previous.At).TotalSeconds;
            speed = seconds > 0.05 ? Math.Max(0, (args.Progress.TransferredBytes - previous.Bytes) / seconds) : previous.BytesPerSecond;
        }
        _progress[args.TaskId] = new ProgressSample(args.Progress.TransferredBytes, args.Progress.TotalBytes, speed, now);
        InvokeOnUi(() => RefreshViews(_queue.Snapshot));
    }

    private void RefreshViews(TransferStoreSnapshot snapshot)
    {
        if (IsDisposed) return;
        Populate(_active, snapshot.Tasks.Where(task => task.State is not (TransferTaskState.Completed or TransferTaskState.Cancelled or TransferTaskState.Failed)));
        Populate(_completed, snapshot.Tasks.Where(task => task.State is TransferTaskState.Completed or TransferTaskState.Cancelled));
        Populate(_failed, snapshot.Tasks.Where(task => task.State == TransferTaskState.Failed));
        _tabs.TabPages[0].Text = $"进行中 ({_active.Items.Count})";
        _tabs.TabPages[1].Text = $"已完成 ({_completed.Items.Count})";
        _tabs.TabPages[2].Text = $"失败 ({_failed.Items.Count})";
    }

    private void Populate(ListView list, IEnumerable<TransferTaskRecord> tasks)
    {
        var selectedId = list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is TransferTaskRecord selected ? selected.Id : Guid.Empty;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var task in tasks.OrderByDescending(item => item.UpdatedAt)) list.Items.Add(CreateItem(task));
            var selectedItem = list.Items.Cast<ListViewItem>().FirstOrDefault(entry => entry.Tag is TransferTaskRecord task && task.Id == selectedId);
            if (selectedItem is not null) selectedItem.Selected = true;
        }
        finally { list.EndUpdate(); }
    }

    private ListViewItem CreateItem(TransferTaskRecord task)
    {
        _progress.TryGetValue(task.Id, out var sample);
        var transferred = task.State == TransferTaskState.Completed ? task.TotalBytes : sample?.Bytes ?? task.TransferredBytes;
        var total = sample?.Total > 0 ? sample.Total : task.TotalBytes;
        var speed = task.State == TransferTaskState.Running ? sample?.BytesPerSecond ?? 0 : 0;
        var source = task.Direction == TransferDirection.Upload ? task.LocalPath : $"s3://{task.Bucket}/{task.ObjectKey}";
        var target = task.Direction == TransferDirection.Upload ? $"s3://{task.Bucket}/{task.ObjectKey}" : task.LocalPath;
        var name = task.Direction == TransferDirection.Upload ? Path.GetFileName(task.LocalPath) : Path.GetFileName(task.ObjectKey);
        var percentage = total <= 0 ? 0 : Math.Clamp(transferred * 100d / total, 0, 100);
        var remaining = speed > 0 && total > transferred ? TimeSpan.FromSeconds((total - transferred) / speed).ToString(@"hh\:mm\:ss") : "—";
        var item = new ListViewItem(name) { Tag = task };
        item.SubItems.Add(task.Direction == TransferDirection.Upload ? "上传" : "下载");
        item.SubItems.Add(source); item.SubItems.Add(target); item.SubItems.Add(FormatBytes(total));
        item.SubItems.Add($"{percentage:N1}%"); item.SubItems.Add(speed > 0 ? $"{FormatBytes((long)speed)}/s" : "—");
        item.SubItems.Add(remaining); item.SubItems.Add(StateText(task.State)); item.SubItems.Add(task.Failure?.SafeMessage ?? string.Empty);
        return item;
    }

    private TransferTaskRecord? SelectedTask()
    {
        var list = _tabs.SelectedTab?.Controls.OfType<ListView>().FirstOrDefault();
        return list?.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as TransferTaskRecord : null;
    }

    private async Task ExecuteActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "传输队列操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private double CurrentSpeed(TransferDirection direction) => _queue.Snapshot.Tasks
        .Where(task => task.Direction == direction && task.State == TransferTaskState.Running)
        .Sum(task => _progress.TryGetValue(task.Id, out var sample) ? sample.BytesPerSecond : 0);

    private void InvokeOnUi(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(action); return; }
        action();
    }

    private static string StateText(TransferTaskState state) => state switch
    {
        TransferTaskState.Queued => "排队中", TransferTaskState.Running => "进行中", TransferTaskState.Paused => "已暂停",
        TransferTaskState.RetryPending => "等待重试", TransferTaskState.Interrupted => "已中断，可继续", TransferTaskState.Completed => "已完成",
        TransferTaskState.Failed => "失败", TransferTaskState.Cancelled => "已取消", TransferTaskState.CleanupPending => "等待清理", _ => state.ToString()
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = Math.Max(0, (double)value); var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:N1} {units[unit]}";
    }
}
