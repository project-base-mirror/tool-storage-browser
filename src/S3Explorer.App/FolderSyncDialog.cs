using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class FolderSyncDialog : Form
{
    private readonly IFolderSyncJobStore _jobStore;
    private readonly IProfileStore _profileStore;
    private readonly IS3StorageService _storage;
    private readonly PersistentTransferQueue _transferQueue;
    private readonly ConnectionProfile? _initialProfile;
    private readonly string? _initialBucket;
    private readonly string? _initialPrefix;
    private readonly int _pageSize;
    private readonly int _itemLimit;
    private readonly int _maxAttempts;
    private readonly int _retryDelaySeconds;
    private readonly ListView _jobList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
    };
    private readonly Label _sourceCaption = PathCaption("本地源");
    private readonly Label _destinationCaption = PathCaption("S3 目标");
    private readonly Label _source = PathValue();
    private readonly Label _destination = PathValue();
    private readonly Label _arrow = new()
    {
        Text = "→",
        TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(37, 99, 235),
        Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 15f, FontStyle.Bold)
    };
    private readonly ListView _results = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
        HideSelection = false
    };
    private readonly ToolStrip _actions = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Bottom, ImageScalingSize = new Size(18, 18) };
    private readonly ToolStripButton _analyze = new("分析", UiIcons.Create(UiIconKind.Analyze, 18));
    private readonly ToolStripButton _synchronize = new("开始同步", UiIcons.Create(UiIconKind.Sync, 18)) { Enabled = false };
    private readonly ToolStripButton _stop = new("停止", UiIcons.Create(UiIconKind.Delete, 18)) { Enabled = false };
    private readonly ToolStripButton _add = new("添加任务", UiIcons.Create(UiIconKind.NewConnection, 18));
    private readonly ToolStripButton _edit = new("编辑任务", UiIcons.Create(UiIconKind.Settings, 18)) { Enabled = false };
    private readonly ToolStripButton _delete = new("删除任务", UiIcons.Create(UiIconKind.Delete, 18)) { Enabled = false };
    private readonly ToolStripComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripLabel _summary = new("尚未分析") { Alignment = ToolStripItemAlignment.Right };
    private readonly Label _empty = new()
    {
        Text = "尚未创建同步任务。点击“添加任务”设置本地文件夹与 S3 位置。",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText
    };
    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private List<FolderSyncJob> _jobs = [];
    private FolderSyncPlan? _plan;
    private CancellationTokenSource? _operation;

    public bool QueuedTransfers { get; private set; }

    public FolderSyncDialog(
        IFolderSyncJobStore jobStore,
        IProfileStore profileStore,
        IS3StorageService storage,
        PersistentTransferQueue transferQueue,
        AppSettings settings,
        ConnectionProfile? initialProfile = null,
        string? initialBucket = null,
        string? initialPrefix = null)
    {
        _jobStore = jobStore;
        _profileStore = profileStore;
        _storage = storage;
        _transferQueue = transferQueue;
        _initialProfile = initialProfile;
        _initialBucket = initialBucket;
        _initialPrefix = initialPrefix;
        _pageSize = settings.ObjectPageSize;
        _itemLimit = settings.ObjectCacheLimit;
        _maxAttempts = Math.Max(1, settings.RetryCount);
        _retryDelaySeconds = Math.Max(0, settings.RetryDelaySeconds);

        Text = "文件夹同步";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 650);
        MinimumSize = new Size(820, 520);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        BuildLayout();
        WireEvents();
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) => _operation?.Cancel();
    }

    private void BuildLayout()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 82,
            Padding = new Padding(16, 12, 16, 8),
            ColumnCount = 2
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new PictureBox
        {
            Image = UiIcons.Create(UiIconKind.Sync, 42),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill
        }, 0, 0);
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(new Label
        {
            Text = "文件夹同步",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(3, 4, 3, 1)
        });
        heading.Controls.Add(new Label
        {
            Text = "先分析差异，再将确认的单向镜像操作加入可恢复传输队列。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        });
        header.Controls.Add(heading, 1, 0);

        var route = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), ColumnCount = 3 };
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        route.Controls.Add(CreatePathCard(_sourceCaption, _source, UiIconKind.Folder), 0, 0);
        route.Controls.Add(_arrow, 1, 0);
        route.Controls.Add(CreatePathCard(_destinationCaption, _destination, UiIconKind.Bucket), 2, 0);

        _results.Columns.Add("文件", 300);
        _results.Columns.Add("状态", 80);
        _results.Columns.Add("操作", 90);
        _results.Columns.Add("本地大小", 100, HorizontalAlignment.Right);
        _results.Columns.Add("远端大小", 100, HorizontalAlignment.Right);
        _results.Columns.Add("原因", 240);
        _body.Controls.Add(_empty);
        _body.Controls.Add(_results);

        _jobList.Columns.Add("名称", 132);
        _jobList.Columns.Add("方向", 82);
        var navigation = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 8, 10) };
        navigation.Controls.Add(_jobList);
        navigation.Controls.Add(new Label
        {
            Text = "任务",
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(3, 6, 0, 0),
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold)
        });

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(route, 0, 0);
        content.Controls.Add(_body, 0, 1);

        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspace.Controls.Add(navigation, 0, 0);
        workspace.Controls.Add(content, 1, 0);

        _filter.Items.AddRange(["全部", "待执行", "新增", "已更改", "已删除", "已排除"]);
        _filter.SelectedIndex = 0;
        _actions.Items.AddRange([
            _analyze, _synchronize, _stop,
            new ToolStripSeparator(),
            _add, _edit, _delete,
            new ToolStripSeparator(),
            _summary,
            new ToolStripLabel("筛选：") { Alignment = ToolStripItemAlignment.Right },
            _filter
        ]);

        Controls.Add(_actions);
        Controls.Add(workspace);
        Controls.Add(header);
        _results.Visible = false;
    }

    private void WireEvents()
    {
        _jobList.SelectedIndexChanged += (_, _) => SelectCurrentJob();
        _filter.SelectedIndexChanged += (_, _) => PopulateResults();
        _add.Click += async (_, _) => await AddJobAsync();
        _edit.Click += async (_, _) => await EditJobAsync();
        _delete.Click += async (_, _) => await DeleteJobAsync();
        _analyze.Click += async (_, _) => await AnalyzeAsync();
        _synchronize.Click += async (_, _) => await QueueSynchronizationAsync();
        _stop.Click += (_, _) => _operation?.Cancel();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _profiles = await _profileStore.LoadAsync();
            _jobs = (await _jobStore.LoadAsync()).ToList();
            RebuildJobList();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法加载同步任务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RebuildJobList(Guid? selectedId = null)
    {
        selectedId ??= CurrentJob()?.Id;
        _jobList.BeginUpdate();
        _jobList.Items.Clear();
        foreach (var job in _jobs)
        {
            var item = new ListViewItem(job.Name) { Tag = job.Id };
            item.SubItems.Add(job.Direction == FolderSyncDirection.Upload ? "本地 → S3" : "S3 → 本地");
            _jobList.Items.Add(item);
        }
        _jobList.EndUpdate();
        if (_jobList.Items.Count > 0)
        {
            var index = selectedId is null ? 0 : _jobs.FindIndex(job => job.Id == selectedId);
            _jobList.Items[index >= 0 ? index : 0].Selected = true;
        }
        SelectCurrentJob();
    }

    private void SelectCurrentJob()
    {
        _plan = null;
        var job = CurrentJob();
        var hasJob = job is not null;
        _edit.Enabled = _delete.Enabled = _analyze.Enabled = hasJob;
        _synchronize.Enabled = false;
        _results.Items.Clear();
        _results.Visible = false;
        _empty.Visible = true;
        _summary.Text = hasJob ? "尚未分析" : "没有同步任务";
        if (job is null)
        {
            _source.Text = "选择源文件夹";
            _destination.Text = "选择目标文件夹";
            _sourceCaption.Text = "源路径";
            _destinationCaption.Text = "目标路径";
            _empty.Text = "尚未创建同步任务。点击“添加任务”设置本地文件夹与 S3 位置。";
            return;
        }

        if (job.Direction == FolderSyncDirection.Upload)
        {
            _sourceCaption.Text = "本地源";
            _destinationCaption.Text = "S3 目标";
            _source.Text = job.LocalDirectory;
            _destination.Text = job.S3Location;
        }
        else
        {
            _sourceCaption.Text = "S3 源";
            _destinationCaption.Text = "本地目标";
            _source.Text = job.S3Location;
            _destination.Text = job.LocalDirectory;
        }
        _empty.Text = "点击“分析”比较源与目标。分析不会修改任何文件。";
    }

    private async Task AddJobAsync()
    {
        if (_profiles.Count == 0)
        {
            MessageBox.Show(this, "请先在主窗口创建对象存储连接。", "文件夹同步", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new FolderSyncJobDialog(_storage, _profiles, null, _initialProfile, _initialBucket, _initialPrefix);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _jobs.Add(dialog.Job);
        await _jobStore.SaveAsync(_jobs);
        RebuildJobList(dialog.Job.Id);
    }

    private async Task EditJobAsync()
    {
        var job = CurrentJob();
        if (job is null) return;
        using var dialog = new FolderSyncJobDialog(_storage, _profiles, job);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var index = _jobs.FindIndex(item => item.Id == job.Id);
        _jobs[index] = dialog.Job;
        await _jobStore.SaveAsync(_jobs);
        RebuildJobList(dialog.Job.Id);
    }

    private async Task DeleteJobAsync()
    {
        var job = CurrentJob();
        if (job is null) return;
        if (MessageBox.Show(this, $"删除同步任务“{job.Name}”？\n\n不会删除本地或远端文件。", "删除同步任务",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _jobs.RemoveAll(item => item.Id == job.Id);
        await _jobStore.SaveAsync(_jobs);
        RebuildJobList();
    }

    private async Task AnalyzeAsync()
    {
        var job = CurrentJob();
        if (job is null) return;
        var profile = _profiles.FirstOrDefault(item => item.Id == job.ProfileId);
        if (profile is null)
        {
            MessageBox.Show(this, $"找不到同步任务引用的连接：{job.ProfileName}", "无法分析", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await RunOperationAsync("正在分析本地与远端...", async token =>
        {
            _plan = await FolderSyncAnalyzer.AnalyzeAsync(job, profile, _storage, _pageSize, _itemLimit, token);
            PopulateResults();
            _synchronize.Enabled = _plan.ActionCount > 0;
            _summary.Text = $"待执行 {_plan.ActionCount:N0} · 传输 {FileSizeFormatter.Format(_plan.TransferBytes)}";
        });
    }

    private void PopulateResults()
    {
        if (_plan is null) return;
        var filter = _filter.SelectedIndex;
        var items = _plan.Items.Where(item => filter switch
        {
            1 => item.Action != FolderSyncAction.None,
            2 => item.Change == FolderSyncChange.New,
            3 => item.Change == FolderSyncChange.Changed,
            4 => item.Change == FolderSyncChange.Deleted,
            5 => item.Change == FolderSyncChange.Excluded,
            _ => true
        }).ToArray();

        _results.BeginUpdate();
        try
        {
            _results.Items.Clear();
            foreach (var planItem in items)
            {
                var row = new ListViewItem(planItem.RelativePath);
                row.SubItems.Add(ChangeText(planItem.Change));
                row.SubItems.Add(ActionText(planItem.Action));
                row.SubItems.Add(planItem.Local is null ? "—" : FileSizeFormatter.Format(planItem.Local.Size));
                row.SubItems.Add(planItem.Remote is null ? "—" : FileSizeFormatter.Format(planItem.Remote.Size));
                row.SubItems.Add(planItem.Reason);
                if (planItem.Change == FolderSyncChange.Excluded) row.ForeColor = SystemColors.GrayText;
                else if (planItem.Action is FolderSyncAction.DeleteLocal or FolderSyncAction.DeleteRemote) row.ForeColor = Color.DarkRed;
                _results.Items.Add(row);
            }
        }
        finally { _results.EndUpdate(); }
        _results.Visible = true;
        _empty.Visible = false;
    }

    private async Task QueueSynchronizationAsync()
    {
        var job = CurrentJob();
        var plan = _plan;
        if (job is null || plan is null || plan.JobId != job.Id) return;
        var deleteCount = plan.Items.Count(item => item.Action is FolderSyncAction.DeleteLocal or FolderSyncAction.DeleteRemote);
        if (deleteCount > 0 && MessageBox.Show(
                this,
                $"计划包含 {deleteCount:N0} 个删除操作。删除将通过传输队列执行。\n\n确认继续？",
                "确认同步删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var profile = _profiles.First(item => item.Id == job.ProfileId);

        await RunOperationAsync("正在创建同步批次...", async token =>
        {
            var groups = plan.Items
                .Where(item => item.Action != FolderSyncAction.None)
                .GroupBy(item => ToTransferDirection(item.Action));
            foreach (var group in groups)
            {
                var batch = await _transferQueue.CreateBatchAsync(new TransferBatchRecord
                {
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Name = $"同步 {job.Name} - {DirectionText(group.Key)}",
                    Bucket = job.Bucket,
                    RootPath = job.LocalDirectory,
                    Direction = group.Key
                }, token);
                var tasks = group.Select(item => CreateTransferTask(job, profile, item, group.Key)).ToArray();
                foreach (var chunk in tasks.Chunk(256))
                    await _transferQueue.AddBatchTasksAsync(batch.Id, chunk, token);
                await _transferQueue.CompleteBatchDiscoveryAsync(batch.Id, cancellationToken: token);
            }
            QueuedTransfers = true;
            _synchronize.Enabled = false;
            _summary.Text = $"已加入队列 {plan.ActionCount:N0} 项";
            MessageBox.Show(this, $"已将 {plan.ActionCount:N0} 项同步操作加入可恢复传输队列。", "文件夹同步",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private TransferTaskRecord CreateTransferTask(
        FolderSyncJob job,
        ConnectionProfile profile,
        FolderSyncPlanItem item,
        TransferDirection direction)
    {
        var remoteKey = S3Path.Combine(job.Prefix, item.RelativePath);
        var localPath = LocalObjectPath.MapRelativeKey(job.LocalDirectory, item.RelativePath);
        var totalBytes = direction switch
        {
            TransferDirection.Upload => item.Local?.Size ?? 0,
            TransferDirection.Download => item.Remote?.Size ?? 0,
            _ => 0
        };
        return new TransferTaskRecord
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Direction = direction,
            Bucket = job.Bucket,
            ObjectKey = remoteKey,
            LocalPath = localPath,
            RelativePath = item.RelativePath,
            TotalBytes = totalBytes,
            StorageClass = profile.DefaultStorageClass,
            MaxAttempts = _maxAttempts,
            RetryBaseDelaySeconds = _retryDelaySeconds
        };
    }

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        _operation = new CancellationTokenSource();
        SetBusy(true, status);
        try
        {
            await action(_operation.Token);
        }
        catch (OperationCanceledException)
        {
            _summary.Text = "操作已取消";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "文件夹同步失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _summary.Text = "操作失败";
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            SetBusy(false, _summary.Text ?? string.Empty);
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _analyze.Enabled = !busy && CurrentJob() is not null;
        _add.Enabled = _edit.Enabled = _delete.Enabled = !busy;
        _stop.Enabled = busy;
        _jobList.Enabled = !busy;
        _summary.Text = status;
        UseWaitCursor = busy;
    }

    private FolderSyncJob? CurrentJob()
    {
        if (_jobList.SelectedItems.Count == 0 || _jobList.SelectedItems[0].Tag is not Guid id) return null;
        return _jobs.FirstOrDefault(job => job.Id == id);
    }

    private static Control CreatePathCard(Label caption, Label value, UiIconKind icon)
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0)
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var image = new PictureBox
        {
            Image = UiIcons.Create(icon, 22),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill
        };
        card.Controls.Add(image, 0, 0);
        card.SetRowSpan(image, 2);
        card.Controls.Add(caption, 1, 0);
        card.Controls.Add(value, 1, 1);
        return card;
    }

    private static Label PathCaption(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = SystemColors.GrayText,
        Padding = new Padding(7, 4, 4, 0)
    };

    private static Label PathValue() => new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Padding = new Padding(7, 1, 5, 3),
        Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold)
    };

    private static TransferDirection ToTransferDirection(FolderSyncAction action) => action switch
    {
        FolderSyncAction.Upload => TransferDirection.Upload,
        FolderSyncAction.Download => TransferDirection.Download,
        FolderSyncAction.DeleteRemote => TransferDirection.DeleteRemote,
        FolderSyncAction.DeleteLocal => TransferDirection.DeleteLocal,
        _ => throw new InvalidOperationException($"无法排队的同步操作：{action}")
    };

    private static string ChangeText(FolderSyncChange change) => change switch
    {
        FolderSyncChange.New => "新增",
        FolderSyncChange.Changed => "已更改",
        FolderSyncChange.Deleted => "已删除",
        FolderSyncChange.Unchanged => "相同",
        FolderSyncChange.Excluded => "已排除",
        _ => change.ToString()
    };

    private static string ActionText(FolderSyncAction action) => action switch
    {
        FolderSyncAction.None => "—",
        FolderSyncAction.Upload => "上传",
        FolderSyncAction.Download => "下载",
        FolderSyncAction.DeleteRemote => "删除远端",
        FolderSyncAction.DeleteLocal => "删除本地",
        _ => action.ToString()
    };

    private static string DirectionText(TransferDirection direction) => direction switch
    {
        TransferDirection.Upload => "上传",
        TransferDirection.Download => "下载",
        TransferDirection.DeleteRemote => "删除远端",
        TransferDirection.DeleteLocal => "删除本地",
        _ => direction.ToString()
    };
}
