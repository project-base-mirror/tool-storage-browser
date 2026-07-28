using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class CdnJobsDialog : Form
{
    private readonly PersistentCdnJobQueue _queue;
    private readonly IReadOnlyDictionary<Guid, CdnProfile> _profiles;
    private readonly ListView _jobs = new()
    {
        Name = "CdnJobsList",
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        GridLines = true
    };
    private readonly Label _status = new()
    {
        Name = "CdnJobsStatus",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Text = "0 个任务"
    };
    private readonly Button _retry = ActionButton("RetryCdnJobButton", "重试所选");
    private readonly Button _retryAll = ActionButton("RetryAllCdnJobsButton", "重试全部失败");
    private readonly Button _cancel = ActionButton("CancelCdnJobButton", "取消所选");
    private readonly Button _clear = ActionButton("ClearCompletedCdnJobsButton", "清理已完成");
    private readonly Button _close = ActionButton("CloseCdnJobsButton", "关闭");
    private CdnJobStoreSnapshot _snapshot = new();

    public CdnJobsDialog(PersistentCdnJobQueue queue, IReadOnlyList<CdnProfile> profiles)
    {
        _queue = queue;
        _profiles = profiles.ToDictionary(value => value.Id);

        Name = "CdnJobsDialog";
        Text = "CDN 任务中心";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1050, 570);
        MinimumSize = new Size(900, 500);
        ShowInTaskbar = false;

        _jobs.Columns.Add("状态", 105);
        _jobs.Columns.Add("操作", 120);
        _jobs.Columns.Add("CDN 配置", 150);
        _jobs.Columns.Add("URL / 数量", 275);
        _jobs.Columns.Add("尝试", 80);
        _jobs.Columns.Add("更新时间", 150);
        _jobs.Columns.Add("结果", 300);

        var actions = new FlowLayoutPanel
        {
            Name = "CdnJobsActions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right
        };
        actions.Controls.AddRange([_retry, _retryAll, _cancel, _clear, _close]);

        var footer = new TableLayoutPanel
        {
            Name = "CdnJobsFooter",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(actions, 1, 0);

        var layout = new TableLayoutPanel
        {
            Name = "CdnJobsLayout",
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_jobs, 0, 0);
        layout.Controls.Add(footer, 0, 1);
        Controls.Add(layout);

        _retry.Click += async (_, _) => await RetrySelectedAsync();
        _retryAll.Click += async (_, _) => await RunQueueActionAsync(
            "重试失败任务",
            () => _queue.RetryAllFailedAsync());
        _cancel.Click += async (_, _) => await CancelSelectedAsync();
        _clear.Click += async (_, _) => await RunQueueActionAsync(
            "清理已完成任务",
            () => _queue.RemoveCompletedAsync());
        _close.Click += (_, _) => Close();
        _jobs.SelectedIndexChanged += (_, _) => UpdateActionStates();
        _jobs.DoubleClick += (_, _) => ShowSelectedDetails();

        AcceptButton = _retry;
        CancelButton = _close;
        _close.DialogResult = DialogResult.Cancel;

        _queue.Changed += Queue_Changed;
        FormClosed += (_, _) => _queue.Changed -= Queue_Changed;
        Shown += (_, _) => ApplySnapshot(_queue.Snapshot);
        ApplySnapshot(_queue.Snapshot);
    }

    private static Button ActionButton(string name, string text) => new()
    {
        Name = name,
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(92, 34),
        Margin = new Padding(6, 0, 0, 0),
        Padding = new Padding(8, 3, 8, 3)
    };

    private void Queue_Changed(object? sender, CdnJobQueueChangedEventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (IsHandleCreated)
                BeginInvoke(() => ApplySnapshot(e.Snapshot));
            return;
        }
        ApplySnapshot(e.Snapshot);
    }

    private void ApplySnapshot(CdnJobStoreSnapshot snapshot)
    {
        if (IsDisposed) return;
        _snapshot = snapshot;
        var selectedId = SelectedJob()?.Id;
        _jobs.BeginUpdate();
        try
        {
            _jobs.Items.Clear();
            foreach (var job in snapshot.Jobs
                         .OrderByDescending(value => value.CreatedAt)
                         .ThenByDescending(value => value.UpdatedAt))
            {
                var profileName = _profiles.TryGetValue(job.CdnProfileId, out var profile)
                    ? profile.Name
                    : $"已删除 ({job.CdnProfileId:N})";
                var urlSummary = job.Urls.Count == 1
                    ? job.Urls[0]
                    : $"{job.Urls.Count} 个 URL";
                var message = job.LastError.Length > 0 ? job.LastError : job.LastMessage;
                var item = new ListViewItem(StateText(job.State))
                {
                    Name = job.Id.ToString("N"),
                    Tag = job.Id,
                    ToolTipText = string.Join(Environment.NewLine, job.Urls)
                };
                item.SubItems.Add(ActionText(job.Action));
                item.SubItems.Add(profileName);
                item.SubItems.Add(urlSummary);
                item.SubItems.Add($"{job.AttemptCount}/{job.MaxAttempts}");
                item.SubItems.Add(job.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(message);
                _jobs.Items.Add(item);
                if (selectedId == job.Id) item.Selected = true;
            }
        }
        finally
        {
            _jobs.EndUpdate();
        }

        var active = snapshot.Jobs.Count(value =>
            value.State is CdnJobState.Pending or CdnJobState.Running or CdnJobState.WaitingProvider);
        var failed = snapshot.Jobs.Count(value => value.State == CdnJobState.Failed);
        var completed = snapshot.Jobs.Count(value => value.State == CdnJobState.Completed);
        _status.Text = $"共 {snapshot.Jobs.Count} 个任务；活动 {active}；失败 {failed}；完成 {completed}";
        UpdateActionStates();
    }

    private CdnJobRecord? SelectedJob()
    {
        if (_jobs.SelectedItems.Count != 1 || _jobs.SelectedItems[0].Tag is not Guid id)
            return null;
        return _snapshot.Jobs.FirstOrDefault(value => value.Id == id);
    }

    private void UpdateActionStates()
    {
        var selected = SelectedJob();
        _retry.Enabled = selected?.State == CdnJobState.Failed;
        _cancel.Enabled = selected?.State is
            CdnJobState.Pending or CdnJobState.Running or CdnJobState.WaitingProvider;
        _retryAll.Enabled = _snapshot.Jobs.Any(value => value.State == CdnJobState.Failed);
        _clear.Enabled = _snapshot.Jobs.Any(value =>
            value.State is CdnJobState.Completed or CdnJobState.Cancelled);
    }

    private async Task RetrySelectedAsync()
    {
        var selected = SelectedJob();
        if (selected is null) return;
        await RunQueueActionAsync("重试 CDN 任务", () => _queue.RetryAsync(selected.Id));
    }

    private async Task CancelSelectedAsync()
    {
        var selected = SelectedJob();
        if (selected is null) return;
        if (MessageBox.Show(
                this,
                $"确定取消任务 {selected.Id}？",
                "取消 CDN 任务",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        await RunQueueActionAsync("取消 CDN 任务", () => _queue.CancelAsync(selected.Id));
    }

    private async Task RunQueueActionAsync(string title, Func<Task> action)
    {
        try
        {
            Enabled = false;
            await action();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, $"{title}失败", "CDN 任务中心", exception);
        }
        finally
        {
            if (!IsDisposed) Enabled = true;
        }
    }

    private void ShowSelectedDetails()
    {
        var selected = SelectedJob();
        if (selected is null) return;
        var details =
            $"任务 ID：{selected.Id}{Environment.NewLine}" +
            $"幂等键：{selected.IdempotencyKey}{Environment.NewLine}" +
            $"状态：{StateText(selected.State)}{Environment.NewLine}" +
            $"操作：{ActionText(selected.Action)}{Environment.NewLine}" +
            $"尝试：{selected.AttemptCount}/{selected.MaxAttempts}{Environment.NewLine}" +
            $"Provider 任务 ID：{(selected.ProviderTaskId.Length == 0 ? "无" : selected.ProviderTaskId)}{Environment.NewLine}" +
            $"状态码：{selected.LastStatusCode?.ToString() ?? "无"}{Environment.NewLine}" +
            $"读取：{FileSizeFormatter.Format(selected.BytesRead)}{Environment.NewLine}" +
            $"创建：{selected.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"更新：{selected.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"信息：{selected.LastMessage}{Environment.NewLine}" +
            $"错误：{selected.LastError}{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, selected.Urls);
        MessageBox.Show(this, details, "CDN 任务详情", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string StateText(CdnJobState state) => state switch
    {
        CdnJobState.Pending => "等待",
        CdnJobState.Running => "执行中",
        CdnJobState.WaitingProvider => "等待 Provider",
        CdnJobState.Completed => "完成",
        CdnJobState.Failed => "失败",
        CdnJobState.Cancelled => "已取消",
        _ => state.ToString()
    };

    private static string ActionText(CdnJobAction action) => action switch
    {
        CdnJobAction.Warmup => "HTTP 预热",
        CdnJobAction.PurgeUrl => "刷新 URL",
        CdnJobAction.PurgeThenWarmup => "刷新后预热",
        _ => action.ToString()
    };
}
