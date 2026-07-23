using System.ComponentModel;
using System.Diagnostics;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record BucketNodeTag(ConnectionProfile Profile, string Bucket);
internal sealed record LoadMoreTag;

internal sealed class MainForm : Form
{
    private readonly IProfileStore _profileStore;
    private readonly IS3StorageService _storage;
    private readonly AppSettingsStore _settingsStore;
    private readonly SimpleFileLogger _logger;

    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(20, 20) };
    private readonly ToolStrip _addressStrip = new() { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(18, 18) };
    private readonly ToolStripTextBox _address = new() { AutoSize = false, Width = 620 };
    private readonly ToolStripTextBox _search = new() { AutoSize = false, Width = 220, ToolTipText = "过滤当前已加载列表（Ctrl+F）" };
    private readonly SplitContainer _outerSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
    private readonly SplitContainer _mainSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1 };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
    private readonly ListView _objects = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        HideSelection = false,
        GridLines = false,
        AllowDrop = true
    };
    private readonly TransferQueueControl _transfers = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _connectionStatus = new("未连接");
    private readonly ToolStripStatusLabel _pathStatus = new("s3://");
    private readonly ToolStripStatusLabel _objectStatus = new("0 个对象");
    private readonly ToolStripStatusLabel _selectionStatus = new("已选择 0 个");
    private readonly ToolStripStatusLabel _requestStatus = new("空闲") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly ToolStripStatusLabel _uploadSpeed = new("↑ 0 B/s");
    private readonly ToolStripStatusLabel _downloadSpeed = new("↓ 0 B/s");
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _speedTimer = new() { Interval = 1000 };
    private readonly List<S3ObjectEntry> _loadedItems = [];
    private readonly List<S3Location> _history = [];
    private readonly Dictionary<string, ToolStripItem> _commands = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private AppSettings _settings = new();
    private ConnectionProfile? _currentProfile;
    private string? _currentBucket;
    private string _currentPrefix = string.Empty;
    private string? _continuationToken;
    private bool _hasMore;
    private int _historyIndex = -1;
    private int _sortColumn;
    private bool _sortAscending = true;
    private long _navigationRevision;
    private CancellationTokenSource? _navigationCancellation;
    private bool _closing;

    public MainForm(IProfileStore profileStore, IS3StorageService storage, AppSettingsStore settingsStore, SimpleFileLogger logger)
    {
        _profileStore = profileStore;
        _storage = storage;
        _settingsStore = settingsStore;
        _logger = logger;

        Text = "S3 Explorer";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 780);
        MinimumSize = new Size(960, 600);
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildAddressBar();
        BuildBody();
        BuildStatus();
        WireEvents();

        Controls.Add(_outerSplit);
        Controls.Add(_addressStrip);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        Shown += async (_, _) => await InitializeAsync();
        FormClosing += MainForm_FormClosing;
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add(Command("new-connection", "新建连接...", (_, _) => NewConnection(), Keys.Control | Keys.N));
        file.DropDownItems.Add(Command("edit-connection", "编辑当前连接...", (_, _) => EditCurrentConnection()));
        file.DropDownItems.Add(Command("delete-connection", "删除当前连接", async (_, _) => await DeleteCurrentConnectionAsync()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Command("connect", "连接", async (_, _) => await ConnectSelectedAsync()));
        file.DropDownItems.Add(Command("disconnect", "断开连接", (_, _) => Disconnect()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Unsupported("导入连接..."));
        file.DropDownItems.Add(Unsupported("导出连接..."));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("退出", null, (_, _) => Close()));

        var edit = new ToolStripMenuItem("编辑(&E)");
        edit.DropDownItems.Add(new ToolStripMenuItem("全选", null, (_, _) => SelectAllObjects(), Keys.Control | Keys.A));
        edit.DropDownItems.Add(Command("copy-path", "复制对象路径", (_, _) => CopySelectedPaths()));
        edit.DropDownItems.Add(Command("copy-url", "复制对象 URL", (_, _) => CopySelectedUrls()));
        edit.DropDownItems.Add(Command("copy-key", "复制对象 Key", (_, _) => CopySelectedKeys()));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Command("rename", "重命名", async (_, _) => await RenameSelectedAsync(), Keys.F2));
        edit.DropDownItems.Add(Command("delete-object", "删除", async (_, _) => await DeleteSelectedAsync(), Keys.Delete));
        edit.DropDownItems.Add(Command("properties", "属性", async (_, _) => await ShowPropertiesAsync(), Keys.Alt | Keys.Enter));

        var view = new ToolStripMenuItem("查看(&V)");
        view.DropDownItems.Add(Command("refresh", "刷新", async (_, _) => await RefreshAsync(), Keys.F5));
        view.DropDownItems.Add(Unsupported("大图标"));
        view.DropDownItems.Add(Unsupported("小图标"));
        var details = new ToolStripMenuItem("详细信息") { Checked = true, CheckOnClick = false };
        view.DropDownItems.Add(details);
        var showTransfers = new ToolStripMenuItem("显示传输队列") { Checked = true, CheckOnClick = true };
        showTransfers.CheckedChanged += (_, _) => SetTransferVisibility(showTransfers.Checked);
        _commands["show-transfers"] = showTransfers;
        view.DropDownItems.Add(showTransfers);
        view.DropDownItems.Add(Unsupported("显示隐藏对象"));
        view.DropDownItems.Add(Unsupported("显示版本"));
        view.DropDownItems.Add(Unsupported("列设置..."));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Command("back", "返回", (_, _) => NavigateHistory(-1), Keys.Alt | Keys.Left));
        view.DropDownItems.Add(Command("forward", "前进", (_, _) => NavigateHistory(1), Keys.Alt | Keys.Right));
        view.DropDownItems.Add(Command("up", "上一级", async (_, _) => await NavigateUpAsync(), Keys.Alt | Keys.Up));

        var bucket = new ToolStripMenuItem("Bucket(&B)");
        bucket.DropDownItems.Add(Command("create-bucket", "新建 Bucket...", async (_, _) => await CreateBucketAsync()));
        bucket.DropDownItems.Add(Command("delete-bucket", "删除 Bucket...", async (_, _) => await DeleteBucketAsync()));
        bucket.DropDownItems.Add(Unsupported("Bucket 属性..."));
        foreach (var text in new[]
                 {
                     "Bucket 权限...", "Bucket Policy...", "CORS 配置...", "版本控制...", "生命周期规则...",
                     "Public Access Block...", "Object Ownership...", "Object Lock...", "清空 Bucket..."
                 })
            bucket.DropDownItems.Add(Unsupported(text));
        bucket.DropDownItems.Add(new ToolStripSeparator());
        bucket.DropDownItems.Add(Command("refresh-buckets", "刷新 Bucket 列表", async (_, _) => await ReloadBucketsAsync()));

        var objects = new ToolStripMenuItem("对象(&O)");
        objects.DropDownItems.Add(Command("upload-file", "上传文件...", async (_, _) => await UploadFilesAsync(), Keys.Control | Keys.U));
        objects.DropDownItems.Add(Command("upload-folder", "上传文件夹...", async (_, _) => await UploadFolderAsync(), Keys.Control | Keys.Shift | Keys.U));
        objects.DropDownItems.Add(Command("download", "下载...", async (_, _) => await DownloadSelectedAsync(), Keys.Control | Keys.D));
        objects.DropDownItems.Add(Command("create-folder", "新建文件夹...", async (_, _) => await CreateFolderAsync()));
        objects.DropDownItems.Add(new ToolStripSeparator());
        objects.DropDownItems.Add(Command("copy-object", "复制...", async (_, _) => await CopyOrMoveSelectedAsync(false)));
        objects.DropDownItems.Add(Command("move-object", "移动...", async (_, _) => await CopyOrMoveSelectedAsync(true)));
        objects.DropDownItems.Add(Command("rename-object", "重命名...", async (_, _) => await RenameSelectedAsync()));
        objects.DropDownItems.Add(Command("delete-object-menu", "删除", async (_, _) => await DeleteSelectedAsync()));
        objects.DropDownItems.Add(Command("properties-menu", "属性...", async (_, _) => await ShowPropertiesAsync()));
        objects.DropDownItems.Add(Command("metadata", "Metadata...", async (_, _) => await ShowPropertiesAsync()));
        foreach (var text in new[] { "权限...", "更改存储类型...", "公开访问", "取消公开访问", "恢复历史版本..." })
            objects.DropDownItems.Add(Unsupported(text));
        objects.DropDownItems.Add(Command("presign", "生成预签名 URL...", (_, _) => ShowPresignedUrl()));

        var tools = new ToolStripMenuItem("工具(&T)");
        tools.DropDownItems.Add(Command("transfer-queue", "传输队列", (_, _) => SetTransferVisibility(true)));
        tools.DropDownItems.Add(Command("failed-transfers", "失败任务", (_, _) => SetTransferVisibility(true)));
        tools.DropDownItems.Add(Unsupported("未完成的分片上传"));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Command("settings", "选项...", async (_, _) => await ShowSettingsAsync()));
        tools.DropDownItems.Add(Command("logs", "查看日志", (_, _) => OpenLog()));
        tools.DropDownItems.Add(Command("clear-cache", "清理缓存", (_, _) => MessageBox.Show(this, "当前版本没有持久对象缓存。", "清理缓存")));
        tools.DropDownItems.Add(Command("diagnostics", "网络诊断", async (_, _) => await TestCurrentConnectionAsync()));
        tools.DropDownItems.Add(Unsupported("检查更新"));

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add(Command("help", "使用说明", (_, _) => OpenProjectFile("README.md")));
        help.DropDownItems.Add(new ToolStripMenuItem("快捷键", null, (_, _) => ShowShortcuts()));
        help.DropDownItems.Add(Unsupported("打开项目主页"));
        help.DropDownItems.Add(Unsupported("报告问题"));
        help.DropDownItems.Add(new ToolStripMenuItem("关于", null, (_, _) =>
            MessageBox.Show(this, "S3 Explorer v0.1\n\n原生 Windows S3 / S3-compatible 对象存储管理工具。\n.NET 10 · WinForms · AWS SDK for .NET", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.AddRange([file, edit, view, bucket, objects, tools, help]);
    }

    private void BuildToolbar()
    {
        AddToolbarButton("new-connection", "新建连接", "＋", (_, _) => NewConnection());
        AddToolbarButton("connect-toolbar", "连接/断开", "⇄", async (_, _) =>
        {
            if (_currentProfile is null) await ConnectSelectedAsync(); else Disconnect();
        });
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("back-toolbar", "返回 (Alt+Left)", "←", (_, _) => NavigateHistory(-1));
        AddToolbarButton("forward-toolbar", "前进 (Alt+Right)", "→", (_, _) => NavigateHistory(1));
        AddToolbarButton("up-toolbar", "上一级 (Alt+Up)", "↑", async (_, _) => await NavigateUpAsync());
        AddToolbarButton("refresh-toolbar", "刷新 (F5)", "↻", async (_, _) => await RefreshAsync());
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("create-bucket-toolbar", "新建 Bucket", "▣", async (_, _) => await CreateBucketAsync());
        AddToolbarButton("create-folder-toolbar", "新建文件夹", "□", async (_, _) => await CreateFolderAsync());

        var upload = new ToolStripDropDownButton("上传", UiIcons.Create("⇧")) { ToolTipText = "上传文件或文件夹" };
        upload.DropDownItems.Add("上传文件...", null, async (_, _) => await UploadFilesAsync());
        upload.DropDownItems.Add("上传文件夹...", null, async (_, _) => await UploadFolderAsync());
        _toolbar.Items.Add(upload);
        _commands["upload-toolbar"] = upload;

        AddToolbarButton("download-toolbar", "下载", "⇩", async (_, _) => await DownloadSelectedAsync());
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("copy-toolbar", "复制", "⧉", async (_, _) => await CopyOrMoveSelectedAsync(false));
        AddToolbarButton("move-toolbar", "移动", "⇥", async (_, _) => await CopyOrMoveSelectedAsync(true));
        AddToolbarButton("delete-toolbar", "删除", "×", async (_, _) => await DeleteSelectedAsync());
        AddToolbarButton("properties-toolbar", "属性", "ⓘ", async (_, _) => await ShowPropertiesAsync());
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("transfers-toolbar", "传输队列", "≡", (_, _) => SetTransferVisibility(!_outerSplit.Panel2Collapsed));
        AddToolbarButton("settings-toolbar", "设置", "⚙", async (_, _) => await ShowSettingsAsync());
        _toolbar.Dock = DockStyle.Top;
    }

    private void BuildAddressBar()
    {
        var back = new ToolStripButton(UiIcons.Create("←")) { ToolTipText = "返回" };
        back.Click += (_, _) => NavigateHistory(-1);
        var forward = new ToolStripButton(UiIcons.Create("→")) { ToolTipText = "前进" };
        forward.Click += (_, _) => NavigateHistory(1);
        var up = new ToolStripButton(UiIcons.Create("↑")) { ToolTipText = "上一级" };
        up.Click += async (_, _) => await NavigateUpAsync();
        var refresh = new ToolStripButton(UiIcons.Create("↻")) { ToolTipText = "刷新" };
        refresh.Click += async (_, _) => await RefreshAsync();
        _address.ToolTipText = "输入 s3://<profile>/<bucket>/<prefix> 并按 Enter";
        _search.Text = string.Empty;
        _search.Alignment = ToolStripItemAlignment.Right;
        var searchLabel = new ToolStripLabel("搜索：") { Alignment = ToolStripItemAlignment.Right };
        _addressStrip.Items.AddRange([back, forward, up, new ToolStripSeparator(), _address, refresh, searchLabel, _search]);
        _addressStrip.Dock = DockStyle.Top;
    }

    private void BuildBody()
    {
        _objects.Columns.Add("名称", 320);
        _objects.Columns.Add("大小", 110, HorizontalAlignment.Right);
        _objects.Columns.Add("类型", 120);
        _objects.Columns.Add("修改时间", 165);
        _objects.Columns.Add("存储类型", 120);

        var root = new TreeNode("Accounts") { Name = "Accounts", ImageIndex = 0, SelectedImageIndex = 0 };
        _tree.Nodes.Add(root);

        _mainSplit.Panel1.Controls.Add(_tree);
        _mainSplit.Panel2.Controls.Add(_objects);
        _outerSplit.Panel1.Controls.Add(_mainSplit);
        _outerSplit.Panel2.Controls.Add(_transfers);
        _outerSplit.SplitterWidth = 6;
        _mainSplit.SplitterWidth = 6;
    }

    private void BuildStatus()
    {
        _status.Items.AddRange([
            _connectionStatus,
            new ToolStripStatusLabel("|"),
            _pathStatus,
            new ToolStripStatusLabel("|"),
            _objectStatus,
            new ToolStripStatusLabel("|"),
            _selectionStatus,
            _requestStatus,
            _uploadSpeed,
            _downloadSpeed
        ]);
        _status.Dock = DockStyle.Bottom;
        Controls.Add(_status);
    }

    private void WireEvents()
    {
        _tree.NodeMouseDoubleClick += async (_, args) =>
        {
            if (args.Node.Tag is ConnectionProfile profile)
                await LoadBucketsAsync(profile, args.Node);
            else if (args.Node.Tag is BucketNodeTag bucket)
                await NavigateAsync(bucket.Profile, bucket.Bucket, string.Empty, true);
        };
        _tree.AfterSelect += async (_, args) =>
        {
            UpdateCommandStates();
            if (args.Node.Tag is ConnectionProfile profile)
                ShowConnectionSummary(profile, args.Node);
            else if (args.Node.Tag is BucketNodeTag bucket)
                await NavigateAsync(bucket.Profile, bucket.Bucket, string.Empty, true);
        };

        _objects.ItemActivate += async (_, _) =>
        {
            if (_objects.SelectedItems.Count == 0) return;
            if (_objects.SelectedItems[0].Tag is LoadMoreTag)
                await LoadObjectsPageAsync(false);
            else if (_objects.SelectedItems[0].Tag is S3ObjectEntry entry)
            {
                if (entry.IsDirectory)
                    await NavigateAsync(_currentProfile!, _currentBucket!, entry.Key, true);
                else
                    await ShowPropertiesAsync();
            }
        };
        _objects.SelectedIndexChanged += (_, _) =>
        {
            UpdateSelectionStatus();
            UpdateCommandStates();
        };
        _objects.ColumnClick += (_, args) =>
        {
            if (_sortColumn == args.Column) _sortAscending = !_sortAscending;
            else { _sortColumn = args.Column; _sortAscending = true; }
            ApplyFilterAndSort();
        };
        _objects.DragEnter += (_, args) =>
        {
            if (args.Data?.GetDataPresent(DataFormats.FileDrop) == true && _currentBucket is not null)
                args.Effect = DragDropEffects.Copy;
        };
        _objects.DragDrop += async (_, args) =>
        {
            if (args.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            await UploadPathsAsync(paths);
        };
        _objects.KeyDown += async (_, args) =>
        {
            if (args.KeyCode == Keys.Delete) { args.Handled = true; await DeleteSelectedAsync(); }
            else if (args.KeyCode == Keys.F2) { args.Handled = true; await RenameSelectedAsync(); }
            else if (args.KeyCode == Keys.Enter && args.Alt) { args.Handled = true; await ShowPropertiesAsync(); }
        };

        _address.KeyDown += async (_, args) =>
        {
            if (args.KeyCode != Keys.Enter) return;
            args.Handled = true;
            await NavigateAddressAsync();
        };
        _search.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilterAndSort();
        };
        _speedTimer.Tick += (_, _) =>
        {
            _uploadSpeed.Text = $"↑ {FileSizeFormatter.Format((long)_transfers.UploadBytesPerSecond)}/s";
            _downloadSpeed.Text = $"↓ {FileSizeFormatter.Format((long)_transfers.DownloadBytesPerSecond)}/s";
        };
        _transfers.TransferCompleted += async (_, _) =>
        {
            if (!_closing && _currentProfile is not null && _currentBucket is not null)
                await RefreshAsync();
        };

        KeyDown += async (_, args) =>
        {
            if (args.Control && args.KeyCode == Keys.L) { _address.Focus(); _address.SelectAll(); args.Handled = true; }
            else if (args.Control && args.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); args.Handled = true; }
            else if (args.KeyCode == Keys.Escape) _objects.SelectedItems.Cast<ListViewItem>().ToList().ForEach(item => item.Selected = false);
            else if (args.KeyCode == Keys.F5) { args.Handled = true; await RefreshAsync(); }
        };
    }

    private async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        ApplySettings();
        _profiles = await _profileStore.LoadAsync();
        PopulateProfiles();
        _speedTimer.Start();
        UpdateCommandStates();
    }

    private void ApplySettings()
    {
        if (_settings.RememberLayout)
        {
            Size = new Size(Math.Max(MinimumSize.Width, _settings.WindowWidth), Math.Max(MinimumSize.Height, _settings.WindowHeight));
            if (_settings.WindowX >= 0 && _settings.WindowY >= 0)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(_settings.WindowX, _settings.WindowY);
            }
            _mainSplit.SplitterDistance = Math.Clamp(_settings.LeftPanelWidth, 180, Math.Max(180, Width - 400));
            foreach (var pair in _objects.Columns.Cast<ColumnHeader>().Zip(_settings.ObjectColumnWidths))
                pair.First.Width = Math.Clamp(pair.Second, 50, 900);
            _sortColumn = Math.Clamp(_settings.SortColumn, 0, _objects.Columns.Count - 1);
            _sortAscending = _settings.SortAscending;
        }
        SetTransferVisibility(_settings.ShowTransfers);
        _transfers.SetConcurrency(_settings.ConcurrentTransfers);
    }

    private void PopulateProfiles()
    {
        var root = _tree.Nodes[0];
        root.Nodes.Clear();
        foreach (var profile in _profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            var node = new TreeNode(profile.Name)
            {
                Tag = profile,
                ToolTipText = $"{profile.Endpoint}\n{profile.Region}\n未连接"
            };
            node.Nodes.Add(new TreeNode("(双击连接)") { ForeColor = SystemColors.GrayText });
            root.Nodes.Add(node);
        }
        root.Expand();
    }

    private void NewConnection()
    {
        using var dialog = new ConnectionDialog(_storage);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _profiles = _profiles.Append(dialog.Profile).ToArray();
        SaveProfilesAndRefresh();
    }

    private void EditCurrentConnection()
    {
        var profile = SelectedTreeProfile();
        if (profile is null) return;
        using var dialog = new ConnectionDialog(_storage, profile);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _profiles = _profiles.Select(item => item.Id == profile.Id ? dialog.Profile : item).ToArray();
        if (_currentProfile?.Id == profile.Id) _currentProfile = dialog.Profile;
        SaveProfilesAndRefresh();
    }

    private async Task DeleteCurrentConnectionAsync()
    {
        var profile = SelectedTreeProfile();
        if (profile is null) return;
        if (MessageBox.Show(this,
                $"确定删除连接“{profile.Name}”吗？\n\n这只删除本地配置，不会删除任何远程 Bucket 或对象。",
                "删除连接", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _profiles = _profiles.Where(item => item.Id != profile.Id).ToArray();
        if (_currentProfile?.Id == profile.Id) Disconnect();
        await _profileStore.SaveAsync(_profiles);
        PopulateProfiles();
    }

    private void SaveProfilesAndRefresh()
    {
        _profileStore.SaveAsync(_profiles).GetAwaiter().GetResult();
        PopulateProfiles();
    }

    private async Task ConnectSelectedAsync()
    {
        var node = _tree.SelectedNode;
        var profile = SelectedTreeProfile();
        if (profile is null)
        {
            MessageBox.Show(this, "请先选择一个连接。", "连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        node ??= _tree.Nodes[0].Nodes.Cast<TreeNode>().FirstOrDefault(item => item.Tag is ConnectionProfile p && p.Id == profile.Id);
        if (node is not null) await LoadBucketsAsync(profile, node);
    }

    private async Task LoadBucketsAsync(ConnectionProfile profile, TreeNode profileNode)
    {
        CancelNavigation();
        using var cancellation = new CancellationTokenSource();
        _navigationCancellation = cancellation;
        SetBusy($"正在连接 {profile.Name}...");
        try
        {
            var buckets = await _storage.ListBucketsAsync(profile, cancellation.Token);
            _currentProfile = profile;
            _currentBucket = null;
            _currentPrefix = string.Empty;
            Text = $"S3 Explorer - {profile.Name}";
            _connectionStatus.Text = $"已连接：{profile.Name}";
            profileNode.Nodes.Clear();
            foreach (var bucket in buckets)
            {
                profileNode.Nodes.Add(new TreeNode(bucket.Name)
                {
                    Tag = new BucketNodeTag(profile, bucket.Name),
                    ToolTipText = bucket.Region is null ? bucket.Name : $"{bucket.Name}\nRegion: {bucket.Region}"
                });
            }
            if (buckets.Count == 0)
                profileNode.Nodes.Add(new TreeNode("(没有 Bucket)") { ForeColor = SystemColors.GrayText });
            profileNode.Expand();
            ShowConnectionSummary(profile, profileNode, buckets.Count);
            _logger.Info($"Connected profile={profile.Name} endpoint={profile.Endpoint} buckets={buckets.Count}");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _logger.Error($"Connect failed profile={profile.Name} endpoint={profile.Endpoint}", exception);
            ErrorDialog.ShowException(this, "连接失败", "列出 Bucket", exception, profile.Endpoint);
        }
        finally
        {
            SetIdle();
            UpdateCommandStates();
        }
    }

    private void Disconnect()
    {
        CancelNavigation();
        _currentProfile = null;
        _currentBucket = null;
        _currentPrefix = string.Empty;
        _loadedItems.Clear();
        _objects.Items.Clear();
        Text = "S3 Explorer";
        _connectionStatus.Text = "未连接";
        _pathStatus.Text = "s3://";
        _address.Text = string.Empty;
        UpdateCommandStates();
    }

    private void ShowConnectionSummary(ConnectionProfile profile, TreeNode node, int? bucketCount = null)
    {
        _objects.BeginUpdate();
        try
        {
            _objects.Items.Clear();
            _loadedItems.Clear();
            AddSummaryItem("连接名称", profile.Name);
            AddSummaryItem("Endpoint", profile.Endpoint);
            AddSummaryItem("Region", profile.Region);
            AddSummaryItem("服务类型", profile.ServiceType.ToString());
            AddSummaryItem("Bucket 数量", bucketCount?.ToString() ?? Math.Max(0, node.Nodes.Count).ToString());
            AddSummaryItem("当前状态", _currentProfile?.Id == profile.Id ? "已连接" : "未连接");
            AddSummaryItem("凭据存储", "SecretKey 与 SessionToken 使用 DPAPI CurrentUser 加密");
        }
        finally { _objects.EndUpdate(); }
        _objectStatus.Text = "连接摘要";
        _selectionStatus.Text = string.Empty;
    }

    private void AddSummaryItem(string name, string value)
    {
        var item = new ListViewItem(name);
        item.SubItems.Add(string.Empty);
        item.SubItems.Add(value);
        item.SubItems.Add(string.Empty);
        item.SubItems.Add(string.Empty);
        _objects.Items.Add(item);
    }

    private async Task NavigateAsync(ConnectionProfile profile, string bucket, string prefix, bool addHistory)
    {
        _currentProfile = profile;
        _currentBucket = bucket;
        _currentPrefix = S3Path.NormalizePrefix(prefix);
        Text = $"S3 Explorer - {profile.Name}";
        var location = new S3Location(profile.Name, bucket, _currentPrefix);
        _address.Text = location.ToString();
        _pathStatus.Text = location.ToString();
        _connectionStatus.Text = $"已连接：{profile.Name}";
        if (addHistory)
            AddHistory(location);
        await LoadObjectsPageAsync(true);
        UpdateCommandStates();
    }

    private async Task LoadObjectsPageAsync(bool reset)
    {
        if (_currentProfile is null || _currentBucket is null) return;

        if (reset)
        {
            CancelNavigation();
            _navigationCancellation = new CancellationTokenSource();
            _loadedItems.Clear();
            _continuationToken = null;
            _hasMore = false;
            _objects.Items.Clear();
        }

        var revision = ++_navigationRevision;
        var cancellation = _navigationCancellation ??= new CancellationTokenSource();
        SetBusy("正在加载对象...");
        try
        {
            var page = await _storage.ListObjectsAsync(
                _currentProfile,
                _currentBucket,
                _currentPrefix,
                reset ? null : _continuationToken,
                1000,
                cancellation.Token);
            if (revision != _navigationRevision || cancellation.IsCancellationRequested)
                return;

            _loadedItems.AddRange(page.Items.Where(item =>
                !_loadedItems.Any(existing => string.Equals(existing.Key, item.Key, StringComparison.Ordinal) && existing.IsDirectory == item.IsDirectory)));
            _continuationToken = page.ContinuationToken;
            _hasMore = page.HasMore;
            ApplyFilterAndSort();
            _logger.Info($"ListObjects profile={_currentProfile.Name} bucket={_currentBucket} prefix={_currentPrefix} count={page.Items.Count} hasMore={page.HasMore}");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _logger.Error($"ListObjects failed bucket={_currentBucket} prefix={_currentPrefix}", exception);
            ErrorDialog.ShowException(this, "加载失败", "列出对象", exception, CurrentLocationText());
        }
        finally
        {
            SetIdle();
        }
    }

    private void ApplyFilterAndSort()
    {
        var query = _search.Text.Trim();
        IEnumerable<S3ObjectEntry> items = _loadedItems;
        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(item =>
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(item.Name).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                FileSizeFormatter.Format(item.Size).Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        IOrderedEnumerable<S3ObjectEntry> ordered = items.OrderByDescending(item => item.IsDirectory);
        Func<S3ObjectEntry, object> selector = _sortColumn switch
        {
            1 => item => item.Size,
            3 => item => item.LastModified ?? DateTimeOffset.MinValue,
            4 => item => item.StorageClass,
            _ => item => item.Name
        };
        ordered = _sortAscending
            ? ordered.ThenBy(selector, Comparer<object>.Create(CompareObjects))
            : ordered.ThenByDescending(selector, Comparer<object>.Create(CompareObjects));

        _objects.BeginUpdate();
        try
        {
            _objects.Items.Clear();
            foreach (var entry in ordered)
                _objects.Items.Add(CreateObjectItem(entry));
            if (_hasMore)
            {
                var more = new ListViewItem("加载更多...") { Tag = new LoadMoreTag(), Font = new Font(_objects.Font, FontStyle.Bold), ForeColor = SystemColors.HotTrack };
                more.SubItems.AddRange(["", "分页", "", ""]);
                _objects.Items.Add(more);
            }
        }
        finally { _objects.EndUpdate(); }

        _objectStatus.Text = _hasMore
            ? $"已显示 {_loadedItems.Count:N0} 个对象，还有更多"
            : $"{_loadedItems.Count:N0} 个对象";
        UpdateSelectionStatus();
    }

    private static int CompareObjects(object? left, object? right)
    {
        if (left is string a && right is string b) return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        if (left is IComparable comparable) return comparable.CompareTo(right);
        return 0;
    }

    private static ListViewItem CreateObjectItem(S3ObjectEntry entry)
    {
        var item = new ListViewItem(entry.Name) { Tag = entry };
        item.SubItems.Add(entry.IsDirectory ? string.Empty : FileSizeFormatter.Format(entry.Size));
        item.SubItems.Add(entry.IsDirectory ? "Folder" : ObjectTypeDetector.GetTypeName(entry.Name));
        item.SubItems.Add(entry.LastModified?.LocalDateTime.ToString("G") ?? string.Empty);
        item.SubItems.Add(entry.IsDirectory ? string.Empty : entry.StorageClass);
        return item;
    }

    private async Task RefreshAsync()
    {
        if (_currentProfile is null) return;
        if (_currentBucket is null) await ReloadBucketsAsync();
        else await LoadObjectsPageAsync(true);
    }

    private async Task ReloadBucketsAsync()
    {
        if (_currentProfile is null) return;
        var node = _tree.Nodes[0].Nodes.Cast<TreeNode>()
            .FirstOrDefault(item => item.Tag is ConnectionProfile profile && profile.Id == _currentProfile.Id);
        if (node is not null) await LoadBucketsAsync(_currentProfile, node);
    }

    private async Task NavigateUpAsync()
    {
        if (_currentProfile is null || _currentBucket is null) return;
        var parent = S3Path.ParentPrefix(_currentPrefix);
        await NavigateAsync(_currentProfile, _currentBucket, parent, true);
    }

    private async Task NavigateAddressAsync()
    {
        if (!S3Location.TryParse(_address.Text, out var location, out var error) || location is null)
        {
            MessageBox.Show(this, error, "地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var profile = _profiles.FirstOrDefault(item => string.Equals(item.Name, location.Profile, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            MessageBox.Show(this, $"找不到连接：{location.Profile}", "地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await NavigateAsync(profile, location.Bucket, location.Prefix, true);
    }

    private void AddHistory(S3Location location)
    {
        if (_historyIndex >= 0 && _history[_historyIndex] == location) return;
        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(location);
        _historyIndex = _history.Count - 1;
        UpdateCommandStates();
    }

    private async void NavigateHistory(int delta)
    {
        var next = _historyIndex + delta;
        if (next < 0 || next >= _history.Count) return;
        _historyIndex = next;
        var location = _history[next];
        var profile = _profiles.FirstOrDefault(item => string.Equals(item.Name, location.Profile, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
            await NavigateAsync(profile, location.Bucket, location.Prefix, false);
    }

    private async Task CreateBucketAsync()
    {
        if (_currentProfile is null)
        {
            MessageBox.Show(this, "请先连接账户。", "新建 Bucket");
            return;
        }
        var name = PromptDialog.Show(this, "新建 Bucket", "Bucket 名称（小写，3-63 个字符）：");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            SetBusy("正在创建 Bucket...");
            await _storage.CreateBucketAsync(_currentProfile, name.Trim(), _currentProfile.Region, CancellationToken.None);
            await ReloadBucketsAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "创建失败", "创建 Bucket", exception, name);
        }
        finally { SetIdle(); }
    }

    private async Task DeleteBucketAsync()
    {
        var tag = _tree.SelectedNode?.Tag as BucketNodeTag;
        if (tag is null) return;
        if (MessageBox.Show(this,
                $"确定删除空 Bucket “{tag.Bucket}”吗？\n\n非空 Bucket、存在未完成分片上传或版本对象时将拒绝删除。",
                "删除 Bucket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            SetBusy("正在检查并删除 Bucket...");
            await _storage.DeleteEmptyBucketAsync(tag.Profile, tag.Bucket, CancellationToken.None);
            _currentBucket = null;
            await ReloadBucketsAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "删除失败", "删除 Bucket", exception, tag.Bucket);
        }
        finally { SetIdle(); }
    }

    private async Task UploadFilesAsync()
    {
        if (!EnsureLocation()) return;
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await UploadPathsAsync(dialog.FileNames);
    }

    private async Task UploadFolderAsync()
    {
        if (!EnsureLocation()) return;
        using var dialog = new FolderBrowserDialog { Description = "选择要递归上传的文件夹" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await UploadPathsAsync([dialog.SelectedPath]);
    }

    private Task UploadPathsAsync(IEnumerable<string> paths)
    {
        if (!EnsureLocation()) return Task.CompletedTask;
        foreach (var path in paths)
        {
            if (File.Exists(path))
                EnqueueUpload(path, _currentPrefix + Path.GetFileName(path));
            else if (Directory.Exists(path))
            {
                var rootName = new DirectoryInfo(path).Name;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    EnqueueUpload(file, S3Path.Combine(_currentPrefix, rootName, relative));
                }
            }
        }
        SetTransferVisibility(true);
        return Task.CompletedTask;
    }

    private void EnqueueUpload(string localPath, string key)
    {
        var file = new FileInfo(localPath);
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var storageClass = profile.DefaultStorageClass;
        _transfers.Enqueue(new TransferItem
        {
            Name = file.Name,
            Direction = "上传",
            Source = localPath,
            Target = $"s3://{profile.Name}/{bucket}/{key}",
            Size = file.Length,
            Operation = (cancellation, progress) => _storage.UploadFileAsync(profile, bucket, key, localPath, storageClass, progress, cancellation)
        });
        _logger.Info($"Upload queued profile={profile.Name} bucket={bucket} key={key} bytes={file.Length}");
    }

    private async Task DownloadSelectedAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count == 0) return;

        string targetRoot;
        if (selected.Count == 1 && !selected[0].IsDirectory)
        {
            using var save = new SaveFileDialog
            {
                FileName = selected[0].Name,
                InitialDirectory = _settings.DefaultDownloadDirectory,
                OverwritePrompt = _settings.ConfirmOverwrite
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;
            EnqueueDownload(selected[0], save.FileName);
            SetTransferVisibility(true);
            return;
        }
        using (var folder = new FolderBrowserDialog
               {
                   InitialDirectory = _settings.DefaultDownloadDirectory,
                   Description = "选择下载目标文件夹"
               })
        {
            if (folder.ShowDialog(this) != DialogResult.OK) return;
            targetRoot = folder.SelectedPath;
        }

        foreach (var entry in selected)
        {
            if (!entry.IsDirectory)
            {
                EnqueueDownload(entry, Path.Combine(targetRoot, entry.Name));
                continue;
            }
            var descendants = await ListAllObjectsAsync(entry.Key);
            foreach (var child in descendants.Where(item => !item.IsDirectory))
            {
                var relative = child.Key[entry.Key.Length..].Replace('/', Path.DirectorySeparatorChar);
                EnqueueDownload(child, Path.Combine(targetRoot, entry.Name, relative));
            }
        }
        SetTransferVisibility(true);
    }

    private void EnqueueDownload(S3ObjectEntry entry, string localPath)
    {
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        _transfers.Enqueue(new TransferItem
        {
            Name = entry.Name,
            Direction = "下载",
            Source = $"s3://{profile.Name}/{bucket}/{entry.Key}",
            Target = localPath,
            Size = entry.Size,
            Operation = (cancellation, progress) => _storage.DownloadFileAsync(profile, bucket, entry.Key, localPath, progress, cancellation)
        });
        _logger.Info($"Download queued profile={profile.Name} bucket={bucket} key={entry.Key} bytes={entry.Size}");
    }

    private async Task CreateFolderAsync()
    {
        if (!EnsureLocation()) return;
        var name = PromptDialog.Show(this, "新建文件夹", "文件夹名称：");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().Trim('/');
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            MessageBox.Show(this, "文件夹名称无效。", "新建文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            await _storage.CreateFolderAsync(_currentProfile!, _currentBucket!, S3Path.Combine(_currentPrefix, name) + "/", CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "创建失败", "新建虚拟目录", exception, CurrentLocationText());
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count == 0) return;
        var keys = new List<string>();
        foreach (var entry in selected)
        {
            if (entry.IsDirectory)
                keys.AddRange((await ListAllObjectsAsync(entry.Key)).Where(item => !item.IsDirectory).Select(item => item.Key));
            else
                keys.Add(entry.Key);
        }
        keys = keys.Distinct(StringComparer.Ordinal).ToList();
        if (_settings.ConfirmDelete &&
            MessageBox.Show(this,
                $"将删除 {keys.Count:N0} 个对象。\n\n此操作可能无法撤销，版本控制 Bucket 的普通删除可能创建 Delete Marker。",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            SetBusy($"正在删除 {keys.Count:N0} 个对象...");
            await _storage.DeleteObjectsAsync(_currentProfile!, _currentBucket!, keys, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "删除失败", "批量删除对象", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    private async Task CopyOrMoveSelectedAsync(bool move)
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory)
        {
            MessageBox.Show(this, "当前版本的复制/移动一次支持一个文件对象；目录递归复制将在后续版本提供。", move ? "移动" : "复制");
            return;
        }
        var source = selected[0];
        var target = PromptDialog.Show(this, move ? "移动对象" : "复制对象",
            "目标（格式：bucket/key）：", $"{_currentBucket}/{source.Key}");
        if (string.IsNullOrWhiteSpace(target)) return;
        var slash = target.IndexOf('/');
        if (slash <= 0 || slash == target.Length - 1)
        {
            MessageBox.Show(this, "目标必须使用 bucket/key 格式。", move ? "移动" : "复制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var targetBucket = target[..slash];
        var targetKey = target[(slash + 1)..];
        try
        {
            SetBusy(move ? "正在移动对象..." : "正在复制对象...");
            if (move)
                await _storage.MoveObjectAsync(_currentProfile!, _currentBucket!, source.Key, targetBucket, targetKey, CancellationToken.None);
            else
                await _storage.CopyObjectAsync(_currentProfile!, _currentBucket!, source.Key, targetBucket, targetKey, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, move ? "移动失败" : "复制失败", move ? "Copy + Delete" : "CopyObject", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    private async Task RenameSelectedAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory)
        {
            MessageBox.Show(this, "当前版本一次只支持重命名一个文件对象。目录重命名将在后续版本提供。", "重命名");
            return;
        }
        var entry = selected[0];
        var name = PromptDialog.Show(this, "重命名对象", "新名称：", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        var parent = entry.Key[..Math.Max(0, entry.Key.LastIndexOf('/') + 1)];
        var targetKey = parent + name.Trim('/');
        try
        {
            SetBusy("正在重命名（Copy + Delete）...");
            await _storage.MoveObjectAsync(_currentProfile!, _currentBucket!, entry.Key, _currentBucket!, targetKey, CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "重命名失败", "Copy + Delete", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    private async Task ShowPropertiesAsync()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory) return;
        try
        {
            SetBusy("正在读取对象属性...");
            var properties = await _storage.GetObjectPropertiesAsync(_currentProfile!, _currentBucket!, selected[0].Key, CancellationToken.None);
            using var dialog = new ObjectPropertiesDialog(properties, _currentProfile!.Endpoint);
            dialog.ShowDialog(this);
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "读取失败", "对象属性", exception, CurrentLocationText());
        }
        finally { SetIdle(); }
    }

    private void ShowPresignedUrl()
    {
        if (!EnsureLocation()) return;
        var selected = SelectedEntries();
        if (selected.Count != 1 || selected[0].IsDirectory) return;
        var entry = selected[0];
        using var dialog = new PresignedUrlDialog(
            $"s3://{_currentProfile!.Name}/{_currentBucket}/{entry.Key}",
            lifetime => _storage.CreatePresignedUrl(_currentProfile!, _currentBucket!, entry.Key, lifetime));
        dialog.ShowDialog(this);
        _logger.Info($"Presigned URL generated bucket={_currentBucket} key={entry.Key}");
    }

    private async Task TestCurrentConnectionAsync()
    {
        var profile = _currentProfile ?? SelectedTreeProfile();
        if (profile is null) return;
        SetBusy("正在测试连接...");
        try
        {
            var result = await _storage.TestConnectionAsync(profile, CancellationToken.None);
            MessageBox.Show(this,
                $"{result.Message}\n\nEndpoint: {profile.Endpoint}\nHTTP: {result.HttpStatusCode?.ToString() ?? "—"}\nAWS ErrorCode: {result.ErrorCode ?? "—"}\nRequestId: {result.RequestId ?? "—"}\n耗时: {result.Elapsed.TotalMilliseconds:N0} ms",
                result.Success ? "连接成功" : "连接失败",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally { SetIdle(); }
    }

    private async Task<IReadOnlyList<S3ObjectEntry>> ListAllObjectsAsync(string prefix)
    {
        var result = new List<S3ObjectEntry>();
        string? token = null;
        do
        {
            var page = await _storage.ListObjectsAsync(_currentProfile!, _currentBucket!, prefix, token, 1000, CancellationToken.None);
            result.AddRange(page.Items);
            token = page.ContinuationToken;
            if (!page.HasMore) break;
        } while (true);
        return result;
    }

    private async Task ShowSettingsAsync()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _settings = dialog.Settings;
        _transfers.SetConcurrency(_settings.ConcurrentTransfers);
        await SaveSettingsAsync();
    }

    private void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logger.CurrentLogPath)!);
            if (!File.Exists(_logger.CurrentLogPath)) File.WriteAllText(_logger.CurrentLogPath, string.Empty);
            Process.Start(new ProcessStartInfo(_logger.CurrentLogPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "无法打开日志", "查看日志", exception, _logger.CurrentLogPath);
        }
    }

    private void OpenProjectFile(string name)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, name),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", name))
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            MessageBox.Show(this, "README.md 不在发布目录中。", "使用说明");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ShowShortcuts()
    {
        MessageBox.Show(this,
            "F5  刷新\nAlt+Left / Alt+Right  返回 / 前进\nAlt+Up  上一级\nCtrl+L  地址栏\nCtrl+F  搜索\nCtrl+U  上传文件\nCtrl+Shift+U  上传文件夹\nCtrl+D  下载\nF2  重命名\nDelete  删除\nAlt+Enter  属性\nCtrl+A  全选\nEscape  清除选择",
            "快捷键", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SelectAllObjects()
    {
        foreach (ListViewItem item in _objects.Items)
            item.Selected = item.Tag is S3ObjectEntry;
    }

    private void CopySelectedPaths()
    {
        var values = SelectedEntries().Select(entry => $"s3://{_currentProfile?.Name}/{_currentBucket}/{entry.Key}").ToArray();
        if (values.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, values));
    }

    private void CopySelectedKeys()
    {
        var values = SelectedEntries().Select(entry => entry.Key).ToArray();
        if (values.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, values));
    }

    private void CopySelectedUrls()
    {
        if (_currentProfile is null || _currentBucket is null) return;
        var endpoint = _currentProfile.Endpoint.TrimEnd('/');
        var values = SelectedEntries().Where(entry => !entry.IsDirectory)
            .Select(entry => $"{endpoint}/{Uri.EscapeDataString(_currentBucket)}/{string.Join("/", entry.Key.Split('/').Select(Uri.EscapeDataString))}")
            .ToArray();
        if (values.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, values));
    }

    private void SetTransferVisibility(bool visible)
    {
        _outerSplit.Panel2Collapsed = !visible;
        if (visible && _outerSplit.Height > 350)
            _outerSplit.SplitterDistance = Math.Max(200, _outerSplit.Height - Math.Clamp(_settings.TransferPanelHeight, 120, 350));
        if (_commands.TryGetValue("show-transfers", out var item) && item is ToolStripMenuItem menu)
            menu.Checked = visible;
    }

    private bool EnsureLocation()
    {
        if (_currentProfile is not null && _currentBucket is not null) return true;
        MessageBox.Show(this, "请先连接账户并选择 Bucket。", "S3 Explorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private ConnectionProfile? SelectedTreeProfile() => _tree.SelectedNode?.Tag switch
    {
        ConnectionProfile profile => profile,
        BucketNodeTag tag => tag.Profile,
        _ => null
    };

    private List<S3ObjectEntry> SelectedEntries() =>
        _objects.SelectedItems.Cast<ListViewItem>().Select(item => item.Tag).OfType<S3ObjectEntry>().ToList();

    private string CurrentLocationText() =>
        _currentProfile is null || _currentBucket is null
            ? "s3://"
            : $"s3://{_currentProfile.Name}/{_currentBucket}/{_currentPrefix}";

    private void UpdateSelectionStatus()
    {
        var selected = SelectedEntries();
        var size = selected.Where(item => !item.IsDirectory).Sum(item => item.Size);
        _selectionStatus.Text = $"已选择 {selected.Count:N0} 个，共 {FileSizeFormatter.Format(size)}";
    }

    private void SetBusy(string text)
    {
        _requestStatus.Text = text;
        UseWaitCursor = true;
    }

    private void SetIdle()
    {
        _requestStatus.Text = "空闲";
        UseWaitCursor = false;
    }

    private void CancelNavigation()
    {
        _navigationRevision++;
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
    }

    private ToolStripMenuItem Command(string id, string text, EventHandler handler, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text, null, handler) { ShortcutKeys = shortcut };
        _commands[id] = item;
        return item;
    }

    private static ToolStripMenuItem Unsupported(string text) =>
        new(text) { Enabled = false, ToolTipText = "当前版本尚未支持" };

    private void AddToolbarButton(string id, string text, string glyph, EventHandler handler)
    {
        var button = new ToolStripButton(text, UiIcons.Create(glyph), handler)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = text,
            AutoToolTip = true
        };
        _toolbar.Items.Add(button);
        _commands[id] = button;
    }

    private void UpdateCommandStates()
    {
        var profileSelected = SelectedTreeProfile() is not null;
        var connected = _currentProfile is not null;
        var inBucket = connected && _currentBucket is not null;
        var selected = SelectedEntries();
        var oneFile = selected.Count == 1 && !selected[0].IsDirectory;
        var any = selected.Count > 0;
        var bucketSelected = _tree.SelectedNode?.Tag is BucketNodeTag;

        SetEnabled("edit-connection", profileSelected);
        SetEnabled("delete-connection", profileSelected);
        SetEnabled("connect", profileSelected);
        SetEnabled("disconnect", connected);
        SetEnabled("create-bucket", connected);
        SetEnabled("create-bucket-toolbar", connected);
        SetEnabled("delete-bucket", bucketSelected);
        SetEnabled("refresh-buckets", connected);
        SetEnabled("upload-file", inBucket);
        SetEnabled("upload-folder", inBucket);
        SetEnabled("upload-toolbar", inBucket);
        SetEnabled("download", any);
        SetEnabled("download-toolbar", any);
        SetEnabled("create-folder", inBucket);
        SetEnabled("create-folder-toolbar", inBucket);
        SetEnabled("delete-object", any);
        SetEnabled("delete-object-menu", any);
        SetEnabled("delete-toolbar", any);
        SetEnabled("copy-object", oneFile);
        SetEnabled("copy-toolbar", oneFile);
        SetEnabled("move-object", oneFile);
        SetEnabled("move-toolbar", oneFile);
        SetEnabled("rename", oneFile);
        SetEnabled("rename-object", oneFile);
        SetEnabled("properties", oneFile);
        SetEnabled("properties-menu", oneFile);
        SetEnabled("properties-toolbar", oneFile);
        SetEnabled("metadata", oneFile);
        SetEnabled("presign", oneFile);
        SetEnabled("copy-path", any);
        SetEnabled("copy-url", any);
        SetEnabled("copy-key", any);
        SetEnabled("back", _historyIndex > 0);
        SetEnabled("forward", _historyIndex >= 0 && _historyIndex < _history.Count - 1);
        SetEnabled("up", inBucket && _currentPrefix.Length > 0);
    }

    private void SetEnabled(string id, bool enabled)
    {
        if (_commands.TryGetValue(id, out var item)) item.Enabled = enabled;
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        if (_transfers.ActiveCount > 0)
        {
            var answer = MessageBox.Show(this,
                $"仍有 {_transfers.ActiveCount} 个传输任务正在运行。\n\n选择“是”将取消任务并退出；选择“否”返回程序。",
                "传输任务仍在运行", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        _closing = true;
        CancelNavigation();
        _speedTimer.Stop();
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        if (!_settings.RememberLayout)
        {
            await _settingsStore.SaveAsync(_settings);
            return;
        }
        var widths = _objects.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray();
        var transferHeight = _outerSplit.Panel2Collapsed ? _settings.TransferPanelHeight : Math.Max(100, _outerSplit.Height - _outerSplit.SplitterDistance);
        _settings = _settings with
        {
            WindowX = WindowState == FormWindowState.Normal ? Left : RestoreBounds.Left,
            WindowY = WindowState == FormWindowState.Normal ? Top : RestoreBounds.Top,
            WindowWidth = WindowState == FormWindowState.Normal ? Width : RestoreBounds.Width,
            WindowHeight = WindowState == FormWindowState.Normal ? Height : RestoreBounds.Height,
            LeftPanelWidth = _mainSplit.SplitterDistance,
            TransferPanelHeight = transferHeight,
            ShowTransfers = !_outerSplit.Panel2Collapsed,
            ObjectColumnWidths = widths,
            SortColumn = _sortColumn,
            SortAscending = _sortAscending
        };
        await _settingsStore.SaveAsync(_settings);
    }
}
