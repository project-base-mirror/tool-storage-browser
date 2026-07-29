using System.Collections.Concurrent;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record UploadBatchItem(
    string LocalPath,
    string ObjectKey,
    string RelativePath,
    long Size,
    string StorageClass);

internal sealed record DownloadBatchItem(
    string ObjectKey,
    string LocalPath,
    string RelativePath,
    long Size);

internal sealed record ObjectTransferBatchItem(
    string SourceKey,
    string DestinationBucket,
    string DestinationKey,
    string RelativePath,
    long Size,
    ObjectConflictPolicy ConflictPolicy);

internal sealed class TransferCompletedEventArgs(TransferTaskRecord task) : EventArgs
{
    public TransferTaskRecord Task { get; } = task;
}

internal sealed class TransferQueueControl : UserControl
{
    private const int VisibleStandaloneLimit = 1_000;

    private sealed record ProgressSample(long Bytes, long Total, double BytesPerSecond, DateTimeOffset At);

    private readonly PersistentTransferQueue _queue;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _batches = CreateBatchList();
    private readonly ListView _all = CreateTaskList("AllTransfersList");
    private readonly ListView _active = CreateTaskList("ActiveTransfersList");
    private readonly ListView _completed = CreateTaskList("SuccessfulTransfersList");
    private readonly ListView _failed = CreateTaskList("FailedTransfersList");
    private readonly ConcurrentDictionary<Guid, ProgressSample> _progress = new();
    private readonly Dictionary<Guid, TransferTaskState> _knownStates = [];
    private TransferBatchSummary[] _batchRows = [];
    private TransferBatchRecord[] _batchRecords = [];
    private int _maxAttempts = 4;
    private int _retryBaseDelaySeconds = 2;
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
        foreach (var task in _queue.Snapshot.Tasks)
            _knownStates[task.Id] = task.State;
        _initializing = false;
        RefreshViews(_queue.Snapshot);
    }

    public Task SetConcurrencyAsync(int value, CancellationToken cancellationToken = default) =>
        _queue.SetConcurrencyAsync(value, cancellationToken);

    public void ConfigureRetryPolicy(int retryCount, int retryBaseDelaySeconds)
    {
        _maxAttempts = Math.Clamp(retryCount + 1, 1, 21);
        _retryBaseDelaySeconds = Math.Clamp(retryBaseDelaySeconds, 0, 3600);
    }

    public Task PauseAllAsync(CancellationToken cancellationToken = default) =>
        _queue.PauseAllAsync(cancellationToken);

    public Task CancelAllAsync(CancellationToken cancellationToken = default) =>
        _queue.CancelAllAsync(cancellationToken);

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default) =>
        _queue.WaitForIdleAsync(cancellationToken);

    public Task EnqueueUploadAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        long size,
        string storageClass,
        CancellationToken cancellationToken = default) =>
        _queue.EnqueueAsync(new TransferTaskRecord
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Direction = TransferDirection.Upload,
            Bucket = bucket,
            ObjectKey = key,
            LocalPath = localPath,
            StorageClass = storageClass,
            TotalBytes = Math.Max(0, size),
            MaxAttempts = _maxAttempts,
            RetryBaseDelaySeconds = _retryBaseDelaySeconds
        }, cancellationToken);

    public Task EnqueueDownloadAsync(
        ConnectionProfile profile,
        string bucket,
        string key,
        string localPath,
        long size,
        string? versionId = null,
        CancellationToken cancellationToken = default) =>
        _queue.EnqueueAsync(new TransferTaskRecord
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Direction = TransferDirection.Download,
            Bucket = bucket,
            ObjectKey = key,
            VersionId = versionId,
            LocalPath = localPath,
            TotalBytes = Math.Max(0, size),
            MaxAttempts = _maxAttempts,
            RetryBaseDelaySeconds = _retryBaseDelaySeconds
        }, cancellationToken);

    public Task<TransferBatchRecord> CreateBatchAsync(
        ConnectionProfile profile,
        string bucket,
        string name,
        string rootPath,
        TransferDirection direction,
        CancellationToken cancellationToken = default) =>
        _queue.CreateBatchAsync(new TransferBatchRecord
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Name = name,
            Bucket = bucket,
            RootPath = rootPath,
            Direction = direction
        }, cancellationToken);

    public Task AddUploadBatchItemsAsync(
        TransferBatchRecord batch,
        IReadOnlyCollection<UploadBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        var tasks = items.Select(item => new TransferTaskRecord
        {
            BatchId = batch.Id,
            ProfileId = batch.ProfileId,
            ProfileName = batch.ProfileName,
            Direction = TransferDirection.Upload,
            Kind = TransferTaskKind.FolderBatchItem,
            Bucket = batch.Bucket,
            ObjectKey = item.ObjectKey,
            LocalPath = item.LocalPath,
            RelativePath = item.RelativePath,
            StorageClass = item.StorageClass,
            TotalBytes = Math.Max(0, item.Size),
            MaxAttempts = _maxAttempts,
            RetryBaseDelaySeconds = _retryBaseDelaySeconds
        }).ToArray();
        return _queue.AddBatchTasksAsync(batch.Id, tasks, cancellationToken);
    }

    public Task AddDownloadBatchItemsAsync(
        TransferBatchRecord batch,
        IReadOnlyCollection<DownloadBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        var tasks = items.Select(item => new TransferTaskRecord
        {
            BatchId = batch.Id,
            ProfileId = batch.ProfileId,
            ProfileName = batch.ProfileName,
            Direction = TransferDirection.Download,
            Kind = TransferTaskKind.FolderBatchItem,
            Bucket = batch.Bucket,
            ObjectKey = item.ObjectKey,
            LocalPath = LocalObjectPath.ToExtendedLengthPath(item.LocalPath),
            RelativePath = item.RelativePath,
            TotalBytes = Math.Max(0, item.Size),
            MaxAttempts = _maxAttempts,
            RetryBaseDelaySeconds = _retryBaseDelaySeconds
        }).ToArray();
        return _queue.AddBatchTasksAsync(batch.Id, tasks, cancellationToken);
    }

    public Task AddObjectTransferBatchItemsAsync(
        TransferBatchRecord batch,
        IReadOnlyCollection<ObjectTransferBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        if (batch.Direction is not (TransferDirection.Copy or TransferDirection.Move))
            throw new InvalidOperationException("对象传输批次方向必须是复制或移动。");
        var tasks = items.Select(item => new TransferTaskRecord
        {
            BatchId = batch.Id,
            ProfileId = batch.ProfileId,
            ProfileName = batch.ProfileName,
            Direction = batch.Direction,
            Kind = TransferTaskKind.ObjectTransfer,
            Bucket = batch.Bucket,
            ObjectKey = item.SourceKey,
            DestinationBucket = item.DestinationBucket,
            DestinationObjectKey = item.DestinationKey,
            ConflictPolicy = item.ConflictPolicy,
            RelativePath = item.RelativePath,
            TotalBytes = Math.Max(0, item.Size),
            MaxAttempts = _maxAttempts,
            RetryBaseDelaySeconds = _retryBaseDelaySeconds
        }).ToArray();
        return _queue.AddBatchTasksAsync(batch.Id, tasks, cancellationToken);
    }

    public Task CompleteBatchDiscoveryAsync(
        Guid batchId,
        int skippedCount = 0,
        CancellationToken cancellationToken = default) =>
        _queue.CompleteBatchDiscoveryAsync(batchId, skippedCount, cancellationToken);

    private void BuildUi()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        toolbar.Items.Add(ActionButton("暂停", UiIconKind.Transfers, task => _queue.PauseAsync(task.Id)));
        toolbar.Items.Add(ActionButton("继续", UiIconKind.Upload, task => _queue.ResumeAsync(task.Id)));
        toolbar.Items.Add(ActionButton("取消", UiIconKind.Delete, task => _queue.CancelAsync(task.Id)));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ActionButton("重试", UiIconKind.Refresh, task => _queue.RetryAsync(task.Id)));
        var taskDetails = new ToolStripButton("任务详情", UiIcons.Create(UiIconKind.Info, 16));
        taskDetails.Click += (_, _) => OpenSelectedTaskDetails();
        toolbar.Items.Add(taskDetails);
        toolbar.Items.Add(GlobalButton("重试全部失败", UiIconKind.Refresh, () => _queue.RetryAllFailedAsync()));
        toolbar.Items.Add(GlobalButton("暂停全部", UiIconKind.Transfers, () => _queue.PauseAllAsync()));
        toolbar.Items.Add(GlobalButton("取消全部", UiIconKind.Delete, () => _queue.CancelAllAsync()));
        toolbar.Items.Add(GlobalButton("清除已完成", UiIconKind.Delete, () => _queue.RemoveCompletedAsync()));
        toolbar.Items.Add(new ToolStripSeparator());
        var details = new ToolStripButton("批次明细", UiIcons.Create(UiIconKind.Transfers, 16));
        details.Click += (_, _) => OpenSelectedBatchDetails();
        toolbar.Items.Add(details);
        var retryBatch = new ToolStripButton("重试批次失败项", UiIcons.Create(UiIconKind.Refresh, 16));
        retryBatch.Click += async (_, _) =>
        {
            var batch = SelectedBatch();
            if (batch is not null)
                await ExecuteActionAsync(() => _queue.RetryBatchFailuresAsync(batch.Id));
        };
        toolbar.Items.Add(retryBatch);
        var cancelBatch = new ToolStripButton("取消批次", UiIcons.Create(UiIconKind.Delete, 16));
        cancelBatch.Click += async (_, _) =>
        {
            var batch = SelectedBatch();
            if (batch is not null)
                await ExecuteActionAsync(() => _queue.CancelBatchAsync(batch.Id));
        };
        toolbar.Items.Add(cancelBatch);

        AddTab("批次", _batches);
        AddTab("全部", _all);
        AddTab("进行中", _active);
        AddTab("成功", _completed);
        AddTab("失败", _failed);
        _batches.RetrieveVirtualItem += RetrieveBatchItem;
        _batches.DoubleClick += (_, _) => OpenSelectedBatchDetails();
        ConfigureTaskList(_all);
        ConfigureTaskList(_active);
        ConfigureTaskList(_completed);
        ConfigureTaskList(_failed);

        Controls.Add(_tabs);
        Controls.Add(toolbar);
    }

    private ToolStripButton ActionButton(
        string text,
        UiIconKind icon,
        Func<TransferTaskRecord, Task> action)
    {
        var button = new ToolStripButton(text, UiIcons.Create(icon, 16));
        button.Click += async (_, _) =>
        {
            var task = SelectedTask();
            if (task is not null)
                await ExecuteActionAsync(() => action(task));
        };
        return button;
    }

    private ToolStripButton GlobalButton(string text, UiIconKind icon, Func<Task> action)
    {
        var button = new ToolStripButton(text, UiIcons.Create(icon, 16));
        button.Click += async (_, _) => await ExecuteActionAsync(action);
        return button;
    }

    private void AddTab(string text, Control control)
    {
        var page = new TabPage(text);
        page.Controls.Add(control);
        _tabs.TabPages.Add(page);
    }

    private static ListView CreateTaskList(string name)
    {
        var list = new ListView
        {
            Name = name,
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            GridLines = true,
            ShowItemToolTips = true
        };
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

    private void ConfigureTaskList(ListView list)
    {
        list.DoubleClick += (_, _) => OpenSelectedTaskDetails();
        list.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Right)
                return;
            var item = list.GetItemAt(args.X, args.Y);
            if (item is not null)
                item.Selected = true;
        };
        list.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                OpenSelectedTaskDetails();
                args.Handled = true;
            }
            else if (args.Control && args.KeyCode == Keys.C)
            {
                CopySelectedTaskDetails();
                args.SuppressKeyPress = true;
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("查看详细信息", UiIcons.Create(UiIconKind.Info, 16), (_, _) => OpenSelectedTaskDetails());
        menu.Items.Add("复制详细信息", UiIcons.Create(UiIconKind.Copy, 16), (_, _) => CopySelectedTaskDetails());
        list.ContextMenuStrip = menu;
    }

    private static ListView CreateBatchList()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            GridLines = true,
            VirtualMode = true
        };
        list.Columns.Add("批次", 240);
        list.Columns.Add("方向", 70);
        list.Columns.Add("状态", 120);
        list.Columns.Add("总文件", 80);
        list.Columns.Add("完成", 70);
        list.Columns.Add("失败", 70);
        list.Columns.Add("跳过", 70);
        list.Columns.Add("活动", 70);
        list.Columns.Add("进度", 90);
        list.Columns.Add("已传输 / 总大小", 190);
        return list;
    }

    private void QueueChanged(object? sender, TransferQueueChangedEventArgs args) => InvokeOnUi(() =>
    {
        if (!_initializing)
        {
            foreach (var task in args.Snapshot.Tasks)
            {
                _knownStates.TryGetValue(task.Id, out var previous);
                if (task.State == TransferTaskState.Completed &&
                    previous != TransferTaskState.Completed)
                {
                    TransferCompleted?.Invoke(this, new TransferCompletedEventArgs(task));
                }
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
            speed = seconds > 0.05
                ? Math.Max(0, (args.Progress.TransferredBytes - previous.Bytes) / seconds)
                : previous.BytesPerSecond;
        }
        _progress[args.TaskId] = new ProgressSample(
            args.Progress.TransferredBytes,
            args.Progress.TotalBytes,
            speed,
            now);
        InvokeOnUi(() => RefreshViews(_queue.Snapshot));
    }

    private void RefreshViews(TransferStoreSnapshot snapshot)
    {
        if (IsDisposed)
            return;

        var liveProgress = _progress.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes);
        _batchRecords = snapshot.Batches
            .OrderByDescending(batch => batch.UpdatedAt)
            .ThenByDescending(batch => batch.CreatedAt)
            .ToArray();
        _batchRows = _batchRecords
            .Select(batch => TransferBatchProjector.Project(batch, snapshot.Tasks, liveProgress))
            .ToArray();
        _batches.VirtualListSize = _batchRows.Length;
        _batches.Invalidate();

        var standalone = snapshot.Tasks
            .Where(task => task.BatchId is null)
            .OrderByDescending(task => task.UpdatedAt)
            .ToArray();
        var active = standalone
            .Where(task => task.State is not (
                TransferTaskState.Completed or
                TransferTaskState.Cancelled or
                TransferTaskState.Failed))
            .OrderByDescending(task => task.UpdatedAt)
            .ToArray();
        var completed = standalone
            .Where(task => task.State == TransferTaskState.Completed)
            .ToArray();
        var failed = standalone
            .Where(task => task.State == TransferTaskState.Failed)
            .OrderByDescending(task => task.UpdatedAt)
            .ToArray();

        Populate(_all, standalone.Take(VisibleStandaloneLimit));
        Populate(_active, active.Take(VisibleStandaloneLimit));
        Populate(_completed, completed.Take(VisibleStandaloneLimit));
        Populate(_failed, failed.Take(VisibleStandaloneLimit));

        _tabs.TabPages[0].Text = $"批次 ({_batchRows.Length:N0})";
        _tabs.TabPages[1].Text = TabText("全部", standalone.Length);
        _tabs.TabPages[2].Text = TabText("进行中", active.Length);
        _tabs.TabPages[3].Text = TabText("成功", completed.Length);
        _tabs.TabPages[4].Text = TabText("失败", failed.Length);
    }

    private void RetrieveBatchItem(object? sender, RetrieveVirtualItemEventArgs args)
    {
        if ((uint)args.ItemIndex >= (uint)_batchRows.Length)
        {
            args.Item = new ListViewItem(string.Empty);
            return;
        }

        var row = _batchRows[args.ItemIndex];
        var item = new ListViewItem(row.Name);
        item.SubItems.Add(DirectionText(row.Direction));
        item.SubItems.Add(BatchStateText(row.State));
        item.SubItems.Add(row.TotalFiles.ToString("N0"));
        item.SubItems.Add(row.CompletedFiles.ToString("N0"));
        item.SubItems.Add(row.FailedFiles.ToString("N0"));
        item.SubItems.Add(row.SkippedFiles.ToString("N0"));
        item.SubItems.Add(row.ActiveFiles.ToString("N0"));
        item.SubItems.Add($"{row.ProgressPercentage:N1}%");
        item.SubItems.Add($"{FormatBytes(row.TransferredBytes)} / {FormatBytes(row.TotalBytes)}");
        args.Item = item;
    }

    private void Populate(ListView list, IEnumerable<TransferTaskRecord> tasks)
    {
        var selectedId = list.SelectedItems.Count == 1 &&
            list.SelectedItems[0].Tag is TransferTaskRecord selected
                ? selected.Id
                : Guid.Empty;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var task in tasks)
                list.Items.Add(CreateItem(task));
            var selectedItem = list.Items
                .Cast<ListViewItem>()
                .FirstOrDefault(entry =>
                    entry.Tag is TransferTaskRecord task &&
                    task.Id == selectedId);
            if (selectedItem is not null)
                selectedItem.Selected = true;
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private ListViewItem CreateItem(TransferTaskRecord task)
    {
        _progress.TryGetValue(task.Id, out var sample);
        var transferred = task.State == TransferTaskState.Completed
            ? task.TotalBytes
            : sample?.Bytes ?? task.TransferredBytes;
        var total = sample?.Total > 0 ? sample.Total : task.TotalBytes;
        var speed = task.State == TransferTaskState.Running
            ? sample?.BytesPerSecond ?? 0
            : 0;
        var source = task.Direction switch
        {
            TransferDirection.Upload => task.LocalPath,
            TransferDirection.DeleteLocal => task.LocalPath,
            _ => $"s3://{task.Bucket}/{task.ObjectKey}"
        };
        var target = task.Direction switch
        {
            TransferDirection.Upload => $"s3://{task.Bucket}/{task.ObjectKey}",
            TransferDirection.Download => task.LocalPath,
            TransferDirection.Copy or TransferDirection.Move =>
                $"s3://{task.DestinationBucket}/{task.DestinationObjectKey}",
            TransferDirection.DeleteRemote => "删除远端对象",
            TransferDirection.DeleteLocal => "删除本地文件",
            _ => string.Empty
        };
        var name = task.Direction is TransferDirection.Upload or TransferDirection.DeleteLocal
            ? Path.GetFileName(task.LocalPath)
            : Path.GetFileName(task.ObjectKey.TrimEnd('/'));
        var percentage = total <= 0
            ? 0
            : Math.Clamp(transferred * 100d / total, 0, 100);
        var remaining = speed > 0 && total > transferred
            ? TimeSpan.FromSeconds((total - transferred) / speed).ToString(@"hh\:mm\:ss")
            : "—";
        var item = new ListViewItem(name)
        {
            Tag = task,
            ToolTipText = task.Failure?.SafeMessage ?? string.Empty
        };
        item.SubItems.Add(DirectionText(task.Direction));
        item.SubItems.Add(source);
        item.SubItems.Add(target);
        item.SubItems.Add(FormatBytes(total));
        item.SubItems.Add($"{percentage:N1}%");
        item.SubItems.Add(speed > 0 ? $"{FormatBytes((long)speed)}/s" : "—");
        item.SubItems.Add(remaining);
        item.SubItems.Add(StateText(task.State));
        item.SubItems.Add(task.Failure?.SafeMessage ?? string.Empty);
        return item;
    }

    private TransferTaskRecord? SelectedTask()
    {
        var list = _tabs.SelectedTab?.Controls
            .OfType<ListView>()
            .FirstOrDefault();
        if (list is null || list == _batches || list.SelectedItems.Count != 1)
            return null;
        return list.SelectedItems[0].Tag as TransferTaskRecord;
    }

    private TransferBatchRecord? SelectedBatch()
    {
        if (_tabs.SelectedIndex != 0 || _batches.SelectedIndices.Count != 1)
            return null;
        var index = _batches.SelectedIndices[0];
        return (uint)index < (uint)_batchRecords.Length
            ? _batchRecords[index]
            : null;
    }

    private void OpenSelectedBatchDetails()
    {
        var batch = SelectedBatch();
        if (batch is null)
            return;
        using var dialog = new BatchFailureDialog(_queue, batch.Id);
        dialog.ShowDialog(this);
    }

    private void OpenSelectedTaskDetails()
    {
        var task = SelectedTask();
        if (task is null)
            return;
        using var dialog = new TransferTaskDetailsDialog(task);
        dialog.ShowDialog(this);
    }

    private void CopySelectedTaskDetails()
    {
        var task = SelectedTask();
        if (task is null)
            return;
        Clipboard.SetText(TransferTaskDetailsFormatter.Format(task));
    }

    private async Task ExecuteActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "传输队列操作失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private double CurrentSpeed(TransferDirection direction) => _queue.Snapshot.Tasks
        .Where(task =>
            task.Direction == direction &&
            task.State == TransferTaskState.Running)
        .Sum(task =>
            _progress.TryGetValue(task.Id, out var sample)
                ? sample.BytesPerSecond
                : 0);

    private void InvokeOnUi(Action action)
    {
        if (IsDisposed || Disposing)
            return;
        if (InvokeRequired)
        {
            if (IsHandleCreated)
                BeginInvoke(action);
            return;
        }
        action();
    }

    private static string TabText(string name, int total) =>
        total > VisibleStandaloneLimit
            ? $"{name} ({total:N0}，显示前 {VisibleStandaloneLimit:N0})"
            : $"{name} ({total:N0})";

    private static string BatchStateText(TransferBatchState state) => state switch
    {
        TransferBatchState.Discovering => "正在发现文件",
        TransferBatchState.Queued => "排队中",
        TransferBatchState.Running => "进行中",
        TransferBatchState.Completed => "已完成",
        TransferBatchState.CompletedWithFailures => "完成但有失败",
        TransferBatchState.Cancelled => "已取消",
        _ => state.ToString()
    };

    private static string DirectionText(TransferDirection direction) => direction switch
    {
        TransferDirection.Upload => "上传",
        TransferDirection.Download => "下载",
        TransferDirection.Copy => "复制",
        TransferDirection.Move => "移动",
        TransferDirection.DeleteRemote => "删除远端",
        TransferDirection.DeleteLocal => "删除本地",
        _ => direction.ToString()
    };

    private static string StateText(TransferTaskState state) => state switch
    {
        TransferTaskState.Queued => "排队中",
        TransferTaskState.Running => "进行中",
        TransferTaskState.Paused => "已暂停",
        TransferTaskState.RetryPending => "等待重试",
        TransferTaskState.Interrupted => "已中断，可继续",
        TransferTaskState.Completed => "成功",
        TransferTaskState.Failed => "失败",
        TransferTaskState.Cancelled => "已取消",
        TransferTaskState.CleanupPending => "等待清理",
        _ => state.ToString()
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = Math.Max(0, (double)value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:N1} {units[unit]}";
    }
}
