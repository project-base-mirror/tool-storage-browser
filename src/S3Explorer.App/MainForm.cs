using System.ComponentModel;
using System.Diagnostics;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record BucketNodeTag(ConnectionProfile Profile, string Bucket);
internal sealed record LoadMoreTag;

internal sealed class MainForm : Form
{
    private static string DisplayVersion
    {
        get
        {
            var version = typeof(MainForm).Assembly.GetName().Version;
            return version is null
                ? Application.ProductVersion
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private static string WindowTitle(string? profileName = null) =>
        string.IsNullOrWhiteSpace(profileName)
            ? $"S3 Explorer v{DisplayVersion}"
            : $"S3 Explorer v{DisplayVersion} - {profileName}";

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
    private readonly ImageList _smallImages = UiIcons.CreateSmallImageList();
    private readonly ContextMenuStrip _accountMenu = new();
    private readonly PersistentTransferQueue _transferQueue;
    private readonly TransferRuntimeConfiguration _transferRuntime;
    private readonly TransferQueueControl _transfers;
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
    private readonly BoundedObjectCache _loadedItems = new(ObjectListingLimits.DefaultCacheLimit);
    private readonly List<S3Location> _history = [];
    private readonly Dictionary<string, ToolStripItem> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly OperationCancellation _navigationCancellation = new();

    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private AppSettings _settings = new();
    private ConnectionProfile? _currentProfile;
    private string? _currentBucket;
    private string _currentPrefix = string.Empty;
    private string? _continuationToken;
    private bool _hasMore;
    private bool _objectLimitReached;
    private int _historyIndex = -1;
    private int _sortColumn;
    private bool _sortAscending = true;
    private long _navigationRevision;
    private bool _closing;
    private bool _suppressTreeSelection;

    public MainForm(
        IProfileStore profileStore,
        IS3StorageService storage,
        AppSettingsStore settingsStore,
        SimpleFileLogger logger,
        PersistentTransferQueue transferQueue,
        TransferRuntimeConfiguration transferRuntime)
    {
        _profileStore = profileStore;
        _storage = storage;
        _settingsStore = settingsStore;
        _logger = logger;
        _transferQueue = transferQueue;
        _transferRuntime = transferRuntime;
        _transfers = new TransferQueueControl(transferQueue);

        Text = WindowTitle();
        Icon = UiIcons.CreateApplicationIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 780);
        MinimumSize = new Size(960, 600);
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildAddressBar();
        BuildBody();
        BuildAccountContextMenu();
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
        tools.DropDownItems.Add(Command("multipart-uploads", "未完成的分片上传...", (_, _) => ShowIncompleteMultipartUploads()));
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
            MessageBox.Show(this, $"S3 Explorer v{Application.ProductVersion}\n\n原生 Windows S3 / S3-compatible 对象存储管理工具。\n.NET 10 · WinForms · AWS SDK for .NET", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.AddRange([file, edit, view, bucket, objects, tools, help]);
    }

    private void BuildToolbar()
    {
        AddToolbarButton("new-connection", "新建连接", UiIconKind.NewConnection, (_, _) => NewConnection());
        AddToolbarButton("connect-toolbar", "连接/断开", UiIconKind.Connect, async (_, _) =>
        {
            if (_currentProfile is null) await ConnectSelectedAsync(); else Disconnect();
        });
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("back-toolbar", "返回 (Alt+Left)", UiIconKind.Back, (_, _) => NavigateHistory(-1));
        AddToolbarButton("forward-toolbar", "前进 (Alt+Right)", UiIconKind.Forward, (_, _) => NavigateHistory(1));
        AddToolbarButton("up-toolbar", "上一级 (Alt+Up)", UiIconKind.Up, async (_, _) => await NavigateUpAsync());
        AddToolbarButton("refresh-toolbar", "刷新 (F5)", UiIconKind.Refresh, async (_, _) => await RefreshAsync());
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("create-bucket-toolbar", "新建 Bucket", UiIconKind.Bucket, async (_, _) => await CreateBucketAsync());
        AddToolbarButton("create-folder-toolbar", "新建文件夹", UiIconKind.Folder, async (_, _) => await CreateFolderAsync());

        var upload = new ToolStripDropDownButton("上传", UiIcons.Create(UiIconKind.Upload))
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "上传文件或文件夹"
        };
        upload.DropDownItems.Add("上传文件...", UiIcons.Create(UiIconKind.Upload, 16), async (_, _) => await UploadFilesAsync());
        upload.DropDownItems.Add("上传文件夹...", UiIcons.Create(UiIconKind.Folder, 16), async (_, _) => await UploadFolderAsync());
        _toolbar.Items.Add(upload);
        _commands["upload-toolbar"] = upload;

        AddToolbarButton("download-toolbar", "下载", UiIconKind.Download, async (_, _) => await DownloadSelectedAsync());
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("copy-toolbar", "复制", UiIconKind.Copy, async (_, _) => await CopyOrMoveSelectedAsync(false));
        AddToolbarButton("move-toolbar", "移动", UiIconKind.Move, async (_, _) => await CopyOrMoveSelectedAsync(true));
        AddToolbarButton("delete-toolbar", "删除", UiIconKind.Delete, async (_, _) => await DeleteSelectedAsync());
        AddToolbarButton("properties-toolbar", "属性", UiIconKind.Properties, async (_, _) => await ShowPropertiesAsync());
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("transfers-toolbar", "传输队列", UiIconKind.Transfers, (_, _) => SetTransferVisibility(!_outerSplit.Panel2Collapsed));
        AddToolbarButton("settings-toolbar", "设置", UiIconKind.Settings, async (_, _) => await ShowSettingsAsync());
        _toolbar.Dock = DockStyle.Top;
    }

    private void BuildAddressBar()
    {
        var back = new ToolStripButton(UiIcons.Create(UiIconKind.Back, 18)) { ToolTipText = "返回" };
        back.Click += (_, _) => NavigateHistory(-1);
        var forward = new ToolStripButton(UiIcons.Create(UiIconKind.Forward, 18)) { ToolTipText = "前进" };
        forward.Click += (_, _) => NavigateHistory(1);
        var up = new ToolStripButton(UiIcons.Create(UiIconKind.Up, 18)) { ToolTipText = "上一级" };
        up.Click += async (_, _) => await NavigateUpAsync();
        var refresh = new ToolStripButton(UiIcons.Create(UiIconKind.Refresh, 18)) { ToolTipText = "刷新" };
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
        _tree.ImageList = _smallImages;
        _objects.SmallImageList = _smallImages;
        _objects.Columns.Add("名称", 320);
        _objects.Columns.Add("大小", 110, HorizontalAlignment.Right);
        _objects.Columns.Add("类型", 120);
        _objects.Columns.Add("修改时间", 165);
        _objects.Columns.Add("存储类型", 120);

        var root = new TreeNode("Accounts")
        {
            Name = "Accounts",
            ImageKey = "accounts",
            SelectedImageKey = "accounts"
        };
        _tree.Nodes.Add(root);

        _mainSplit.Panel1.Controls.Add(_tree);
        _mainSplit.Panel2.Controls.Add(_objects);
        _outerSplit.Panel1.Controls.Add(_mainSplit);
        _outerSplit.Panel2.Controls.Add(_transfers);
        _outerSplit.SplitterWidth = 6;
        _mainSplit.SplitterWidth = 6;
    }

    private void BuildAccountContextMenu()
    {
        var connect = new ToolStripMenuItem("连接", UiIcons.Create(UiIconKind.Connect, 16));
        connect.Click += async (_, _) => await ConnectSelectedAsync();
        var edit = new ToolStripMenuItem("修改...", UiIcons.Create(UiIconKind.Properties, 16));
        edit.Click += (_, _) => EditCurrentConnection();
        var delete = new ToolStripMenuItem("删除", UiIcons.Create(UiIconKind.Delete, 16));
        delete.Click += async (_, _) => await DeleteCurrentConnectionAsync();
        _accountMenu.Items.AddRange([connect, edit, new ToolStripSeparator(), delete]);
        _accountMenu.Opening += (_, args) =>
        {
            var accountSelected = _tree.SelectedNode?.Tag is ConnectionProfile;
            args.Cancel = !accountSelected;
            connect.Enabled = accountSelected;
            edit.Enabled = accountSelected;
            delete.Enabled = accountSelected;
        };
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
        _tree.NodeMouseClick += (_, args) =>
        {
            var node = args.Node;
            if (args.Button != MouseButtons.Right || node?.Tag is not ConnectionProfile) return;
            _tree.SelectedNode = node;
            _accountMenu.Show(_tree, args.Location);
        };
        _tree.NodeMouseDoubleClick += async (_, args) =>
        {
            var node = args.Node;
            if (node is null) return;
            if (node.Tag is ConnectionProfile profile)
                await LoadBucketsAsync(profile, node);
            else if (node.Tag is BucketNodeTag bucket)
                await NavigateAsync(bucket.Profile, bucket.Bucket, string.Empty, true);
        };
        _tree.AfterSelect += async (_, args) =>
        {
            UpdateCommandStates();
            if (_suppressTreeSelection) return;
            var node = args.Node;
            if (node is null) return;
            if (node.Tag is ConnectionProfile profile)
                ShowConnectionSummary(profile, node);
            else if (node.Tag is BucketNodeTag bucket)
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
        _transfers.TransferCompleted += async (_, args) =>
        {
            if (!_closing &&
                _currentProfile?.Id == args.Task.ProfileId &&
                string.Equals(_currentBucket, args.Task.Bucket, StringComparison.Ordinal))
            {
                await RefreshAsync();
            }
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
        _transferRuntime.Apply(_settings);
        _transfers.ConfigureRetryPolicy(_settings.RetryCount, _settings.RetryDelaySeconds);
        ApplySettings();
        _profiles = await _profileStore.LoadAsync();
        PopulateProfiles();
        await _transfers.InitializeAsync();
        await _transfers.SetConcurrencyAsync(_settings.ConcurrentTransfers);
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
    }

    private void PopulateProfiles()
    {
        var root = _tree.Nodes[0];
        root.Nodes.Clear();
        foreach (var profile in _profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            var defaultBucket = string.IsNullOrWhiteSpace(profile.DefaultBucket) ? string.Empty : $"\n默认 Bucket: {profile.DefaultBucket}";
            var node = new TreeNode(profile.Name)
            {
                Tag = profile,
                ImageKey = "account",
                SelectedImageKey = "account",
                ToolTipText = $"{profile.Endpoint}\n签名 Region: {profile.EffectiveSignatureRegion}{defaultBucket}\n未连接"
            };
            node.Nodes.Add(new TreeNode("(双击连接)")
            {
                ForeColor = SystemColors.GrayText,
                ImageKey = "connect",
                SelectedImageKey = "connect"
            });
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
        var revision = ++_navigationRevision;
        var cancellationToken = _navigationCancellation.StartNew();
        SetBusy($"正在连接 {profile.Name}...");
        try
        {
            var buckets = await _storage.ListBucketsAsync(profile, cancellationToken);
            if (revision != _navigationRevision || cancellationToken.IsCancellationRequested)
                return;

            _currentProfile = profile;
            _currentBucket = null;
            _currentPrefix = string.Empty;
            Text = WindowTitle(profile.Name);
            _connectionStatus.Text = $"已连接：{profile.Name}";
            profileNode.Nodes.Clear();
            foreach (var bucket in buckets)
            {
                profileNode.Nodes.Add(new TreeNode(bucket.Name)
                {
                    Tag = new BucketNodeTag(profile, bucket.Name),
                    ImageKey = "bucket",
                    SelectedImageKey = "bucket",
                    ToolTipText = bucket.Region is null ? bucket.Name : $"{bucket.Name}\nRegion: {bucket.Region}"
                });
            }
            if (buckets.Count == 0)
            {
                profileNode.Nodes.Add(new TreeNode("(没有 Bucket；可在连接设置中添加外部 Bucket)")
                {
                    ForeColor = SystemColors.GrayText,
                    ImageKey = "info",
                    SelectedImageKey = "info"
                });
            }
            profileNode.Expand();
            var defaultNode = string.IsNullOrWhiteSpace(profile.DefaultBucket)
                ? null
                : profileNode.Nodes.Cast<TreeNode>()
                    .FirstOrDefault(item => item.Tag is BucketNodeTag tag &&
                        string.Equals(tag.Bucket, profile.DefaultBucket, StringComparison.Ordinal));
            if (defaultNode?.Tag is BucketNodeTag defaultBucket)
            {
                _suppressTreeSelection = true;
                _tree.SelectedNode = defaultNode;
                _suppressTreeSelection = false;
                await NavigateAsync(defaultBucket.Profile, defaultBucket.Bucket, string.Empty, true);
            }
            else
            {
                ShowConnectionSummary(profile, profileNode, buckets.Count);
            }
            _logger.Info($"Connected profile={profile.Name} endpoint={profile.Endpoint} buckets={buckets.Count}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.Error($"Connect failed profile={profile.Name} endpoint={profile.Endpoint}", exception);
            ErrorDialog.ShowException(this, "连接失败", "列出 Bucket", exception, profile.Endpoint);
        }
        finally
        {
            if (revision == _navigationRevision)
            {
                SetIdle();
                UpdateCommandStates();
            }
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
        Text = WindowTitle();
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
            AddSummaryItem("签名 Region", profile.EffectiveSignatureRegion);
            AddSummaryItem("服务类型", profile.ServiceType.ToString());
            AddSummaryItem("默认 Bucket", string.IsNullOrWhiteSpace(profile.DefaultBucket) ? "未配置" : profile.DefaultBucket);
            AddSummaryItem("外部 Bucket", profile.ExternalBuckets.Count == 0 ? "未配置" : string.Join(", ", profile.ExternalBuckets));
            AddSummaryItem("Bucket 数量", bucketCount?.ToString() ?? Math.Max(0, node.Nodes.Count).ToString());
            AddSummaryItem("当前状态", _currentProfile?.Id == profile.Id ? "已连接" : "未连接");
            AddSummaryItem("临时凭据", profile.UsesTemporarySessionCredentials
                ? "已启用（Session Token）"
                : "未启用");
            AddSummaryItem("凭据存储", "SecretKey 与 SessionToken 使用 DPAPI CurrentUser 加密");
        }
        finally { _objects.EndUpdate(); }
        _objectStatus.Text = "连接摘要";
        _selectionStatus.Text = string.Empty;
    }

    private void AddSummaryItem(string name, string value)
    {
        var item = new ListViewItem(name) { ImageKey = "info" };
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
        Text = WindowTitle(profile.Name);
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

        var profile = _currentProfile;
        var bucket = _currentBucket;
        var prefix = _currentPrefix;
        var continuationToken = reset ? null : _continuationToken;
        var cancellationToken = reset
            ? _navigationCancellation.StartNew()
            : _navigationCancellation.CurrentOrStart();

        if (reset)
        {
            _loadedItems.Reset(Math.Clamp(
                _settings.ObjectCacheLimit,
                ObjectListingLimits.MinimumCacheLimit,
                ObjectListingLimits.MaximumCacheLimit));
            _continuationToken = null;
            _hasMore = false;
            _objectLimitReached = false;
            _objects.Items.Clear();
        }

        var revision = ++_navigationRevision;
        SetBusy("正在加载对象...");
        try
        {
            var page = await _storage.ListObjectsAsync(
                profile,
                bucket,
                prefix,
                continuationToken,
                Math.Clamp(
                    _settings.ObjectPageSize,
                    ObjectListingLimits.MinimumPageSize,
                    ObjectListingLimits.MaximumPageSize),
                cancellationToken);
            if (revision != _navigationRevision ||
                cancellationToken.IsCancellationRequested ||
                _currentProfile?.Id != profile.Id ||
                !string.Equals(_currentBucket, bucket, StringComparison.Ordinal) ||
                !string.Equals(_currentPrefix, prefix, StringComparison.Ordinal))
            {
                return;
            }

            var addResult = _loadedItems.AddRange(page.Items);
            _objectLimitReached = addResult.Truncated || (_loadedItems.LimitReached && page.HasMore);
            _hasMore = page.HasMore &&
                !_objectLimitReached &&
                !string.IsNullOrEmpty(page.ContinuationToken);
            _continuationToken = _hasMore ? page.ContinuationToken : null;
            ApplyFilterAndSort();
            _logger.Info(
                $"ListObjects profile={profile.Name} bucket={bucket} prefix={prefix} received={page.Items.Count} added={addResult.AddedCount} total={_loadedItems.Count} hasMore={_hasMore} limitReached={_objectLimitReached}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.Error($"ListObjects failed bucket={bucket} prefix={prefix}", exception);
            ErrorDialog.ShowException(this, "加载失败", "列出对象", exception, $"s3://{profile.Name}/{bucket}/{prefix}");
        }
        finally
        {
            if (revision == _navigationRevision)
                SetIdle();
        }
    }

    private void ApplyFilterAndSort()
    {
        var query = _search.Text.Trim();
        IEnumerable<S3ObjectEntry> items = _loadedItems.Items;
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
                var more = new ListViewItem("加载更多...")
                {
                    Tag = new LoadMoreTag(),
                    ImageKey = "refresh",
                    Font = new Font(_objects.Font, FontStyle.Bold),
                    ForeColor = SystemColors.HotTrack
                };
                more.SubItems.AddRange(["", "分页", "", ""]);
                _objects.Items.Add(more);
            }
        }
        finally { _objects.EndUpdate(); }

        _objectStatus.Text = _objectLimitReached
            ? $"已显示 {_loadedItems.Count:N0} 个对象，已达到内存保护上限"
            : _hasMore
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
        var item = new ListViewItem(entry.Name)
        {
            Tag = entry,
            ImageKey = UiIcons.ObjectImageKey(entry.Name, entry.IsDirectory)
        };
        item.SubItems.Add(entry.IsDirectory ? string.Empty : FileSizeFormatter.Format(entry.Size));
        item.SubItems.Add(ObjectTypeDetector.Detect(entry.Name, entry.IsDirectory));
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
        var node = FindProfileNode(_currentProfile);
        if (node is not null) await LoadBucketsAsync(_currentProfile, node);
    }

    private TreeNode? FindProfileNode(ConnectionProfile profile) =>
        _tree.Nodes[0].Nodes.Cast<TreeNode>()
            .FirstOrDefault(item => item.Tag is ConnectionProfile candidate && candidate.Id == profile.Id);

    private async Task NavigateUpAsync()
    {
        if (_currentProfile is null || _currentBucket is null) return;
        var parent = S3Path.ParentPrefix(_currentPrefix);
        await NavigateAsync(_currentProfile, _currentBucket, parent, true);
    }

    private async Task NavigateAddressAsync()
    {
        if (!S3Location.TryParse(_address.Text, out var location))
        {
            MessageBox.Show(this, "S3 路径格式应为 s3://<连接名称>/<bucket>/<prefix>。", "地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var profile = _profiles.FirstOrDefault(item => string.Equals(item.Name, location.Profile, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            MessageBox.Show(this, $"找不到连接：{location.Profile}", "地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (location.Bucket is null)
        {
            var node = FindProfileNode(profile);
            if (node is not null)
            {
                _tree.SelectedNode = node;
                await LoadBucketsAsync(profile, node);
            }
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
        if (profile is null) return;
        if (location.Bucket is null)
        {
            var node = FindProfileNode(profile);
            if (node is not null)
            {
                _tree.SelectedNode = node;
                await LoadBucketsAsync(profile, node);
            }
            return;
        }
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

    private async Task UploadPathsAsync(IEnumerable<string> paths)
    {
        if (!EnsureLocation()) return;
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var prefix = _currentPrefix;

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                await EnqueueUploadAsync(path, prefix + Path.GetFileName(path));
                continue;
            }
            if (!Directory.Exists(path))
                continue;

            var rootName = new DirectoryInfo(path).Name;
            var batch = await _transfers.CreateBatchAsync(
                profile,
                bucket,
                $"上传 {rootName}",
                path,
                TransferDirection.Upload);
            var chunk = new List<UploadBatchItem>(256);
            var skipped = 0;
            try
            {
                foreach (var file in EnumerateFilesSafely(path, (directory, exception) =>
                         {
                             skipped++;
                             _logger.Error($"Folder upload discovery skipped directory={directory}", exception);
                         }))
                {
                    var info = new FileInfo(file);
                    var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    chunk.Add(new UploadBatchItem(
                        file,
                        S3Path.Combine(prefix, $"{rootName}/{relative}"),
                        relative,
                        info.Length,
                        profile.DefaultStorageClass));
                    if (chunk.Count < 256)
                        continue;
                    await _transfers.AddUploadBatchItemsAsync(batch, chunk);
                    chunk.Clear();
                }
                if (chunk.Count > 0)
                    await _transfers.AddUploadBatchItemsAsync(batch, chunk);
            }
            catch (Exception exception)
            {
                skipped++;
                _logger.Error($"Folder upload discovery failed root={path}", exception);
                MessageBox.Show(
                    this,
                    $"文件夹发现提前停止：{exception.Message}\n\n已发现的文件仍会继续传输。",
                    "文件夹上传",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                await _transfers.CompleteBatchDiscoveryAsync(batch.Id, skipped);
            }
            _logger.Info($"Upload batch queued profile={profile.Name} bucket={bucket} batch={batch.Id} root={path} skipped={skipped}");
        }
        SetTransferVisibility(true);
    }

    private async Task EnqueueUploadAsync(string localPath, string key)
    {
        var file = new FileInfo(localPath);
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        await _transfers.EnqueueUploadAsync(
            profile, bucket, key, localPath, file.Length, profile.DefaultStorageClass);
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
            await EnqueueDownloadAsync(selected[0], save.FileName);
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

        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        var batchName = selected.Count == 1
            ? $"下载 {selected[0].Name}"
            : $"下载 {selected.Count:N0} 项";
        var batch = await _transfers.CreateBatchAsync(
            profile,
            bucket,
            batchName,
            targetRoot,
            TransferDirection.Download);
        var chunk = new List<DownloadBatchItem>(256);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        try
        {
            foreach (var entry in selected)
            {
                if (!entry.IsDirectory)
                {
                    if (seenKeys.Add(entry.Key))
                    {
                        var relative = entry.Name;
                        chunk.Add(new DownloadBatchItem(
                            entry.Key,
                            LocalObjectPath.MapRelativeKey(targetRoot, relative),
                            relative,
                            entry.Size));
                    }
                }
                else
                {
                    await foreach (var child in EnumerateAllObjectsAsync(entry.Key))
                    {
                        if (child.IsDirectory || !seenKeys.Add(child.Key))
                            continue;
                        if (!child.Key.StartsWith(entry.Key, StringComparison.Ordinal))
                            throw new InvalidOperationException("对象 Key 不属于所选文件夹。");

                        var childRelative = child.Key[entry.Key.Length..].TrimStart('/');
                        var relative = $"{entry.Name}/{childRelative}";
                        chunk.Add(new DownloadBatchItem(
                            child.Key,
                            LocalObjectPath.MapRelativeKey(targetRoot, relative),
                            relative,
                            child.Size));
                        if (chunk.Count < 256)
                            continue;
                        await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
                        chunk.Clear();
                    }
                }

                if (chunk.Count >= 256)
                {
                    await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0)
                await _transfers.AddDownloadBatchItemsAsync(batch, chunk);
        }
        catch (Exception exception)
        {
            skipped++;
            _logger.Error($"Folder download discovery failed target={targetRoot}", exception);
            MessageBox.Show(
                this,
                $"递归发现提前停止：{exception.Message}\n\n已发现的对象仍会继续下载。",
                "文件夹下载",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            await _transfers.CompleteBatchDiscoveryAsync(batch.Id, skipped);
        }

        _logger.Info($"Download batch queued profile={profile.Name} bucket={bucket} batch={batch.Id} target={targetRoot} skipped={skipped}");
        SetTransferVisibility(true);
    }

    private async Task EnqueueDownloadAsync(S3ObjectEntry entry, string localPath)
    {
        localPath = LocalObjectPath.ToExtendedLengthPath(localPath);
        var profile = _currentProfile!;
        var bucket = _currentBucket!;
        await _transfers.EnqueueDownloadAsync(profile, bucket, entry.Key, localPath, entry.Size);
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

    private static IEnumerable<string> EnumerateFilesSafely(
        string root,
        Action<string, Exception> onDirectoryError)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                onDirectoryError(directory, exception);
                continue;
            }

            foreach (var file in files)
                yield return file;

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                onDirectoryError(directory, exception);
                continue;
            }
            for (var index = children.Length - 1; index >= 0; index--)
                pending.Push(children[index]);
        }
    }

    private async IAsyncEnumerable<S3ObjectEntry> EnumerateAllObjectsAsync(string prefix)
    {
        var profile = _currentProfile ?? throw new InvalidOperationException("当前连接已断开。");
        var bucket = _currentBucket ?? throw new InvalidOperationException("当前 Bucket 已关闭。");
        var limit = Math.Clamp(
            _settings.ObjectCacheLimit,
            ObjectListingLimits.MinimumCacheLimit,
            ObjectListingLimits.MaximumCacheLimit);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;

        while (true)
        {
            var page = await _storage.ListObjectsAsync(
                profile,
                bucket,
                prefix,
                token,
                Math.Clamp(
                    _settings.ObjectPageSize,
                    ObjectListingLimits.MinimumPageSize,
                    ObjectListingLimits.MaximumPageSize),
                CancellationToken.None);
            foreach (var item in page.Items)
            {
                if (!seenKeys.Add(item.Key))
                    continue;
                if (seenKeys.Count > limit)
                    throw new InvalidOperationException(
                        $"文件夹对象数量达到内存保护上限 {limit:N0}，已停止递归下载。");
                yield return item;
            }

            if (!page.HasMore)
                yield break;
            var nextToken = page.ContinuationToken;
            if (string.IsNullOrEmpty(nextToken) || !seenTokens.Add(nextToken))
                throw new InvalidOperationException("对象列表分页令牌无效或重复，已停止递归下载。");
            token = nextToken;
        }
    }

    private async Task<IReadOnlyList<S3ObjectEntry>> ListAllObjectsAsync(string prefix)
    {
        var profile = _currentProfile ?? throw new InvalidOperationException("当前连接已断开。");
        var bucket = _currentBucket ?? throw new InvalidOperationException("当前 Bucket 已关闭。");
        var cache = new BoundedObjectCache(Math.Clamp(
            _settings.ObjectCacheLimit,
            ObjectListingLimits.MinimumCacheLimit,
            ObjectListingLimits.MaximumCacheLimit));
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;

        while (true)
        {
            var page = await _storage.ListObjectsAsync(
                profile,
                bucket,
                prefix,
                token,
                Math.Clamp(
                    _settings.ObjectPageSize,
                    ObjectListingLimits.MinimumPageSize,
                    ObjectListingLimits.MaximumPageSize),
                CancellationToken.None);
            var addResult = cache.AddRange(page.Items);
            if (addResult.Truncated || (cache.LimitReached && page.HasMore))
            {
                throw new InvalidOperationException(
                    $"文件夹对象数量达到内存保护上限 {cache.Limit:N0}，已停止递归下载。");
            }

            if (!page.HasMore)
                return cache.Items.ToArray();

            var nextToken = page.ContinuationToken;
            if (string.IsNullOrEmpty(nextToken) || !seenTokens.Add(nextToken))
                throw new InvalidOperationException("对象列表分页令牌无效或重复，已停止递归下载。");
            token = nextToken;
        }
    }

    private async Task ShowSettingsAsync()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _settings = dialog.Settings;
        _transferRuntime.Apply(_settings);
        _transfers.ConfigureRetryPolicy(_settings.RetryCount, _settings.RetryDelaySeconds);
        await _transfers.SetConcurrencyAsync(_settings.ConcurrentTransfers);
        await SaveSettingsAsync();
        if (_currentProfile is not null && _currentBucket is not null)
            await LoadObjectsPageAsync(true);
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

    private void ShowIncompleteMultipartUploads()
    {
        if (!EnsureLocation()) return;
        using var dialog = new MultipartUploadManagerDialog(
            _currentProfile!, _currentBucket!, _storage, _transferQueue, _logger);
        dialog.ShowDialog(this);
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
        _navigationCancellation.CancelCurrent();
    }

    private ToolStripMenuItem Command(string id, string text, EventHandler handler, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text, null, handler) { ShortcutKeys = shortcut };
        _commands[id] = item;
        return item;
    }

    private static ToolStripMenuItem Unsupported(string text) =>
        new(text) { Enabled = false, ToolTipText = "当前版本尚未支持" };

    private void AddToolbarButton(string id, string text, UiIconKind icon, EventHandler handler)
    {
        var button = new ToolStripButton(text, UiIcons.Create(icon), handler)
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
        e.Cancel = true;

        var closeAction = TransferCloseAction.Pause;
        if (_transfers.ActiveCount > 0)
        {
            using var dialog = new TransferCloseDialog(_transfers.ActiveCount);
            dialog.ShowDialog(this);
            closeAction = dialog.SelectedAction;
            if (closeAction == TransferCloseAction.Return)
                return;
        }

        try
        {
            CancelNavigation();
            _speedTimer.Stop();
            _requestStatus.Text = closeAction switch
            {
                TransferCloseAction.Wait => "等待传输完成...",
                TransferCloseAction.Cancel => "正在取消传输...",
                _ => "正在暂停传输..."
            };

            switch (closeAction)
            {
                case TransferCloseAction.Wait:
                    await _transfers.WaitForIdleAsync();
                    break;
                case TransferCloseAction.Cancel:
                    await _transfers.CancelAllAsync();
                    await _transfers.WaitForIdleAsync();
                    break;
                case TransferCloseAction.Pause:
                    await _transfers.PauseAllAsync();
                    await _transfers.WaitForIdleAsync();
                    break;
            }

            await SaveSettingsAsync();
            await _transferQueue.DisposeAsync();
            _closing = true;
            BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to close transfer queue safely", exception);
            _speedTimer.Start();
            ErrorDialog.ShowException(this, "无法安全退出", "保存传输队列", exception);
        }
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
