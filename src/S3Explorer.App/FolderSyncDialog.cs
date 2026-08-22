using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class FolderSyncDialog : Form
{
    private enum ExclusionRuleKind
    {
        ExactFile,
        Extension,
        ParentDirectory
    }

    private readonly IFolderSyncJobStore _jobStore;
    private readonly IProfileStore _profileStore;
    private readonly IS3StorageService _storage;
    private readonly BucketDiscoveryCache _bucketCache;
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
        Name = "SyncJobList",
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
    };
    private readonly ColumnHeader _jobNameColumn = new() { Text = "名称" };
    private readonly ColumnHeader _jobDirectionColumn = new() { Text = "方向" };
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
        Name = "SyncResultsList",
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
        CheckBoxes = true,
        HeaderStyle = ColumnHeaderStyle.Clickable
    };
    private readonly ToolStrip _actions = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Bottom, ImageScalingSize = new Size(18, 18) };
    private readonly ToolStripButton _analyze = new("分析", UiIcons.Create(UiIconKind.Analyze, 18));
    private readonly ToolStripButton _synchronize = new("开始同步", UiIcons.Create(UiIconKind.Sync, 18)) { Enabled = false };
    private readonly ToolStripButton _stop = new("停止", UiIcons.Create(UiIconKind.Delete, 18)) { Enabled = false };
    private readonly ToolStripButton _add = new("添加任务", UiIcons.Create(UiIconKind.NewConnection, 18));
    private readonly ToolStripButton _edit = new("编辑任务", UiIcons.Create(UiIconKind.Settings, 18)) { Enabled = false };
    private readonly ToolStripButton _delete = new("删除任务", UiIcons.Create(UiIconKind.Delete, 18)) { Enabled = false };
    private readonly ToolStripButton _execution = new("执行结果", UiIcons.Create(UiIconKind.Info, 18)) { Name = "SyncExecutionReport", Enabled = false };
    private readonly ToolStripDropDownButton _selectionActions = new("选择项目", UiIcons.Create(UiIconKind.Properties, 18)) { Name = "SyncSelectionActions", Enabled = false };
    private readonly ToolStripComboBox _filter = new() { Name = "SyncResultFilter", DropDownStyle = ComboBoxStyle.DropDownList, Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripLabel _summary = new("尚未分析") { Alignment = ToolStripItemAlignment.Right };
    private readonly Label _empty = new()
    {
        Text = "尚未创建同步任务。点击“添加任务”设置本地文件夹与 S3 位置。",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText
    };
    private readonly Panel _body = new()
    {
        Name = "SyncResultsFrame",
        Dock = DockStyle.Fill,
        Margin = new Padding(14, 0, 14, 10),
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly ContextMenuStrip _resultMenu = new() { Name = "SyncResultContextMenu" };
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private List<FolderSyncJob> _jobs = [];
    private FolderSyncPlan? _plan;
    private FolderSyncPlanSelection? _selection;
    private Guid? _latestExecutionId;
    private CancellationTokenSource? _operation;
    private int _sortColumn;
    private bool _sortAscending = true;
    private bool _populatingResults;
    private bool _updatingRunHistory;

    public bool QueuedTransfers { get; private set; }

    public FolderSyncDialog(
        IFolderSyncJobStore jobStore,
        IProfileStore profileStore,
        IS3StorageService storage,
        PersistentTransferQueue transferQueue,
        AppSettings settings,
        ConnectionProfile? initialProfile = null,
        string? initialBucket = null,
        string? initialPrefix = null,
        BucketDiscoveryCache? bucketCache = null)
    {
        _jobStore = jobStore;
        _profileStore = profileStore;
        _storage = storage;
        _bucketCache = bucketCache ?? new BucketDiscoveryCache();
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
        Shown += async (_, _) => await RunCommandAsync("加载同步任务", InitializeAsync);
        FormClosing += (_, _) => _operation?.Cancel();
        FormClosed += (_, _) => _transferQueue.Changed -= TransferQueueChanged;
        _transferQueue.Changed += TransferQueueChanged;
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

        var route = new TableLayoutPanel
        {
            Name = "SyncRouteSummary",
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 10),
            ColumnCount = 3,
            RowCount = 1
        };
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        route.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        route.Controls.Add(CreatePathCard("SyncSourcePathCard", _sourceCaption, _source, UiIconKind.Folder), 0, 0);
        route.Controls.Add(_arrow, 1, 0);
        route.Controls.Add(CreatePathCard("SyncDestinationPathCard", _destinationCaption, _destination, UiIconKind.Bucket), 2, 0);

        _results.Columns.Add("文件", 300);
        _results.Columns.Add("扩展名", 80);
        _results.Columns.Add("状态", 80);
        _results.Columns.Add("操作", 90);
        _results.Columns.Add("本地大小", 100, HorizontalAlignment.Right);
        _results.Columns.Add("远端大小", 100, HorizontalAlignment.Right);
        _results.Columns.Add("原因", 240);
        _results.ContextMenuStrip = _resultMenu;
        BuildResultContextMenu();
        _body.Controls.Add(_empty);
        _body.Controls.Add(_results);

        _jobList.Columns.AddRange([_jobNameColumn, _jobDirectionColumn]);
        var navigation = new Panel
        {
            Name = "SyncTaskNavigation",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 10, 10),
            BackColor = SystemColors.Control
        };
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
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(route, 0, 0);
        content.Controls.Add(_body, 0, 1);

        var workspace = new SplitContainer
        {
            Name = "SyncWorkspace",
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 5
        };
        workspace.Size = new Size(ClientSize.Width, Math.Max(1, ClientSize.Height - header.Height - _actions.Height));
        workspace.Panel1MinSize = 220;
        workspace.Panel2MinSize = 500;
        workspace.SplitterDistance = 250;
        workspace.Panel1.Controls.Add(navigation);
        workspace.Panel2.Controls.Add(content);

        _filter.Items.AddRange(["全部", "已选择", "新增", "已更改", "已删除", "跳过"]);
        _filter.SelectedIndex = 0;
        _selectionActions.DropDownItems.Add("全选当前筛选", null, (_, _) => SetVisibleSelection(true));
        _selectionActions.DropDownItems.Add("全不选当前筛选", null, (_, _) => SetVisibleSelection(false));
        _selectionActions.DropDownItems.Add("反选当前筛选", null, (_, _) => InvertVisibleSelection());
        _actions.Items.AddRange([
            _analyze, _synchronize, _stop,
            new ToolStripSeparator(),
            _selectionActions,
            new ToolStripSeparator(),
            _add, _edit, _delete, _execution,
            new ToolStripSeparator(),
            _summary,
            new ToolStripLabel("筛选：") { Alignment = ToolStripItemAlignment.Right },
            _filter
        ]);

        Controls.Add(_actions);
        Controls.Add(workspace);
        Controls.Add(header);
        _results.Visible = false;
        ResizeJobColumns();
    }

    private void WireEvents()
    {
        _jobList.SelectedIndexChanged += (_, _) => SelectCurrentJob();
        _jobList.Resize += (_, _) => ResizeJobColumns();
        _jobList.FontChanged += (_, _) => ResizeJobColumns();
        _filter.SelectedIndexChanged += (_, _) => PopulateResults();
        _results.ColumnClick += (_, args) => SortResults(args.Column);
        _results.ItemCheck += ResultsOnItemCheck;
        _results.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Right) return;
            var hit = _results.HitTest(args.Location).Item;
            if (hit is null) return;
            _results.SelectedItems.Clear();
            hit.Selected = true;
        };
        _add.Click += async (_, _) => await RunCommandAsync("添加同步任务", AddJobAsync);
        _edit.Click += async (_, _) => await RunCommandAsync("编辑同步任务", EditJobAsync);
        _delete.Click += async (_, _) => await RunCommandAsync("删除同步任务", DeleteJobAsync);
        _analyze.Click += async (_, _) => await RunCommandAsync("分析同步任务", AnalyzeAsync);
        _synchronize.Click += async (_, _) => await RunCommandAsync("开始同步", QueueSynchronizationAsync);
        _execution.Click += (_, _) => ShowExecutionReport();
        _stop.Click += (_, _) => _operation?.Cancel();
    }

    private void BuildResultContextMenu()
    {
        var exact = new ToolStripMenuItem("排除此文件");
        var extension = new ToolStripMenuItem("排除此扩展名");
        var directory = new ToolStripMenuItem("排除此目录");
        exact.Click += async (_, _) => await RunCommandAsync(
            "添加排除规则", () => AddExclusionRuleAsync(ExclusionRuleKind.ExactFile));
        extension.Click += async (_, _) => await RunCommandAsync(
            "添加排除规则", () => AddExclusionRuleAsync(ExclusionRuleKind.Extension));
        directory.Click += async (_, _) => await RunCommandAsync(
            "添加排除规则", () => AddExclusionRuleAsync(ExclusionRuleKind.ParentDirectory));
        _resultMenu.Items.AddRange([exact, extension, directory]);
        _resultMenu.Opening += (_, args) =>
        {
            var item = SelectedResultItem();
            args.Cancel = item is null;
            exact.Enabled = item is not null;
            extension.Enabled = item is not null && Path.GetExtension(item.RelativePath).Length > 0;
            directory.Enabled = item is not null && item.RelativePath.Contains('/');
        };
    }

    private IEnumerable<FolderSyncPlanItem> VisibleItems()
    {
        if (_plan is null || _selection is null) return [];
        var filter = _filter.SelectedIndex;
        var values = _plan.Items.Where(item => filter switch
        {
            1 => _selection.IsSelected(item),
            2 => item.Change == FolderSyncChange.New,
            3 => item.Change == FolderSyncChange.Changed,
            4 => item.Change == FolderSyncChange.Deleted,
            5 => item.Action == FolderSyncAction.None,
            _ => true
        });
        return (_sortColumn, _sortAscending) switch
        {
            (0, true) => values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (0, false) => values.OrderByDescending(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (1, true) => values.OrderBy(item => Path.GetExtension(item.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (1, false) => values.OrderByDescending(item => Path.GetExtension(item.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (2, true) => values.OrderBy(item => item.Change).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (2, false) => values.OrderByDescending(item => item.Change).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (3, true) => values.OrderBy(item => item.Action).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (3, false) => values.OrderByDescending(item => item.Action).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (4, true) => values.OrderBy(item => item.Local?.Size ?? -1).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (4, false) => values.OrderByDescending(item => item.Local?.Size ?? -1).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (5, true) => values.OrderBy(item => item.Remote?.Size ?? -1).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (5, false) => values.OrderByDescending(item => item.Remote?.Size ?? -1).ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            (6, true) => values.OrderBy(item => item.Reason, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            _ => values.OrderByDescending(item => item.Reason, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void SortResults(int column)
    {
        if (_sortColumn == column) _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }
        PopulateResults();
    }

    private void ResultsOnItemCheck(object? sender, ItemCheckEventArgs args)
    {
        if (_populatingResults || _selection is null ||
            _results.Items[args.Index].Tag is not FolderSyncPlanItem item)
            return;
        if (item.Action == FolderSyncAction.None)
        {
            args.NewValue = CheckState.Unchecked;
            return;
        }
        _selection.Set(item, args.NewValue == CheckState.Checked);
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(new Action(UpdateSelectionSummary));
    }

    private void SetVisibleSelection(bool selected)
    {
        if (_selection is null) return;
        _selection.Set(VisibleItems().ToArray(), selected);
        PopulateResults();
    }

    private void InvertVisibleSelection()
    {
        if (_selection is null) return;
        _selection.Invert(VisibleItems().ToArray());
        PopulateResults();
    }

    private void UpdateSelectionSummary()
    {
        if (_plan is null || _selection is null) return;
        var selected = _selection.SelectedItems(_plan);
        var bytes = selected
            .Where(item => item.Action is FolderSyncAction.Upload or FolderSyncAction.Download)
            .Sum(item => item.SourceSize(item.Action == FolderSyncAction.Upload
                ? FolderSyncDirection.Upload
                : FolderSyncDirection.Download));
        _synchronize.Enabled = _operation is null && selected.Count > 0;
        _selectionActions.Enabled = _operation is null;
        _summary.Text = $"已选 {selected.Count:N0} / 待执行 {_plan.ActionCount:N0} · 传输 {FileSizeFormatter.Format(bytes)}";
    }

    private FolderSyncPlanItem? SelectedResultItem() =>
        _results.SelectedItems.Count == 1
            ? _results.SelectedItems[0].Tag as FolderSyncPlanItem
            : null;

    private async Task AddExclusionRuleAsync(ExclusionRuleKind kind)
    {
        var item = SelectedResultItem();
        var job = CurrentJob();
        if (item is null || job is null) return;
        var extension = Path.GetExtension(item.RelativePath);
        var slash = item.RelativePath.LastIndexOf('/');
        var rule = kind switch
        {
            ExclusionRuleKind.ExactFile => item.RelativePath,
            ExclusionRuleKind.Extension when extension.Length > 0 => $"*{extension};**/*{extension}",
            ExclusionRuleKind.ParentDirectory when slash > 0 => item.RelativePath[..slash] + "/**",
            _ => string.Empty
        };
        if (rule.Length == 0) return;
        if (job.ExclusionPatterns.Contains(rule, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"排除规则已存在：{rule}", "文件夹同步", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"将排除规则添加到任务“{job.Name}”：\n\n{rule}\n\n添加后需要重新分析。",
                "添加排除规则", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return;

        var updated = job with
        {
            ExclusionPatterns = job.ExclusionPatterns.Append(rule).ToArray()
        };
        updated.Validate();
        var index = _jobs.FindIndex(value => value.Id == job.Id);
        var proposed = _jobs.ToList();
        proposed[index] = updated;
        await _jobStore.SaveAsync(proposed);
        _jobs = proposed;
        RebuildJobList(updated.Id);
        _empty.Text = $"已添加排除规则 {rule}。请重新分析。";
        _summary.Text = "需要重新分析";
    }

    private async Task InitializeAsync()
    {
        _profiles = await _profileStore.LoadAsync();
        _jobs = (await _jobStore.LoadAsync()).ToList();
        RebuildJobList();
    }

    private async Task RunCommandAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            _summary.Text = $"{operation}已取消";
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, $"{operation}失败", operation, exception);
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
        _selection = null;
        var job = CurrentJob();
        var hasJob = job is not null;
        _latestExecutionId = job is null
            ? null
            : FolderSyncReportProjector.FindLatestExecutionId(job.Id, _transferQueue.Snapshot.Batches);
        _execution.Enabled = _operation is null && _latestExecutionId is not null;
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
        using var dialog = new FolderSyncJobDialog(_storage, _profiles, null, _initialProfile, _initialBucket, _initialPrefix, _bucketCache);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var proposed = _jobs.Append(dialog.Job).ToList();
        await _jobStore.SaveAsync(proposed);
        _jobs = proposed;
        RebuildJobList(dialog.Job.Id);
    }

    private async Task EditJobAsync()
    {
        var job = CurrentJob();
        if (job is null) return;
        using var dialog = new FolderSyncJobDialog(_storage, _profiles, job, bucketCache: _bucketCache);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var index = _jobs.FindIndex(item => item.Id == job.Id);
        var proposed = _jobs.ToList();
        proposed[index] = dialog.Job;
        await _jobStore.SaveAsync(proposed);
        _jobs = proposed;
        RebuildJobList(dialog.Job.Id);
    }

    private async Task DeleteJobAsync()
    {
        var job = CurrentJob();
        if (job is null) return;
        if (MessageBox.Show(this, $"删除同步任务“{job.Name}”？\n\n不会删除本地或远端文件。", "删除同步任务",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var proposed = _jobs.Where(item => item.Id != job.Id).ToList();
        await _jobStore.SaveAsync(proposed);
        _jobs = proposed;
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
            _selection = FolderSyncPlanSelection.SelectAllActions(_plan);
            PopulateResults();
            UpdateSelectionSummary();
        });
    }

    private void PopulateResults()
    {
        if (_plan is null || _selection is null) return;
        var items = VisibleItems().ToArray();

        _results.BeginUpdate();
        _populatingResults = true;
        try
        {
            _results.Items.Clear();
            foreach (var planItem in items)
            {
                var row = new ListViewItem(planItem.RelativePath)
                {
                    Tag = planItem,
                    Checked = _selection.IsSelected(planItem)
                };
                row.SubItems.Add(Path.GetExtension(planItem.RelativePath));
                row.SubItems.Add(ChangeText(planItem.Change));
                row.SubItems.Add(ActionText(planItem.Action));
                row.SubItems.Add(planItem.Local is null ? "—" : FileSizeFormatter.Format(planItem.Local.Size));
                row.SubItems.Add(planItem.Remote is null ? "—" : FileSizeFormatter.Format(planItem.Remote.Size));
                row.SubItems.Add(planItem.Reason);
                if (planItem.Action == FolderSyncAction.None) row.ForeColor = SystemColors.GrayText;
                else if (planItem.Action is FolderSyncAction.DeleteLocal or FolderSyncAction.DeleteRemote) row.ForeColor = Color.DarkRed;
                _results.Items.Add(row);
            }
        }
        finally
        {
            _populatingResults = false;
            _results.EndUpdate();
        }
        _results.Visible = true;
        _empty.Visible = false;
        UpdateSelectionSummary();
    }

    private async Task QueueSynchronizationAsync()
    {
        var job = CurrentJob();
        var plan = _plan;
        var selection = _selection;
        if (job is null || plan is null || selection is null) return;
        if (!plan.IsValidFor(job, DateTimeOffset.UtcNow, out var invalidReason))
        {
            _plan = null;
            _selection = null;
            _synchronize.Enabled = false;
            _selectionActions.Enabled = false;
            _results.Visible = false;
            _empty.Visible = true;
            _empty.Text = $"{invalidReason} 请重新分析后再同步。";
            _summary.Text = "需要重新分析";
            MessageBox.Show(this, _empty.Text, "同步计划已失效", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var selectedItems = selection.SelectedItems(plan);
        if (selectedItems.Count == 0)
        {
            MessageBox.Show(this, "请至少勾选一个待执行项目。", "文件夹同步", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var deleteCount = selectedItems.Count(item => item.Action is FolderSyncAction.DeleteLocal or FolderSyncAction.DeleteRemote);
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
            var executionId = Guid.NewGuid();
            var groups = selectedItems.GroupBy(item => ToTransferDirection(item.Action));
            foreach (var group in groups)
            {
                var batch = await _transferQueue.CreateBatchAsync(new TransferBatchRecord
                {
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Name = $"同步 {job.Name} - {DirectionText(group.Key)}",
                    Bucket = job.Bucket,
                    RootPath = job.LocalDirectory,
                    Direction = group.Key,
                    FolderSyncJobId = job.Id,
                    FolderSyncExecutionId = executionId
                }, token);
                var tasks = group.Select(item => CreateTransferTask(job, profile, item, group.Key)).ToArray();
                foreach (var chunk in tasks.Chunk(256))
                    await _transferQueue.AddBatchTasksAsync(batch.Id, chunk, token);
                await _transferQueue.CompleteBatchDiscoveryAsync(batch.Id, cancellationToken: token);
            }
            QueuedTransfers = true;
            _latestExecutionId = executionId;
            _execution.Enabled = true;
            _synchronize.Enabled = false;
            _selectionActions.Enabled = false;
            _summary.Text = $"已加入队列 {selectedItems.Count:N0} 项";
            MessageBox.Show(this, $"已将 {selectedItems.Count:N0} 项同步操作加入可恢复传输队列。", "文件夹同步",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _plan = null;
            _selection = null;
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
        _add.Enabled = !busy;
        _edit.Enabled = _delete.Enabled = !busy && CurrentJob() is not null;
        _selectionActions.Enabled = !busy && _plan is not null;
        _execution.Enabled = !busy && _latestExecutionId is not null;
        _stop.Enabled = busy;
        _jobList.Enabled = !busy;
        _summary.Text = status;
        UseWaitCursor = busy;
    }

    private void ShowExecutionReport()
    {
        var job = CurrentJob();
        if (job is null || _latestExecutionId is not Guid executionId) return;
        using var dialog = new FolderSyncExecutionDialog(_transferQueue, job, executionId);
        dialog.ShowDialog(this);
    }

    private void TransferQueueChanged(object? sender, TransferQueueChangedEventArgs args)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke(new Action(async () => await RefreshExecutionStateAsync()));
    }

    private async Task RefreshExecutionStateAsync()
    {
        if (_updatingRunHistory) return;
        var job = CurrentJob();
        if (job is null) return;
        _latestExecutionId = FolderSyncReportProjector.FindLatestExecutionId(job.Id, _transferQueue.Snapshot.Batches);
        _execution.Enabled = _operation is null && _latestExecutionId is not null;
        if (_latestExecutionId is not Guid executionId) return;

        FolderSyncExecutionReport report;
        try
        {
            report = FolderSyncReportProjector.Project(job, executionId, _transferQueue.Snapshot);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (!report.IsFinished || report.CompletedAt is not DateTimeOffset completedAt || job.LastRunAt >= completedAt)
            return;

        _updatingRunHistory = true;
        try
        {
            var index = _jobs.FindIndex(item => item.Id == job.Id);
            if (index < 0) return;
            var proposed = _jobs.ToList();
            proposed[index] = job with { LastRunAt = completedAt };
            await _jobStore.SaveAsync(proposed);
            _jobs = proposed;
        }
        finally
        {
            _updatingRunHistory = false;
        }
    }

    private FolderSyncJob? CurrentJob()
    {
        if (_jobList.SelectedItems.Count == 0 || _jobList.SelectedItems[0].Tag is not Guid id) return null;
        return _jobs.FirstOrDefault(job => job.Id == id);
    }

    private void ResizeJobColumns()
    {
        var available = Math.Max(160, _jobList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        var direction = Math.Min(
            Math.Max(76, TextRenderer.MeasureText("本地 → S3", _jobList.Font).Width + 14),
            available / 2);
        _jobDirectionColumn.Width = direction;
        _jobNameColumn.Width = Math.Max(80, available - direction);
    }

    private static Control CreatePathCard(string name, Label caption, Label value, UiIconKind icon)
    {
        var card = new TableLayoutPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(8, 5, 8, 5)
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var image = new PictureBox
        {
            Image = UiIcons.Create(icon, 20),
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
        AutoEllipsis = true,
        Padding = new Padding(4, 1, 4, 0)
    };

    private static Label PathValue() => new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(4, 0, 4, 1),
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
