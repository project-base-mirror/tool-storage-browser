using System.ComponentModel;
using System.Diagnostics;
using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

internal sealed record BucketNodeTag(ConnectionProfile Profile, string Bucket);
internal sealed record LoadMoreTag;
internal sealed record ParentDirectoryTag;
internal sealed record ObjectClipboardEntry(string Key, string Name, bool IsDirectory, long Size);
internal sealed record ObjectClipboardPayload(
    Guid ProfileId, string ProfileName, string SourceBucket,
    IReadOnlyList<ObjectClipboardEntry> Entries, bool Move);

internal sealed partial class MainForm : Form
{
    private static readonly TimeSpan ShutdownStepTimeout = TimeSpan.FromSeconds(60);
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
    private readonly AutomationSession? _automation;

    private readonly MenuStrip _menu = new() { Name = "MainMenu" };
    private readonly ToolStrip _toolbar = new() { Name = "MainToolbar", GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(22, 22), Padding = new Padding(3, 2, 3, 2) };
    private readonly ToolStrip _addressStrip = new() { Name = "AddressStrip", GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(18, 18) };
    private readonly ToolStripTextBox _address = new() { Name = "AddressBox", AutoSize = false, Width = 620 };
    private readonly ToolStripTextBox _search = new() { Name = "SearchBox", AutoSize = false, Width = 220, ToolTipText = "过滤当前已加载列表（Ctrl+F）" };
    private readonly SplitContainer _outerSplit = new() { Name = "MainLayout", Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
    private readonly SplitContainer _mainSplit = new() { Name = "NavigationLayout", Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1 };
    private readonly TreeView _tree = new() { Name = "AccountTree", Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
    private readonly ListView _objects = new()
    {
        Name = "ObjectList",
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
    private readonly ContextMenuStrip _groupMenu = new();
    private readonly ContextMenuStrip _bucketMenu = new();
    private readonly ContextMenuStrip _objectMenu = new();
    private readonly PersistentTransferQueue _transferQueue;
    private readonly TransferRuntimeConfiguration _transferRuntime;
    private readonly IFolderSyncJobStore _syncJobStore;
    private readonly GitHubUpdateChecker _updateChecker;
    private readonly ConfigurationTransactionCoordinator _configurationTransactions;
    private readonly ConnectionArchiveService _connectionArchive = new();
    private readonly TransferQueueControl _transfers;
    private readonly StatusStrip _status = new() { Name = "StatusBar" };
    private readonly ToolStripStatusLabel _connectionStatus = new("未连接");
    private readonly ToolStripStatusLabel _pathStatus = new("s3://");
    private readonly ToolStripStatusLabel _objectStatus = new("0 个对象");
    private readonly ToolStripStatusLabel _selectionStatus = new("已选择 0 个");
    private readonly ToolStripStatusLabel _requestStatus = new("空闲") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly ToolStripStatusLabel _uploadSpeed = new("↑ 0 B/s");
    private readonly ToolStripStatusLabel _downloadSpeed = new("↓ 0 B/s");
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _speedTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _trayNotificationTimer = new() { Interval = 750 };
    private readonly BoundedObjectCache _loadedItems = new(ObjectListingLimits.DefaultCacheLimit);
    private readonly NavigationHistoryCoordinator _navigationHistory = new();
    private readonly Dictionary<string, ToolStripItem> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly OperationCancellation _navigationCancellation = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private NotifyIcon? _trayIcon;
    private TrayNotificationForm? _trayNotification;

    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private IReadOnlyList<ConnectionGroup> _profileGroups = [];
    private AppSettings _settings = new();
    private ConnectionProfile? _currentProfile;
    private string? _currentBucket;
    private string _currentPrefix = string.Empty;
    private string? _continuationToken;
    private bool _hasMore;
    private bool _objectLimitReached;
    private int _sortColumn;
    private bool _sortAscending = true;
    private long _navigationRevision;
    private bool _closing;
    private bool _suppressTreeSelection;
    private bool _populatingProfiles;
    private ObjectClipboardPayload? _objectClipboard;
    private bool _updateCheckInProgress;
    private bool _exitRequested;
    private int _trayCompletedTransfers;
    private int _trayFailedTransfers;

    public MainForm(
        IProfileStore profileStore,
        IS3StorageService storage,
        AppSettingsStore settingsStore,
        SimpleFileLogger logger,
        PersistentTransferQueue transferQueue,
        TransferRuntimeConfiguration transferRuntime,
        IFolderSyncJobStore syncJobStore,
        GitHubUpdateChecker updateChecker,
        ICdnConfigurationStore cdnConfigurationStore,
        ICdnCredentialStore cdnCredentialStore,
        ICdnDeliveryService cdnDeliveryService,
        PersistentCdnJobQueue cdnJobQueue,
        ICdnCertificateInspector cdnCertificateInspector,
        ConfigurationTransactionCoordinator configurationTransactions,
        AutomationSession? automation = null)
    {
        _profileStore = profileStore;
        _storage = storage;
        _settingsStore = settingsStore;
        _logger = logger;
        _transferQueue = transferQueue;
        _transferRuntime = transferRuntime;
        _syncJobStore = syncJobStore;
        _updateChecker = updateChecker;
        _cdnConfigurationStore = cdnConfigurationStore;
        _cdnCredentialStore = cdnCredentialStore;
        _cdnDeliveryService = cdnDeliveryService;
        _cdnJobQueue = cdnJobQueue;
        _cdnUploadAutomation = new CdnUploadAutomationCoordinator(cdnJobQueue);
        _cdnCertificateInspector = cdnCertificateInspector;
        _configurationTransactions = configurationTransactions;
        _automation = automation;
        _transfers = new TransferQueueControl(transferQueue) { Name = "TransferQueue" };

        Name = "MainWindow";
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
        BuildGroupContextMenu();
        BuildBucketContextMenu();
        BuildObjectContextMenu();
        BuildStatus();
        WireEvents();
        if (_automation is null)
            BuildTray();

        Controls.Add(_outerSplit);
        Controls.Add(_addressStrip);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        Shown += async (_, _) =>
        {
            try
            {
                await InitializeAsync();
                _automation?.Ready(this);
                if (_automation is null && _settings.CheckForUpdatesOnStartup)
                    _ = CheckForUpdatesAsync(automatic: true);
            }
            catch (Exception exception)
            {
                _logger.Error("Main window initialization failed", exception);
                if (_automation is not null)
                    _automation.Fail(this, exception);
                else
                    ErrorDialog.ShowException(this, "启动未完成", "初始化主窗口", exception);
            }
        };
        FormClosing += MainForm_FormClosing;
        FormClosed += (_, _) =>
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _trayNotification?.Close();
            _trayNotification?.Dispose();
            _trayNotificationTimer.Dispose();
            _automation?.MarkStopped(this);
        };
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add(Command("new-connection", "新建连接...", async (_, _) => await RunUiCommandAsync("新建连接", NewConnectionAsync), Keys.Control | Keys.N));
        file.DropDownItems.Add(Command("new-connection-group", "新建连接分组...", async (_, _) => await RunUiCommandAsync("新建连接分组", NewConnectionGroupAsync)));
        file.DropDownItems.Add(Command("edit-connection", "编辑当前连接...", async (_, _) => await RunUiCommandAsync("编辑连接", EditCurrentConnectionAsync)));
        file.DropDownItems.Add(Command("copy-connection", "复制当前连接", async (_, _) => await RunUiCommandAsync("复制连接", CopyCurrentConnectionAsync)));
        file.DropDownItems.Add(Command("delete-connection", "删除当前连接", async (_, _) => await RunUiCommandAsync("删除连接", DeleteCurrentConnectionAsync)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Command("connect", "连接", async (_, _) => await ConnectSelectedAsync()));
        file.DropDownItems.Add(Command("disconnect", "断开连接", (_, _) => Disconnect()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Command("import-connections", "导入连接...", async (_, _) => await ImportConnectionsAsync()));
        file.DropDownItems.Add(Command("export-connection", "导出当前连接...", async (_, _) => await ExportConnectionsAsync(exportAll: false)));
        file.DropDownItems.Add(Command("export-all-connections", "导出全部连接...", async (_, _) => await ExportConnectionsAsync(exportAll: true)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("退出", null, (_, _) => RequestExit()));

        var edit = new ToolStripMenuItem("编辑(&E)");
        edit.DropDownItems.Add(new ToolStripMenuItem("全选", null, (_, _) => SelectAllObjects(), Keys.Control | Keys.A));
        edit.DropDownItems.Add(Command("clipboard-copy", "复制", (_, _) => CopySelectionToObjectClipboard(false), Keys.Control | Keys.C));
        edit.DropDownItems.Add(Command("clipboard-cut", "剪切", (_, _) => CopySelectionToObjectClipboard(true), Keys.Control | Keys.X));
        edit.DropDownItems.Add(Command("clipboard-paste", "粘贴", async (_, _) => await PasteObjectClipboardAsync(), Keys.Control | Keys.V));
        edit.DropDownItems.Add(new ToolStripSeparator());
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
        view.DropDownItems.Add(Command("show-versions", "显示版本...", async (_, _) => await ShowObjectVersionsAsync(false)));
        view.DropDownItems.Add(Unsupported("列设置..."));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Command("back", "返回", async (_, _) => await NavigateHistoryAsync(-1), Keys.Alt | Keys.Left));
        view.DropDownItems.Add(Command("forward", "前进", async (_, _) => await NavigateHistoryAsync(1), Keys.Alt | Keys.Right));
        view.DropDownItems.Add(Command("up", "上一级", async (_, _) => await NavigateUpAsync(), Keys.Alt | Keys.Up));

        var bucket = new ToolStripMenuItem("Bucket(&B)");
        bucket.DropDownItems.Add(Command("create-bucket", "新建 Bucket...", async (_, _) => await CreateBucketAsync()));
        bucket.DropDownItems.Add(Command("delete-bucket", "删除 Bucket...", async (_, _) => await DeleteBucketAsync()));
        bucket.DropDownItems.Add(Command("bucket-properties", "Bucket 属性...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Overview)));
        bucket.DropDownItems.Add(Command("bucket-acl", "Bucket 权限...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Acl)));
        bucket.DropDownItems.Add(Command("bucket-policy", "Bucket Policy...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Policy)));
        bucket.DropDownItems.Add(Command("bucket-cors", "CORS 配置...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Cors)));
        bucket.DropDownItems.Add(Command("bucket-versioning", "版本控制...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Versioning)));
        bucket.DropDownItems.Add(Command("bucket-encryption", "默认加密...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Encryption)));
        bucket.DropDownItems.Add(Command("bucket-tags", "Bucket Tags...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Tags)));
        bucket.DropDownItems.Add(Command("bucket-lifecycle", "生命周期规则...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.Lifecycle)));
        bucket.DropDownItems.Add(Command("bucket-access-controls", "Public Access Block / Object Ownership...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.AccessControls)));
        bucket.DropDownItems.Add(Command("bucket-object-lock", "Object Lock...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.ObjectLock)));
        bucket.DropDownItems.Add(Command("empty-bucket", "清空 Bucket...", async (_, _) => await ShowBucketManagementAsync(BucketManagementPage.EmptyBucket)));
        bucket.DropDownItems.Add(new ToolStripSeparator());
        bucket.DropDownItems.Add(Command("refresh-buckets", "刷新 Bucket 列表", async (_, _) => await ReloadBucketsAsync()));

        var objects = new ToolStripMenuItem("对象(&O)");
        objects.DropDownItems.Add(Command("upload-file", "上传文件...", async (_, _) => await UploadFilesAsync(), Keys.Control | Keys.U));
        objects.DropDownItems.Add(Command("upload-folder", "上传文件夹...", async (_, _) => await UploadFolderAsync(), Keys.Control | Keys.Shift | Keys.U));
        objects.DropDownItems.Add(Command("download", "下载...", async (_, _) => await DownloadSelectedAsync(), Keys.Control | Keys.D));
        objects.DropDownItems.Add(Command("create-folder", "新建文件夹...", async (_, _) => await CreateFolderAsync()));
        objects.DropDownItems.Add(new ToolStripSeparator());
        objects.DropDownItems.Add(Command("copy-object", "复制到...", async (_, _) => await CopyOrMoveSelectedAsync(false), Keys.Control | Keys.Shift | Keys.C));
        objects.DropDownItems.Add(Command("move-object", "移动到...", async (_, _) => await CopyOrMoveSelectedAsync(true), Keys.Control | Keys.Shift | Keys.X));
        objects.DropDownItems.Add(Command("rename-object", "重命名...", async (_, _) => await RenameSelectedAsync()));
        objects.DropDownItems.Add(Command("delete-object-menu", "删除", async (_, _) => await DeleteSelectedAsync()));
        objects.DropDownItems.Add(Command("properties-menu", "属性...", async (_, _) => await ShowPropertiesAsync()));
        objects.DropDownItems.Add(Command("metadata", "Metadata...", async (_, _) => await ShowPropertiesAsync()));
        objects.DropDownItems.Add(Command("batch-metadata", "批量 Header / Metadata...", async (_, _) => await EditBatchMetadataAsync()));
        foreach (var text in new[] { "权限...", "更改存储类型...", "公开访问", "取消公开访问" })
            objects.DropDownItems.Add(Unsupported(text));
        objects.DropDownItems.Add(Command("object-versions", "查看 / 恢复历史版本...", async (_, _) => await ShowObjectVersionsAsync(true)));
        objects.DropDownItems.Add(Command("presign", "生成预签名 URL...", (_, _) => ShowPresignedUrl()));

        var tools = new ToolStripMenuItem("工具(&T)");
        tools.DropDownItems.Add(Command("transfer-queue", "传输队列", (_, _) => SetTransferVisibility(true)));
        tools.DropDownItems.Add(Command("failed-transfers", "失败任务", (_, _) => SetTransferVisibility(true)));
        tools.DropDownItems.Add(Command("multipart-uploads", "未完成的分片上传...", (_, _) => ShowIncompleteMultipartUploads()));
        tools.DropDownItems.Add(Command("folder-sync", "文件夹同步...", (_, _) => ShowFolderSync()));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Command("settings", "选项...", async (_, _) => await ShowSettingsAsync()));
        tools.DropDownItems.Add(Command("logs", "查看日志", (_, _) => OpenLog()));
        tools.DropDownItems.Add(Command("clear-cache", "清理缓存", (_, _) => MessageBox.Show(this, "当前版本没有持久对象缓存。", "清理缓存")));
        tools.DropDownItems.Add(Command("diagnostics", "网络诊断", async (_, _) => await TestCurrentConnectionAsync()));
        tools.DropDownItems.Add(Command("check-updates", "检查更新...", async (_, _) => await CheckForUpdatesAsync(automatic: false)));

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add(Command("help", "使用说明", (_, _) => OpenProjectFile("README.md")));
        help.DropDownItems.Add(new ToolStripMenuItem("快捷键", null, (_, _) => ShowShortcuts()));
        help.DropDownItems.Add(Command("project-home", "打开项目主页", (_, _) => OpenExternalUrl(ProjectLinks.Homepage)));
        help.DropDownItems.Add(Command("report-issue", "报告问题", (_, _) => OpenExternalUrl(ProjectLinks.Issues)));
        help.DropDownItems.Add(new ToolStripMenuItem("关于", null, (_, _) =>
            MessageBox.Show(this, $"S3 Explorer v{Application.ProductVersion}\n\n原生 Windows S3 / S3-compatible 对象存储管理工具。\n.NET 10 · WinForms · AWS SDK for .NET", "关于", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.AddRange([file, edit, view, bucket, objects, BuildCdnMenu(), tools, help]);
    }

    private void BuildToolbar()
    {
        AddToolbarButton("new-connection", "新建连接", UiIconKind.NewConnection, async (_, _) => await RunUiCommandAsync("新建连接", NewConnectionAsync));
        AddToolbarButton("connect-toolbar", "连接/断开", UiIconKind.Connect, async (_, _) =>
        {
            if (_currentProfile is null) await ConnectSelectedAsync(); else Disconnect();
        });
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolbarButton("back-toolbar", "返回 (Alt+Left)", UiIconKind.Back, async (_, _) => await NavigateHistoryAsync(-1));
        AddToolbarButton("forward-toolbar", "前进 (Alt+Right)", UiIconKind.Forward, async (_, _) => await NavigateHistoryAsync(1));
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
        AddToolbarButton("folder-sync-toolbar", "文件夹同步", UiIconKind.Sync, (_, _) => ShowFolderSync());
        AddToolbarButton("settings-toolbar", "设置", UiIconKind.Settings, async (_, _) => await ShowSettingsAsync());
        _toolbar.Dock = DockStyle.Top;
    }

    private void BuildAddressBar()
    {
        var back = new ToolStripButton(UiIcons.Create(UiIconKind.Back, 18)) { ToolTipText = "返回" };
        back.Click += async (_, _) => await NavigateHistoryAsync(-1);
        var forward = new ToolStripButton(UiIcons.Create(UiIconKind.Forward, 18)) { ToolTipText = "前进" };
        forward.Click += async (_, _) => await NavigateHistoryAsync(1);
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
        edit.Click += async (_, _) => await RunUiCommandAsync("编辑连接", EditCurrentConnectionAsync);
        var copy = new ToolStripMenuItem("复制连接", UiIcons.Create(UiIconKind.Copy, 16));
        copy.Click += async (_, _) => await RunUiCommandAsync("复制连接", CopyCurrentConnectionAsync);
        var export = new ToolStripMenuItem("导出此连接...", UiIcons.Create(UiIconKind.Download, 16));
        export.Click += async (_, _) => await ExportConnectionsAsync(exportAll: false);
        var moveToGroup = new ToolStripMenuItem("移动到分组");
        var moveUp = new ToolStripMenuItem("上移");
        moveUp.Click += async (_, _) => await MoveSelectedProfileAsync(-1);
        var moveDown = new ToolStripMenuItem("下移");
        moveDown.Click += async (_, _) => await MoveSelectedProfileAsync(1);
        var delete = new ToolStripMenuItem("删除", UiIcons.Create(UiIconKind.Delete, 16));
        delete.Click += async (_, _) => await DeleteCurrentConnectionAsync();
        _accountMenu.Items.AddRange([connect, edit, copy, export, new ToolStripSeparator(), moveToGroup, moveUp, moveDown, new ToolStripSeparator(), delete]);
        _accountMenu.Opening += (_, args) =>
        {
            var profile = SelectedTreeProfile();
            var accountSelected = profile is not null && _tree.SelectedNode?.Tag is not BucketNodeTag;
            args.Cancel = !accountSelected;
            connect.Enabled = accountSelected;
            edit.Enabled = accountSelected;
            copy.Enabled = accountSelected;
            export.Enabled = accountSelected;
            delete.Enabled = accountSelected;
            moveToGroup.DropDownItems.Clear();
            if (profile is not null)
            {
                moveToGroup.DropDownItems.Add(CreateMoveToGroupItem("未分组", null, profile));
                if (_profileGroups.Count > 0) moveToGroup.DropDownItems.Add(new ToolStripSeparator());
                foreach (var group in _profileGroups.OrderBy(group => group.SortOrder))
                    moveToGroup.DropDownItems.Add(CreateMoveToGroupItem(group.Name, group.Id, profile));
                var members = _profiles.Where(item => item.GroupId == profile.GroupId).OrderBy(item => item.SortOrder).ToArray();
                var index = Array.FindIndex(members, item => item.Id == profile.Id);
                moveUp.Enabled = index > 0;
                moveDown.Enabled = index >= 0 && index < members.Length - 1;
            }
        };
    }

    private ToolStripMenuItem CreateMoveToGroupItem(string text, Guid? groupId, ConnectionProfile profile)
    {
        var item = new ToolStripMenuItem(text) { Checked = profile.GroupId == groupId };
        item.Click += async (_, _) => await MoveProfileToGroupAsync(profile, groupId);
        return item;
    }

    private void BuildGroupContextMenu()
    {
        _groupMenu.Items.Add("新建连接...", UiIcons.Create(UiIconKind.NewConnection, 16),
            async (_, _) => await RunUiCommandAsync("新建连接", NewConnectionAsync));
        _groupMenu.Items.Add("重命名...", null, async (_, _) => await RenameSelectedGroupAsync());
        _groupMenu.Items.Add(new ToolStripSeparator());
        _groupMenu.Items.Add("上移", null, async (_, _) => await MoveSelectedGroupAsync(-1));
        _groupMenu.Items.Add("下移", null, async (_, _) => await MoveSelectedGroupAsync(1));
        _groupMenu.Items.Add(new ToolStripSeparator());
        _groupMenu.Items.Add("删除分组", UiIcons.Create(UiIconKind.Delete, 16), async (_, _) => await DeleteSelectedGroupAsync());
        _groupMenu.Opening += (_, args) =>
        {
            var group = _tree.SelectedNode?.Tag as ConnectionGroup;
            args.Cancel = group is null;
            if (group is null) return;
            var ordered = _profileGroups.OrderBy(item => item.SortOrder).ToArray();
            var index = Array.FindIndex(ordered, item => item.Id == group.Id);
            _groupMenu.Items[3].Enabled = index > 0;
            _groupMenu.Items[4].Enabled = index >= 0 && index < ordered.Length - 1;
        };
    }

    private void BuildObjectContextMenu()
    {
        _objectMenu.Items.Add("复制", UiIcons.Create(UiIconKind.Copy, 16), (_, _) => CopySelectionToObjectClipboard(false));
        _objectMenu.Items.Add("剪切", UiIcons.Create(UiIconKind.Move, 16), (_, _) => CopySelectionToObjectClipboard(true));
        _objectMenu.Items.Add("粘贴", UiIcons.Create(UiIconKind.Copy, 16), async (_, _) => await PasteObjectClipboardAsync());
        _objectMenu.Items.Add(new ToolStripSeparator());
        _objectMenu.Items.Add("复制到...", UiIcons.Create(UiIconKind.Copy, 16), async (_, _) => await CopyOrMoveSelectedAsync(false));
        _objectMenu.Items.Add("移动到...", UiIcons.Create(UiIconKind.Move, 16), async (_, _) => await CopyOrMoveSelectedAsync(true));
        _objectMenu.Items.Add("重命名...", UiIcons.Create(UiIconKind.Properties, 16), async (_, _) => await RenameSelectedAsync());
        _objectMenu.Items.Add("删除", UiIcons.Create(UiIconKind.Delete, 16), async (_, _) => await DeleteSelectedAsync());
        _objectMenu.Items.Add(new ToolStripSeparator());
        _objectMenu.Items.Add("属性...", UiIcons.Create(UiIconKind.Properties, 16), async (_, _) => await ShowPropertiesAsync());
        _objectMenu.Items.Add("批量 Header / Metadata...", UiIcons.Create(UiIconKind.Properties, 16), async (_, _) => await EditBatchMetadataAsync());
        _objectMenu.Items.Add("查看 / 恢复历史版本...", UiIcons.Create(UiIconKind.Properties, 16), async (_, _) => await ShowObjectVersionsAsync(true));
        _objectMenu.Items.Add(new ToolStripSeparator());
        _objectMenu.Items.Add(BuildObjectCdnContextMenu());
        _objectMenu.Opening += (_, _) =>
        {
            var entries = SelectedEntries();
            var any = entries.Count > 0;
            var oneFile = entries.Count == 1 && !entries[0].IsDirectory;
            var capabilities = _currentProfile is null
                ? null
                : S3ProviderCapabilityRegistry.For(_currentProfile.ServiceType).Object;
            _objectMenu.Items[0].Enabled = any;
            _objectMenu.Items[1].Enabled = any;
            _objectMenu.Items[2].Enabled = _objectClipboard is not null && EnsureClipboardProfile(false);
            _objectMenu.Items[4].Enabled = any;
            _objectMenu.Items[5].Enabled = any;
            _objectMenu.Items[11].Enabled = oneFile && capabilities?.VersionOperations.Supported == true;
            UpdateCdnContextCommandStates();
        };
    }

    private void BuildBucketContextMenu()
    {
        var open = ContextCommand("open-bucket", "打开 Bucket", UiIconKind.Bucket, async (_, _) => await OpenSelectedBucketAsync());
        var refresh = ContextCommand("refresh", "刷新对象", UiIconKind.Refresh, async (_, _) =>
            await InSelectedBucketAsync(RefreshAsync));
        var create = ContextCommand("create-bucket", "新建 Bucket...", UiIconKind.Bucket, async (_, _) =>
            await InSelectedBucketAsync(CreateBucketAsync));
        var uploadFile = ContextCommand("upload-file", "上传文件...", UiIconKind.Upload, async (_, _) =>
            await InSelectedBucketAsync(UploadFilesAsync));
        var uploadFolder = ContextCommand("upload-folder", "上传文件夹...", UiIconKind.Folder, async (_, _) =>
            await InSelectedBucketAsync(UploadFolderAsync));
        var downloadAll = ContextCommand("download", "下载整个 Bucket...", UiIconKind.Download, async (_, _) =>
            await DownloadSelectedBucketAsync());
        var sync = ContextCommand("folder-sync", "文件夹同步...", UiIconKind.Sync, async (_, _) =>
            await InSelectedBucketAsync(() =>
            {
                ShowFolderSync();
                return Task.CompletedTask;
            }));
        var acl = ContextCommand("bucket-acl", "Bucket 权限 (ACL)...", UiIconKind.Info, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Acl));
        var policy = ContextCommand("bucket-policy", "Bucket Policy...", UiIconKind.Info, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Policy));
        var accessControls = ContextCommand("bucket-access-controls", "Public Access Block / Object Ownership...", UiIconKind.Info, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.AccessControls));
        var cors = ContextCommand("bucket-cors", "CORS 配置...", UiIconKind.Properties, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Cors));
        var versioning = ContextCommand("bucket-versioning", "版本控制...", UiIconKind.Properties, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Versioning));
        var encryption = ContextCommand("bucket-encryption", "默认加密...", UiIconKind.Properties, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Encryption));
        var tags = ContextCommand("bucket-tags", "Bucket Tags...", UiIconKind.Properties, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Tags));
        var lifecycle = ContextCommand("bucket-lifecycle", "生命周期配置...", UiIconKind.Properties, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Lifecycle));
        var objectLock = ContextCommand("bucket-object-lock", "Object Lock...", UiIconKind.Info, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.ObjectLock));
        var empty = ContextCommand("empty-bucket", "清空 Bucket...", UiIconKind.Delete, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.EmptyBucket));
        var delete = ContextCommand("delete-bucket", "删除 Bucket...", UiIconKind.Delete, async (_, _) => await DeleteBucketAsync());
        var properties = ContextCommand("bucket-properties", "属性...", UiIconKind.Properties, async (_, _) =>
            await ShowBucketManagementAsync(BucketManagementPage.Overview));

        _bucketMenu.Items.AddRange([
            open,
            refresh,
            create,
            new ToolStripSeparator(),
            uploadFile,
            uploadFolder,
            downloadAll,
            sync,
            new ToolStripSeparator(),
            acl,
            policy,
            accessControls,
            new ToolStripSeparator(),
            BuildBucketCdnContextMenu(),
            new ToolStripSeparator(),
            versioning,
            encryption,
            lifecycle,
            cors,
            objectLock,
            tags,
            Unsupported("日志设置..."),
            Unsupported("跨区域复制..."),
            new ToolStripSeparator(),
            empty,
            delete,
            properties
        ]);
        open.Font = new Font(open.Font, FontStyle.Bold);
        _bucketMenu.Opening += (_, args) => args.Cancel = _tree.SelectedNode?.Tag is not BucketNodeTag;
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
        _tree.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Right) return;
            var node = _tree.GetNodeAt(args.Location);
            if (node is null) return;
            _tree.SelectedNode = node;
            _tree.ContextMenuStrip = node.Tag switch
            {
                ConnectionProfile => _accountMenu,
                ConnectionGroup => _groupMenu,
                BucketNodeTag => _bucketMenu,
                _ => null
            };
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
            _tree.ContextMenuStrip = args.Node?.Tag switch
            {
                ConnectionProfile => _accountMenu,
                ConnectionGroup => _groupMenu,
                BucketNodeTag => _bucketMenu,
                _ => null
            };
            UpdateCommandStates();
            if (_suppressTreeSelection) return;
            var node = args.Node;
            if (node is null) return;
            if (node.Tag is ConnectionProfile profile)
                ShowConnectionSummary(profile, node);
            else if (node.Tag is BucketNodeTag bucket)
                await NavigateAsync(bucket.Profile, bucket.Bucket, string.Empty, true);
        };
        _tree.AfterExpand += async (_, args) =>
        {
            if (args.Node is not null) await PersistGroupExpansionAsync(args.Node, true);
        };
        _tree.AfterCollapse += async (_, args) =>
        {
            if (args.Node is not null) await PersistGroupExpansionAsync(args.Node, false);
        };

        _objects.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Right) return;
            var hit = _objects.HitTest(args.Location).Item;
            if (hit is not null && !hit.Selected)
            {
                _objects.SelectedItems.Cast<ListViewItem>().ToList().ForEach(item => item.Selected = false);
                hit.Selected = true;
            }
            _objectMenu.Show(_objects, args.Location);
        };
        _objects.ItemActivate += async (_, _) =>
        {
            if (_objects.SelectedItems.Count == 0) return;
            if (_objects.SelectedItems[0].Tag is ParentDirectoryTag)
                await NavigateUpAsync();
            else if (_objects.SelectedItems[0].Tag is LoadMoreTag)
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
            if (args.Control && args.Shift && args.KeyCode == Keys.C) { args.Handled = true; await CopyOrMoveSelectedAsync(false); }
            else if (args.Control && args.Shift && args.KeyCode == Keys.X) { args.Handled = true; await CopyOrMoveSelectedAsync(true); }
            else if (args.Control && args.KeyCode == Keys.C) { args.Handled = true; CopySelectionToObjectClipboard(false); }
            else if (args.Control && args.KeyCode == Keys.X) { args.Handled = true; CopySelectionToObjectClipboard(true); }
            else if (args.Control && args.KeyCode == Keys.V) { args.Handled = true; await PasteObjectClipboardAsync(); }
            else if (args.KeyCode == Keys.Delete) { args.Handled = true; await DeleteSelectedAsync(); }
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
            UpdateTrayStatus();
        };
        _transfers.TransferCompleted += async (_, args) =>
        {
            if (!_closing)
                await ProcessCompletedCdnUploadsAsync([args.Task]);
            if (!_closing &&
                _currentProfile?.Id == args.Task.ProfileId &&
                (string.Equals(_currentBucket, args.Task.Bucket, StringComparison.Ordinal) ||
                 string.Equals(_currentBucket, args.Task.DestinationBucket, StringComparison.Ordinal)))
            {
                await RefreshAsync();
            }
        };
        _transfers.TransferFinished += (_, args) => QueueTrayTransferNotification(args.Task);
        _trayNotificationTimer.Tick += (_, _) => FlushTrayTransferNotifications();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized &&
                TrayResidencePolicy.ShouldHideOnMinimize(
                    _settings.KeepRunningInTray,
                    _automation is not null,
                    _closing))
            {
                BeginInvoke(new Action(HideToTray));
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
        var warnings = new List<string>();
        try
        {
            if (await _configurationTransactions.RecoverPendingAsync())
                warnings.Add("已完成上次中断的连接/CDN 配置事务。");
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to recover configuration transaction", exception);
            warnings.Add($"配置事务恢复：{exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            _settings = await _settingsStore.LoadAsync();
            AddRecoveryWarning(warnings, "应用设置", _settingsStore);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to load application settings", exception);
            _settings = new AppSettings();
            warnings.Add($"应用设置：{exception.GetType().Name}: {exception.Message}；已使用安全默认值。");
        }

        try
        {
            _transferRuntime.Apply(_settings);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to apply transfer settings", exception);
            _settings = new AppSettings();
            _transferRuntime.Apply(_settings);
            warnings.Add("传输参数无效，已恢复安全默认值。");
        }
        _transfers.ConfigureRetryPolicy(_settings.RetryCount, _settings.RetryDelaySeconds);
        ApplySettings();

        try
        {
            var configuration = await _profileStore.LoadConfigurationAsync();
            _profiles = configuration.Profiles;
            _profileGroups = configuration.Groups;
            AddRecoveryWarning(warnings, "对象存储连接", _profileStore as IRecoveryAwareStore);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to load storage profiles", exception);
            _profiles = [];
            _profileGroups = [];
            warnings.Add($"对象存储连接：{exception.GetType().Name}: {exception.Message}；原文件已保留，当前以空列表启动。");
        }
        PopulateProfiles();
        warnings.AddRange(await LoadCdnStateAsync());

        var cdnQueueReady = false;
        try
        {
            await _cdnJobQueue.InitializeAsync();
            cdnQueueReady = true;
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to initialize CDN job queue", exception);
            warnings.Add($"CDN 任务队列：{exception.GetType().Name}: {exception.Message}");
        }

        var transferQueueReady = false;
        try
        {
            await _transfers.InitializeAsync();
            await _transfers.SetConcurrencyAsync(_settings.ConcurrentTransfers);
            transferQueueReady = true;
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to initialize transfer queue", exception);
            warnings.Add($"传输队列：{exception.GetType().Name}: {exception.Message}");
        }

        if (cdnQueueReady && transferQueueReady)
            await ProcessCompletedCdnUploadsAsync(_transferQueue.Snapshot.Tasks);
        _speedTimer.Start();
        UpdateCommandStates();

        if (warnings.Count > 0)
        {
            var summary = SensitiveDataRedactor.Redact(string.Join(Environment.NewLine, warnings.Select(value => "• " + value)));
            _logger.Warning("Startup completed with recovery warnings: " + summary);
            _requestStatus.Text = $"启动完成，{warnings.Count} 项恢复提示";
            if (_automation is null)
            {
                MessageBox.Show(
                    this,
                    "应用已启动，但检测到以下恢复情况：\n\n" + summary + "\n\n详细信息已写入内置日志。",
                    "启动恢复提示",
                    MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            }
        }
        if (_automation is null)
            ShowPendingUpdateResult();
    }

    private static void AddRecoveryWarning(
        ICollection<string> warnings,
        string name,
        IRecoveryAwareStore? store)
    {
        if (store?.LastRecovery is not { } recovery) return;
        warnings.Add(recovery.RestoredFromBackup
            ? $"{name}主文件损坏，已从最近备份恢复；损坏文件已保留。"
            : $"{name}主文件损坏且没有可用备份，已保留损坏文件并使用默认值。");
    }

    internal AutomationReport BuildAutomationReport()
    {
        var hasUpdateCommand = _commands.TryGetValue("check-updates", out var updateCommand) && updateCommand.Enabled;
        var hasProjectHome = _commands.TryGetValue("project-home", out var projectHome) && projectHome.Enabled;
        var hasIssueLink = _commands.TryGetValue("report-issue", out var issueLink) && issueLink.Enabled;
        var hasConnectionImport = _commands.TryGetValue("import-connections", out var importConnections) && importConnections.Enabled;
        var hasConnectionExport = _commands.TryGetValue("export-all-connections", out var exportConnections);
        var hasCdnConfiguration = _commands.TryGetValue("cdn-configure", out var cdnConfiguration);
        var hasCdnUrlCommand = _commands.TryGetValue("cdn-copy-url", out var cdnCopyUrl);
        var checks = new List<AutomationCheck>
        {
            new("window-handle", IsHandleCreated && Handle != IntPtr.Zero, $"Handle={Handle}"),
            new("window-size", ClientSize.Width >= 960 && ClientSize.Height >= 600, $"ClientSize={ClientSize.Width}x{ClientSize.Height}"),
            new("main-menu", _menu.Name == "MainMenu" && _menu.Items.Count > 0, $"Items={_menu.Items.Count}"),
            new("main-toolbar", _toolbar.Name == "MainToolbar" && _toolbar.Items.Count > 0, $"Items={_toolbar.Items.Count}"),
            new("address-strip", _addressStrip.Name == "AddressStrip" && _addressStrip.Items.Count > 0, $"Items={_addressStrip.Items.Count}"),
            new("address-box", _address.Name == "AddressBox" && _address.Width > 0, $"Width={_address.Width}"),
            new("search-box", _search.Name == "SearchBox" && _search.Width > 0, $"Width={_search.Width}"),
            new("account-tree", _tree.Name == "AccountTree" && _tree.Parent is not null, $"Nodes={_tree.Nodes.Count}"),
            new("object-list", _objects.Name == "ObjectList" && _objects.Parent is not null && _objects.Columns.Count == 5, $"Columns={_objects.Columns.Count}"),
            new("transfer-queue", _transfers.Name == "TransferQueue" && _transfers.Parent is not null, $"Visible={_transfers.Visible}"),
            new("status-bar", _status.Name == "StatusBar" && _status.Items.Count > 0, $"Items={_status.Items.Count}"),
            new("update-command", hasUpdateCommand, $"Present={updateCommand is not null}"),
            new("tray-automation-isolated", _automation is null || _trayIcon is null,
                $"Automation={_automation is not null}; TrayCreated={_trayIcon is not null}"),
            new("project-links", hasProjectHome && hasIssueLink,
                $"Home={projectHome is not null}; Issue={issueLink is not null}"),
            new("connection-transfer-commands", hasConnectionImport && hasConnectionExport,
                $"Import={importConnections is not null}; Export={exportConnections is not null}"),
            new("cdn-commands", hasCdnConfiguration && hasCdnUrlCommand,
                $"Configure={cdnConfiguration is not null}; CopyUrl={cdnCopyUrl is not null}")
        };

        var parent = CreateParentDirectoryItem(_objects.Font);
        try
        {
            checks.Add(new AutomationCheck(
                "parent-directory-row",
                parent.Text == ".." &&
                parent.Tag is ParentDirectoryTag &&
                parent.SubItems.Count == 5 &&
                parent.SubItems[2].Text == "上级目录" &&
                parent.Font.Bold,
                $"Text={parent.Text}; Type={parent.SubItems[2].Text}; Bold={parent.Font.Bold}"));
        }
        finally
        {
            parent.Font.Dispose();
        }

        return new AutomationReport(
            checks.All(check => check.Passed),
            DisplayVersion,
            Text,
            ClientSize.Width,
            ClientSize.Height,
            checks);
    }

    internal void CaptureAutomationScreenshot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Refresh();
        Update();
        using var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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
        ApplyTraySettings();
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        var show = new ToolStripMenuItem("打开 S3 Explorer", null, (_, _) => ShowMainWindow())
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        var pause = new ToolStripMenuItem("暂停全部传输", null, async (_, _) =>
            await RunUiCommandAsync("暂停全部传输", () => _transfers.PauseAllAsync()));
        var updates = new ToolStripMenuItem("检查更新...", null, async (_, _) =>
        {
            ShowMainWindow();
            await CheckForUpdatesAsync(automatic: false);
        });
        var exit = new ToolStripMenuItem("退出", null, (_, _) => RequestExit());
        menu.Items.AddRange([show, pause, updates, new ToolStripSeparator(), exit]);

        _trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "S3 Explorer",
            ContextMenuStrip = menu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ApplyTraySettings()
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = _settings.KeepRunningInTray && !_closing;
        if (!_settings.KeepRunningInTray || !_settings.ShowTrayTransferNotifications)
        {
            _trayNotification?.Close();
            _trayNotification?.Dispose();
            _trayNotification = null;
        }
        UpdateTrayStatus();
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon is null) return;
        var active = _transfers.ActiveCount;
        _trayIcon.Text = active > 0
            ? $"S3 Explorer - {active:N0} 个活动传输"
            : "S3 Explorer - 空闲";
    }

    private void HideToTray()
    {
        if (_trayIcon is null || !_settings.KeepRunningInTray || _closing) return;
        _trayIcon.Visible = true;
        Hide();
        UpdateTrayStatus();
    }

    private void ShowMainWindow()
    {
        if (IsDisposed || Disposing) return;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    internal void ActivateFromSecondaryInstance()
    {
        if (_closing) return;
        ShowMainWindow();
    }

    private void RequestExit()
    {
        if (_closing) return;
        _exitRequested = true;
        ShowMainWindow();
        Close();
    }

    private void QueueTrayTransferNotification(TransferTaskRecord task)
    {
        if (task.State == TransferTaskState.Completed)
            _trayCompletedTransfers++;
        else if (task.State == TransferTaskState.Failed)
            _trayFailedTransfers++;
        else
            return;

        _trayNotificationTimer.Stop();
        _trayNotificationTimer.Start();
        UpdateTrayStatus();
    }

    private void FlushTrayTransferNotifications()
    {
        _trayNotificationTimer.Stop();
        if (_transfers.ActiveCount > 0)
        {
            _trayNotificationTimer.Start();
            return;
        }

        var completed = _trayCompletedTransfers;
        var failed = _trayFailedTransfers;
        _trayCompletedTransfers = 0;
        _trayFailedTransfers = 0;
        if (!_settings.KeepRunningInTray ||
            !_settings.ShowTrayTransferNotifications ||
            _trayIcon is not { Visible: true } ||
            completed + failed == 0)
            return;

        ShowTrayTransferNotification(
            failed == 0 ? "传输已完成" : "传输任务已结束",
            $"成功 {completed:N0} 项，失败 {failed:N0} 项。",
            failed > 0);
    }

    private void ShowTrayTransferNotification(string title, string message, bool warning)
    {
        _trayNotification?.Close();
        _trayNotification?.Dispose();
        var notification = new TrayNotificationForm(title, message, warning, ShowMainWindow);
        _trayNotification = notification;
        notification.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_trayNotification, notification))
                _trayNotification = null;
        };
        notification.Show();
    }

    private void PopulateProfiles()
    {
        _populatingProfiles = true;
        try
        {
            var root = _tree.Nodes[0];
            root.Nodes.Clear();
            foreach (var group in _profileGroups.OrderBy(group => group.SortOrder))
            {
                var groupNode = new TreeNode(group.Name)
                {
                    Tag = group,
                    ImageKey = "folder",
                    SelectedImageKey = "folder",
                    ToolTipText = "删除分组只会把连接移到未分组，不会删除连接。"
                };
                foreach (var profile in _profiles
                             .Where(profile => profile.GroupId == group.Id)
                             .OrderBy(profile => profile.SortOrder))
                    groupNode.Nodes.Add(CreateProfileNode(profile));
                root.Nodes.Add(groupNode);
                if (group.IsExpanded) groupNode.Expand();
            }

            foreach (var profile in _profiles
                         .Where(profile => profile.GroupId is null)
                         .OrderBy(profile => profile.SortOrder))
                root.Nodes.Add(CreateProfileNode(profile));
            root.Expand();
        }
        finally
        {
            _populatingProfiles = false;
        }
    }

    private static TreeNode CreateProfileNode(ConnectionProfile profile)
    {
        var node = new TreeNode(profile.Name)
        {
            Tag = profile,
            ImageKey = "account",
            SelectedImageKey = "account"
        };
        ApplyProfileNodePresentation(profile, node);
        node.Nodes.Add(new TreeNode("(双击连接)")
        {
            ForeColor = SystemColors.GrayText,
            ImageKey = "connect",
            SelectedImageKey = "connect"
        });
        return node;
    }

    private static void ApplyProfileNodePresentation(ConnectionProfile profile, TreeNode node)
    {
        var defaultBucket = string.IsNullOrWhiteSpace(profile.DefaultBucket)
            ? string.Empty
            : $"\n默认 Bucket: {profile.DefaultBucket}";
        var lastSuccess = profile.LastConnectionSucceededAtUtc is null
            ? string.Empty
            : $"\n最近成功: {FormatLocalTime(profile.LastConnectionSucceededAtUtc)}";
        var credentials = profile.HasCredentialConfiguration
            ? profile.CredentialSourceDisplayName
            : "待补充";
        node.Text = profile.Name;
        node.ForeColor = !profile.HasCredentialConfiguration
            ? Color.DarkOrange
            : profile.HealthStatus switch
            {
                ConnectionHealthStatus.Healthy => Color.DarkGreen,
                ConnectionHealthStatus.Failed => Color.Firebrick,
                _ => SystemColors.WindowText
            };
        node.ToolTipText =
            $"{profile.Endpoint}\n签名 Region: {profile.EffectiveSignatureRegion}{defaultBucket}" +
            $"\n凭据: {credentials}\n健康状态: {HealthStatusText(profile.HealthStatus)}{lastSuccess}";
    }

    private static string HealthStatusText(ConnectionHealthStatus status) => status switch
    {
        ConnectionHealthStatus.Healthy => "正常",
        ConnectionHealthStatus.Failed => "失败",
        _ => "未检查"
    };

    private static string FormatLocalTime(DateTimeOffset? value) =>
        value is null ? "—" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private async Task NewConnectionAsync()
    {
        var groupId = SelectedTargetGroupId();
        using var dialog = new ConnectionDialog(_storage);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var profile = dialog.Profile with
        {
            GroupId = groupId,
            SortOrder = NextProfileSortOrder(groupId)
        };
        await SaveProfilesAndRefreshAsync(_profiles.Append(profile).ToArray());
        SelectProfileNode(profile);
    }

    private async Task NewConnectionGroupAsync()
    {
        using var dialog = new ConnectionGroupDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_profileGroups.Any(group => string.Equals(group.Name, dialog.GroupName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"连接分组“{dialog.GroupName}”已存在。");
        var group = new ConnectionGroup
        {
            Name = dialog.GroupName,
            SortOrder = _profileGroups.Count,
            IsExpanded = true
        };
        await SaveConfigurationAndRefreshAsync(new ConnectionProfileConfiguration(
            _profiles, _profileGroups.Append(group).ToArray()));
        SelectGroupNode(group.Id);
    }

    private async Task RenameSelectedGroupAsync()
    {
        if (_tree.SelectedNode?.Tag is not ConnectionGroup group) return;
        using var dialog = new ConnectionGroupDialog(group);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_profileGroups.Any(item => item.Id != group.Id &&
            string.Equals(item.Name, dialog.GroupName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"连接分组“{dialog.GroupName}”已存在。");
        var proposed = _profileGroups.Select(item => item.Id == group.Id
            ? item with { Name = dialog.GroupName }
            : item).ToArray();
        await SaveConfigurationAndRefreshAsync(new ConnectionProfileConfiguration(_profiles, proposed));
        SelectGroupNode(group.Id);
    }

    private async Task DeleteSelectedGroupAsync()
    {
        if (_tree.SelectedNode?.Tag is not ConnectionGroup group) return;
        var count = _profiles.Count(profile => profile.GroupId == group.Id);
        if (MessageBox.Show(this,
                $"确定删除分组“{group.Name}”吗？\n\n其中 {count} 个连接会移到未分组；连接本身不会被删除。",
                "删除连接分组", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        var configuration = new ConnectionProfileConfiguration(_profiles, _profileGroups).RemoveGroup(group.Id);
        await SaveConfigurationAndRefreshAsync(configuration);
    }

    private async Task MoveSelectedGroupAsync(int offset)
    {
        if (_tree.SelectedNode?.Tag is not ConnectionGroup group) return;
        var configuration = new ConnectionProfileConfiguration(_profiles, _profileGroups).MoveGroup(group.Id, offset);
        await SaveConfigurationAndRefreshAsync(configuration);
        SelectGroupNode(group.Id);
    }

    private async Task MoveProfileToGroupAsync(ConnectionProfile profile, Guid? groupId)
    {
        if (profile.GroupId == groupId) return;
        var configuration = new ConnectionProfileConfiguration(_profiles, _profileGroups)
            .PlaceProfile(profile.Id, groupId, int.MaxValue);
        await SaveConfigurationAndRefreshAsync(configuration);
        SelectProfileNode(configuration.Profiles.First(item => item.Id == profile.Id));
    }

    private async Task MoveSelectedProfileAsync(int offset)
    {
        var profile = SelectedTreeProfile();
        if (profile is null) return;
        var members = _profiles.Where(item => item.GroupId == profile.GroupId)
            .OrderBy(item => item.SortOrder).ToArray();
        var index = Array.FindIndex(members, item => item.Id == profile.Id);
        if (index < 0) return;
        var configuration = new ConnectionProfileConfiguration(_profiles, _profileGroups)
            .PlaceProfile(profile.Id, profile.GroupId, index + offset);
        await SaveConfigurationAndRefreshAsync(configuration);
        SelectProfileNode(configuration.Profiles.First(item => item.Id == profile.Id));
    }

    private async Task PersistGroupExpansionAsync(TreeNode node, bool expanded)
    {
        if (_populatingProfiles || node.Tag is not ConnectionGroup group || group.IsExpanded == expanded) return;
        var groups = _profileGroups.Select(item => item.Id == group.Id
            ? item with { IsExpanded = expanded }
            : item).ToArray();
        try
        {
            await _profileStore.SaveConfigurationAsync(new ConnectionProfileConfiguration(_profiles, groups));
            _profileGroups = groups;
            node.Tag = group with { IsExpanded = expanded };
        }
        catch (Exception exception)
        {
            _logger.Warning($"Failed to persist connection group expansion: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task EditCurrentConnectionAsync()
    {
        var profile = SelectedTreeProfile();
        if (profile is null) return;
        using var dialog = new ConnectionDialog(_storage, profile);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var proposed = _profiles.Select(item => item.Id == profile.Id ? dialog.Profile : item).ToArray();
        await SaveProfilesAndRefreshAsync(proposed);
        if (_currentProfile?.Id == profile.Id) _currentProfile = dialog.Profile;
    }

    private async Task CopyCurrentConnectionAsync()
    {
        var profile = SelectedTreeProfile();
        if (profile is null) return;
        var copy = profile with
        {
            Id = Guid.NewGuid(),
            Name = CreateUniqueCopyName(profile.Name),
            SortOrder = NextProfileSortOrder(profile.GroupId),
            HealthStatus = ConnectionHealthStatus.Unknown,
            LastConnectionCheckedAtUtc = null,
            LastConnectionSucceededAtUtc = null
        };
        await SaveProfilesAndRefreshAsync(_profiles.Append(copy).ToArray());
        var node = FindProfileNode(copy);
        if (node is not null)
        {
            _tree.SelectedNode = node;
            node.EnsureVisible();
        }
        _logger.Info($"Connection copied source={profile.Name} copy={copy.Name}");
    }

    private string CreateUniqueCopyName(string sourceName)
    {
        var names = _profiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = $"{sourceName} (副本)";
        var suffix = 2;
        while (!names.Add(candidate))
            candidate = $"{sourceName} (副本 {suffix++})";
        return candidate;
    }

    private async Task DeleteCurrentConnectionAsync()
    {
        var profile = SelectedTreeProfile();
        if (profile is null) return;
        var affectedBindings = _cdnConfiguration.Bindings
            .Count(binding => binding.StorageProfileId == profile.Id);
        var associationWarning = affectedBindings == 0
            ? string.Empty
            : $"\n\n同时会删除引用该连接的 {affectedBindings} 个本地 CDN Bucket/前缀关联。";
        if (MessageBox.Show(this,
                $"确定删除连接“{profile.Name}”吗？{associationWarning}\n\n这只删除本地配置，不会删除任何远程 Bucket、对象或 CDN 内容。",
                "删除连接", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        var proposedProfiles = _profiles.Where(item => item.Id != profile.Id).ToArray();
        var proposedCdnConfiguration = new CdnConfiguration(
            _cdnConfiguration.Profiles,
            _cdnConfiguration.Bindings
                .Where(binding => binding.StorageProfileId != profile.Id)
                .ToArray());
        await _configurationTransactions.SaveAsync(
            new ConfigurationSnapshot(_profiles, _cdnConfiguration, _cdnCredentials, _profileGroups),
            new ConfigurationSnapshot(proposedProfiles, proposedCdnConfiguration, _cdnCredentials, _profileGroups));
        _profiles = proposedProfiles;
        _cdnConfiguration = proposedCdnConfiguration;
        if (_currentProfile?.Id == profile.Id) Disconnect();
        PopulateProfiles();
    }

    private async Task SaveProfilesAndRefreshAsync(IReadOnlyList<ConnectionProfile> proposed)
    {
        await SaveConfigurationAndRefreshAsync(new ConnectionProfileConfiguration(proposed, _profileGroups));
    }

    private async Task SaveConfigurationAndRefreshAsync(ConnectionProfileConfiguration proposed)
    {
        var normalized = proposed.Normalize();
        normalized.Validate();
        await _profileStore.SaveConfigurationAsync(normalized);
        _profiles = normalized.Profiles;
        _profileGroups = normalized.Groups;
        PopulateProfiles();
    }

    private async Task<ConnectionProfile> RecordConnectionHealthAsync(
        ConnectionProfile profile,
        bool succeeded)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = profile with
        {
            HealthStatus = succeeded ? ConnectionHealthStatus.Healthy : ConnectionHealthStatus.Failed,
            LastConnectionCheckedAtUtc = now,
            LastConnectionSucceededAtUtc = succeeded ? now : profile.LastConnectionSucceededAtUtc
        };
        var proposed = _profiles
            .Select(item => item.Id == updated.Id ? updated : item)
            .ToArray();
        try
        {
            await _profileStore.SaveConfigurationAsync(new ConnectionProfileConfiguration(proposed, _profileGroups));
            _profiles = proposed;
            if (_currentProfile?.Id == updated.Id)
                _currentProfile = updated;
        }
        catch (Exception exception)
        {
            _logger.Warning($"Failed to persist connection health profile={profile.Name}: {exception.GetType().Name}: {exception.Message}");
        }
        return updated;
    }

    private async Task ExportConnectionsAsync(bool exportAll)
    {
        var profiles = exportAll
            ? _profiles.ToArray()
            : SelectedTreeProfile() is { } selected ? [selected] : [];
        if (profiles.Length == 0)
        {
            MessageBox.Show(this,
                exportAll ? "当前没有可导出的连接。" : "请先选择一个连接。",
                "导出连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var (cdnConfiguration, cdnCredentials) = SelectCdnArchiveData(profiles, exportAll);

        using var options = new ConnectionExportOptionsDialog(
            profiles.Length,
            profiles.Count(profile => profile.HasStoredCredentials ||
                profile.CredentialSource == CredentialSourceKind.AwsAssumeRole &&
                !string.IsNullOrWhiteSpace(profile.AwsExternalId)),
            cdnConfiguration.Profiles.Count,
            cdnCredentials.Count(credential =>
                credential.AuthenticationType != CdnAuthenticationType.None &&
                !string.IsNullOrEmpty(credential.Secret)));
        if (options.ShowDialog(this) != DialogResult.OK) return;

        var suggestedName = exportAll
            ? $"S3Explorer-connections-{DateTime.Now:yyyyMMdd}.{ConnectionArchiveService.FileExtension}"
            : $"{SanitizeFileName(profiles[0].Name)}.{ConnectionArchiveService.FileExtension}";
        using var saveDialog = new SaveFileDialog
        {
            Title = exportAll ? "导出全部连接" : "导出当前连接",
            Filter = $"S3 Explorer 连接包 (*.{ConnectionArchiveService.FileExtension})|*.{ConnectionArchiveService.FileExtension}|JSON 文件 (*.json)|*.json",
            FileName = suggestedName,
            AddExtension = true,
            DefaultExt = ConnectionArchiveService.FileExtension,
            OverwritePrompt = true
        };
        if (saveDialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var archive = _connectionArchive.Export(
                profiles,
                options.IncludeCredentials,
                options.Password,
                cdnConfiguration,
                cdnCredentials);
            await File.WriteAllBytesAsync(saveDialog.FileName, archive);
            _logger.Info($"Connections exported count={profiles.Length} credentials={options.IncludeCredentials} file={saveDialog.FileName}");
            MessageBox.Show(this,
                $"已导出 {profiles.Length} 个对象存储连接、{cdnConfiguration.Profiles.Count} 个 CDN 配置和 " +
                $"{cdnConfiguration.Bindings.Count} 个 CDN 关联。\n\n" +
                (options.IncludeCredentials
                    ? "连接包包含密码加密的 S3/CDN 凭据。请通过其他安全渠道传递迁移密码。"
                    : "连接包不包含任何秘密值；CDN 认证引用已移除。"),
                "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _logger.Error($"Connection export failed file={saveDialog.FileName}", exception);
            ErrorDialog.ShowException(this, "导出失败", "写入连接包", exception, saveDialog.FileName);
        }
    }

    private async Task ImportConnectionsAsync()
    {
        using var openDialog = new OpenFileDialog
        {
            Title = "导入连接",
            Filter = $"S3 Explorer 连接包 (*.{ConnectionArchiveService.FileExtension};*.json)|*.{ConnectionArchiveService.FileExtension};*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (openDialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var fileInfo = new FileInfo(openDialog.FileName);
            if (fileInfo.Length > ConnectionArchiveService.MaximumArchiveBytes)
                throw new InvalidDataException($"连接包不能超过 {ConnectionArchiveService.MaximumArchiveBytes / 1024 / 1024} MiB。");
            var archive = await File.ReadAllBytesAsync(openDialog.FileName);
            var inspection = _connectionArchive.Inspect(archive);
            ConnectionArchivePackage package;
            if (!inspection.RequiresPassword)
            {
                package = _connectionArchive.Import(archive);
            }
            else
            {
                while (true)
                {
                    var password = ConnectionArchivePasswordDialog.RequestPassword(this);
                    if (password is null) return;
                    try
                    {
                        package = _connectionArchive.Import(archive, password);
                        break;
                    }
                    catch (ConnectionArchiveAuthenticationException exception)
                    {
                        _logger.Warning($"Connection archive unlock failed file={openDialog.FileName}: {exception.Message}");
                        if (MessageBox.Show(this,
                                "迁移密码错误，或连接包已损坏。是否重新输入？",
                                "无法解锁连接包", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                            return;
                    }
                }
            }

            using var preview = new ConnectionImportPreviewDialog(
                package,
                _profiles,
                _cdnConfiguration,
                _cdnCredentials,
                _connectionArchive,
                _profileGroups);
            if (preview.ShowDialog(this) != DialogResult.OK) return;
            var selectedStorage = preview.SelectedProfiles;
            var selectedCdn = preview.SelectedCdnProfiles;
            var previousProfiles = _profiles;
            var previousCdnConfiguration = _cdnConfiguration;
            var previousCdnCredentials = _cdnCredentials;
            var merged = _connectionArchive.MergePackage(
                _profiles,
                _cdnConfiguration,
                _cdnCredentials,
                package,
                new ConnectionArchiveImportSelection(
                    selectedStorage.Select(profile => profile.Id).ToArray(),
                    selectedCdn.Select(profile => profile.Id).ToArray()),
                preview.ImportStorageCredentials,
                preview.ImportCdnCredentials,
                preview.ConflictStrategy,
                preview.TargetGroupId);

            await _configurationTransactions.SaveAsync(
                new ConfigurationSnapshot(
                    previousProfiles,
                    previousCdnConfiguration,
                    previousCdnCredentials,
                    _profileGroups),
                new ConfigurationSnapshot(
                    merged.Profiles,
                    merged.CdnConfiguration,
                    merged.CdnCredentials,
                    _profileGroups));

            _profiles = merged.Profiles;
            _cdnConfiguration = merged.CdnConfiguration;
            _cdnCredentials = merged.CdnCredentials;
            PopulateProfiles();

            if (_currentProfile is not null)
            {
                _currentProfile = _profiles.FirstOrDefault(profile => profile.Id == _currentProfile.Id);
                if (_currentProfile is null) Disconnect();
            }

            var changedCount = CountChangedProfiles(previousProfiles, _profiles);
            var changedCdnProfiles = CountChangedRecords(
                previousCdnConfiguration.Profiles,
                _cdnConfiguration.Profiles,
                profile => profile.Id);
            var changedCdnBindings = CountChangedRecords(
                previousCdnConfiguration.Bindings,
                _cdnConfiguration.Bindings,
                binding => binding.Id);
            var changedCdnCredentials = CountChangedRecords(
                previousCdnCredentials,
                _cdnCredentials,
                credential => credential.Id);
            _logger.Info(
                $"Connections imported selectedStorage={selectedStorage.Count} changedStorage={changedCount} " +
                $"selectedCdn={selectedCdn.Count} " +
                $"cdnProfiles={changedCdnProfiles} cdnBindings={changedCdnBindings} " +
                $"cdnCredentials={changedCdnCredentials} " +
                $"storageCredentials={preview.ImportStorageCredentials} " +
                $"importCdnCredentials={preview.ImportCdnCredentials} " +
                $"strategy={preview.ConflictStrategy} file={openDialog.FileName}");
            MessageBox.Show(this,
                $"导入处理完成：选择 {selectedStorage.Count} 个对象存储连接，实际新增或更新 {changedCount} 个。\n" +
                $"选择 {selectedCdn.Count} 个 CDN 配置；配置 {changedCdnProfiles} 个、关联 {changedCdnBindings} 个、" +
                $"凭据 {changedCdnCredentials} 个发生变化。\n\n" +
                $"对象存储凭据：{(preview.ImportStorageCredentials ? "已导入" : "未导入")}；" +
                $"CDN 凭据：{(preview.ImportCdnCredentials ? "已导入" : "未导入")}。",
                "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _logger.Error($"Connection import failed file={openDialog.FileName}", exception);
            ErrorDialog.ShowException(this, "导入失败", "读取连接包", exception, openDialog.FileName);
        }
    }

    private static int CountChangedProfiles(
        IReadOnlyCollection<ConnectionProfile> before,
        IReadOnlyCollection<ConnectionProfile> after)
    {
        var previous = before.ToDictionary(profile => profile.Id);
        return after.Count(profile => !previous.TryGetValue(profile.Id, out var oldProfile) || oldProfile != profile);
    }

    private (CdnConfiguration Configuration, IReadOnlyList<CdnCredential> Credentials) SelectCdnArchiveData(
        IReadOnlyCollection<ConnectionProfile> profiles,
        bool exportAll)
    {
        if (exportAll)
            return (_cdnConfiguration, _cdnCredentials);

        var storageIds = profiles.Select(profile => profile.Id).ToHashSet();
        var bindings = _cdnConfiguration.Bindings
            .Where(binding => storageIds.Contains(binding.StorageProfileId))
            .ToArray();
        var cdnProfileIds = bindings.Select(binding => binding.CdnProfileId).ToHashSet();
        var cdnProfiles = _cdnConfiguration.Profiles
            .Where(profile => cdnProfileIds.Contains(profile.Id))
            .ToArray();
        var credentialIds = cdnProfiles
            .Where(profile => profile.CredentialId.HasValue)
            .Select(profile => profile.CredentialId!.Value)
            .ToHashSet();
        var credentials = _cdnCredentials
            .Where(credential => credentialIds.Contains(credential.Id))
            .ToArray();
        return (new CdnConfiguration(cdnProfiles, bindings), credentials);
    }

    private static int CountChangedRecords<T>(
        IReadOnlyCollection<T> before,
        IReadOnlyCollection<T> after,
        Func<T, Guid> idSelector)
        where T : notnull
    {
        var previous = before.ToDictionary(idSelector);
        var current = after.ToDictionary(idSelector);
        return previous.Keys
            .Union(current.Keys)
            .Count(id =>
                !previous.TryGetValue(id, out var oldItem) ||
                !current.TryGetValue(id, out var newItem) ||
                !EqualityComparer<T>.Default.Equals(oldItem, newItem));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "connection" : sanitized;
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

            profile = await RecordConnectionHealthAsync(profile, succeeded: true);
            profileNode.Tag = profile;
            ApplyProfileNodePresentation(profile, profileNode);
            _currentProfile = profile;
            _currentBucket = null;
            _currentPrefix = string.Empty;
            Text = WindowTitle(profile.Name);
            _connectionStatus.Text = $"已连接：{profile.Name}";
            profileNode.Nodes.Clear();
            foreach (var bucket in buckets)
            {
                var imageKey = bucket.IsConfigured ? "bucket-configured" : "bucket";
                var tooltip = bucket.IsConfigured
                    ? $"{bucket.Name}\n来自账户配置（默认/外部 Bucket）；服务未返回 ListBuckets 结果。"
                    : bucket.Name;
                if (!string.IsNullOrWhiteSpace(bucket.Region))
                    tooltip += $"\nRegion: {bucket.Region}";
                profileNode.Nodes.Add(new TreeNode(bucket.Name)
                {
                    Tag = new BucketNodeTag(profile, bucket.Name),
                    ImageKey = imageKey,
                    SelectedImageKey = imageKey,
                    ToolTipText = tooltip
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
            profile = await RecordConnectionHealthAsync(profile, succeeded: false);
            profileNode.Tag = profile;
            ApplyProfileNodePresentation(profile, profileNode);
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
            AddSummaryItem("健康状态", HealthStatusText(profile.HealthStatus));
            AddSummaryItem("最近检查", FormatLocalTime(profile.LastConnectionCheckedAtUtc));
            AddSummaryItem("最近成功", FormatLocalTime(profile.LastConnectionSucceededAtUtc));
            AddSummaryItem("凭据来源", profile.CredentialSourceDisplayName);
            AddSummaryItem("临时凭据", profile.UsesTemporarySessionCredentials
                ? "已保存 Session Token"
                : profile.UsesExternalAwsCredentials ? "由 AWS SDK 在运行时获取" : "未启用");
            AddSummaryItem("凭据存储", profile.UsesExternalAwsCredentials
                ? "不在 S3 Explorer 中保存凭据值"
                : "SecretKey 与 SessionToken 使用 DPAPI CurrentUser 加密");
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

    internal static ListViewItem CreateParentDirectoryItem(Font baseFont)
    {
        var parent = new ListViewItem("..")
        {
            Tag = new ParentDirectoryTag(),
            ImageKey = UiIcons.ObjectImageKey("..", true),
            Font = new Font(baseFont, FontStyle.Bold)
        };
        parent.SubItems.AddRange(["", "上级目录", "", ""]);
        return parent;
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
            if (!string.IsNullOrEmpty(_currentPrefix))
                _objects.Items.Add(CreateParentDirectoryItem(_objects.Font));

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
        EnumerateTreeNodes(_tree.Nodes[0].Nodes)
            .FirstOrDefault(item => item.Tag is ConnectionProfile candidate && candidate.Id == profile.Id);

    private static IEnumerable<TreeNode> EnumerateTreeNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateTreeNodes(node.Nodes))
                yield return child;
        }
    }

    private Guid? SelectedTargetGroupId() => _tree.SelectedNode?.Tag switch
    {
        ConnectionGroup group => group.Id,
        ConnectionProfile profile => profile.GroupId,
        BucketNodeTag bucket => bucket.Profile.GroupId,
        _ => null
    };

    private int NextProfileSortOrder(Guid? groupId) =>
        _profiles.Where(profile => profile.GroupId == groupId)
            .Select(profile => profile.SortOrder)
            .DefaultIfEmpty(-1)
            .Max() + 1;

    private void SelectProfileNode(ConnectionProfile profile)
    {
        var node = FindProfileNode(profile);
        if (node is null) return;
        node.Parent?.Expand();
        _tree.SelectedNode = node;
        node.EnsureVisible();
    }

    private void SelectGroupNode(Guid groupId)
    {
        var node = _tree.Nodes[0].Nodes.Cast<TreeNode>()
            .FirstOrDefault(item => item.Tag is ConnectionGroup group && group.Id == groupId);
        if (node is null) return;
        _tree.SelectedNode = node;
        node.EnsureVisible();
    }

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
        if (_navigationHistory.Record(location))
            UpdateCommandStates();
    }

    private async Task NavigateHistoryAsync(int delta)
    {
        if (!_navigationHistory.TryMove(delta, out var location)) return;
        UpdateCommandStates();
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

    private async Task ShowBucketManagementAsync(BucketManagementPage page)
    {
        if (!TryGetBucketContext(out var profile, out var bucket)) return;
        using var dialog = new BucketManagementDialog(_storage, profile, bucket, page);
        dialog.ShowDialog(this);
        if (dialog.BucketEmptied && _currentProfile?.Id == profile.Id &&
            string.Equals(_currentBucket, bucket, StringComparison.Ordinal))
            await RefreshAsync();
    }

    private async Task ShowObjectVersionsAsync(bool selectedOnly)
    {
        if (!EnsureLocation()) return;
        var support = S3ProviderCapabilityRegistry.For(_currentProfile!.ServiceType).Object.VersionOperations;
        if (!support.Supported)
        {
            MessageBox.Show(this, support.Reason, "对象版本", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var prefix = _currentPrefix;
        if (selectedOnly)
        {
            var selected = SelectedEntries();
            if (selected.Count != 1 || selected[0].IsDirectory)
            {
                MessageBox.Show(this,
                    "请选择一个对象；也可通过“查看 → 显示版本”浏览当前路径。",
                    "对象版本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            prefix = selected[0].Key;
        }
        using var dialog = new ObjectVersionsDialog(
            _storage, _currentProfile!, _currentBucket!, prefix, _transfers);
        dialog.ShowDialog(this);
        if (dialog.RemoteChanged)
            await RefreshAsync();
    }

    private bool TryGetBucketContext(out ConnectionProfile profile, out string bucket)
    {
        if (_tree.SelectedNode?.Tag is BucketNodeTag tag)
        {
            profile = tag.Profile;
            bucket = tag.Bucket;
            return true;
        }
        if (_currentProfile is not null && _currentBucket is not null)
        {
            profile = _currentProfile;
            bucket = _currentBucket;
            return true;
        }
        profile = null!;
        bucket = string.Empty;
        MessageBox.Show(this, "请先连接账户并选择 Bucket。", "Bucket 管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private async Task<bool> OpenSelectedBucketAsync()
    {
        if (_tree.SelectedNode?.Tag is not BucketNodeTag selected)
        {
            MessageBox.Show(this, "请先选择 Bucket。", "Bucket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var alreadyAtRoot = _currentProfile?.Id == selected.Profile.Id &&
            string.Equals(_currentBucket, selected.Bucket, StringComparison.Ordinal) &&
            _currentPrefix.Length == 0;
        if (!alreadyAtRoot)
            await NavigateAsync(selected.Profile, selected.Bucket, string.Empty, true);

        return _currentProfile?.Id == selected.Profile.Id &&
            string.Equals(_currentBucket, selected.Bucket, StringComparison.Ordinal) &&
            _currentPrefix.Length == 0;
    }

    private async Task InSelectedBucketAsync(Func<Task> action)
    {
        if (await OpenSelectedBucketAsync())
            await action();
    }

    private async Task TestCurrentConnectionAsync()
    {
        var profile = _currentProfile ?? SelectedTreeProfile();
        if (profile is null) return;
        SetBusy("正在测试连接...");
        try
        {
            var result = await _storage.TestConnectionAsync(profile, CancellationToken.None);
            profile = await RecordConnectionHealthAsync(profile, result.Success);
            var profileNode = FindProfileNode(profile);
            if (profileNode is not null)
            {
                profileNode.Tag = profile;
                ApplyProfileNodePresentation(profile, profileNode);
            }
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
        var pageSize = Math.Clamp(
            _settings.ObjectPageSize,
            ObjectListingLimits.MinimumPageSize,
            ObjectListingLimits.MaximumPageSize);
        await foreach (var item in RecursiveObjectListing.EnumerateFilesAsync(
                           prefix,
                           pageSize,
                           limit,
                           (currentPrefix, token, cancellationToken) => _storage.ListObjectsAsync(
                               profile, bucket, currentPrefix, token, pageSize, cancellationToken),
                           CancellationToken.None))
        {
            yield return item;
        }
    }

    private async Task<IReadOnlyList<S3ObjectEntry>> ListAllObjectsAsync(string prefix)
    {
        var profile = _currentProfile ?? throw new InvalidOperationException("当前连接已断开。");
        var bucket = _currentBucket ?? throw new InvalidOperationException("当前 Bucket 已关闭。");
        var limit = Math.Clamp(
            _settings.ObjectCacheLimit,
            ObjectListingLimits.MinimumCacheLimit,
            ObjectListingLimits.MaximumCacheLimit);
        var pageSize = Math.Clamp(
            _settings.ObjectPageSize,
            ObjectListingLimits.MinimumPageSize,
            ObjectListingLimits.MaximumPageSize);
        return await RecursiveObjectListing.ListFilesAsync(
            prefix,
            pageSize,
            limit,
            (currentPrefix, token, cancellationToken) => _storage.ListObjectsAsync(
                profile, bucket, currentPrefix, token, pageSize, cancellationToken),
            CancellationToken.None);
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
        ApplyTraySettings();
        if (_currentProfile is not null && _currentBucket is not null)
            await LoadObjectsPageAsync(true);
    }

    private void OpenLog()
    {
        try
        {
            using var dialog = new LogViewerDialog(_logger);
            dialog.ShowDialog(this);
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "无法显示日志", "查看日志", exception, _logger.CurrentLogPath);
        }
    }

    private async Task CheckForUpdatesAsync(bool automatic)
    {
        if (_updateCheckInProgress)
        {
            if (!automatic)
                MessageBox.Show(this, "更新检查正在进行，请稍候。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _updateCheckInProgress = true;
        var previousStatus = _requestStatus.Text;
        _requestStatus.Text = "正在检查更新...";
        try
        {
            var currentVersion = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            var release = await _updateChecker.GetLatestAsync(_updateCancellation.Token);
            if (!release.IsNewerThan(currentVersion))
            {
                if (!automatic && !IsDisposed)
                {
                    var message = release.IsFromCache
                        ? $"在线更新通道暂时不可用。最近成功检查缓存的版本是 {release.TagName}，无法据此确认当前版本 {DisplayVersion} 是否为最新版本。"
                        : $"当前版本 {DisplayVersion} 已是最新稳定版本。";
                    MessageBox.Show(this, message, "检查更新", MessageBoxButtons.OK,
                        release.IsFromCache ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
                return;
            }

            if (IsDisposed || Disposing) return;
            _logger.Info($"Update check source={release.Source}; latest={release.TagName}; cachedAt={release.CachedAtUtc:O}");
            var canApplyInstaller = UpdateInstallerLauncher.CanApply(release);
            using var dialog = new UpdateDialog(currentVersion, release, canApplyInstaller);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (dialog.InstallRequested && canApplyInstaller)
                    await DownloadAndInstallUpdateAsync(release);
                else if (dialog.SelectedUri is not null)
                    OpenExternalUrl(dialog.SelectedUri.AbsoluteUri);
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.Warning("GitHub update check found no published release.");
            if (!automatic && !IsDisposed)
                MessageBox.Show(this,
                    "GitHub 上还没有已发布的稳定 Release，暂时无法比较版本。",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException exception)
        {
            _logger.Warning($"Update check timed out: {exception.Message}");
            if (!automatic && !_closing && !IsDisposed)
                MessageBox.Show(this,
                    "检查更新超时，请确认网络连接后重试。",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            _logger.Warning($"Update check failed: {exception.GetType().Name}: {exception.Message}");
            if (!automatic && !IsDisposed)
                MessageBox.Show(this,
                    $"无法检查更新：{exception.Message}",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (!IsDisposed && !Disposing)
                _requestStatus.Text = previousStatus;
        }
    }

    private async Task DownloadAndInstallUpdateAsync(GitHubReleaseInfo release)
    {
        using var service = new UpdateDownloadService();
        using var downloadDialog = new UpdateDownloadDialog(service, release);
        var result = downloadDialog.ShowDialog(this);
        if (result == DialogResult.Cancel)
            return;
        if (result != DialogResult.OK || downloadDialog.Package is null)
        {
            var failure = downloadDialog.Failure ?? new InvalidOperationException("更新下载未完成。");
            _logger.Error("Verified update download failed", failure);
            ErrorDialog.ShowException(this, "更新下载失败", "下载并校验安装包", failure);
            return;
        }

        var package = downloadDialog.Package;
        var answer = MessageBox.Show(
            this,
            $"安装包已通过 SHA-256 校验。\n\n" +
            $"版本：{package.Version.ToString(3)}\n" +
            $"大小：{FileSizeFormatter.Format(package.Bytes)}\n\n" +
            "继续后会先安全暂停活动传输并退出 S3 Explorer，然后静默运行 MSI。" +
            "Windows 仍会显示管理员权限确认，安装完成后程序会自动重新打开。",
            "确认安装更新",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        if (!await PrepareTransfersForUpdateAsync())
            return;

        try
        {
            using var updater = UpdateInstallerLauncher.Launch(package);
            _logger.Info($"Verified MSI updater launched. target={package.Version.ToString(3)}; pid={updater.Id}");
            _exitRequested = true;
            Close();
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to launch MSI updater", exception);
            ErrorDialog.ShowException(this, "无法启动更新安装", "启动维护程序", exception);
        }
    }

    private async Task<bool> PrepareTransfersForUpdateAsync()
    {
        if (_transfers.ActiveCount <= 0)
            return true;

        using var dialog = new TransferCloseDialog(_transfers.ActiveCount);
        dialog.Text = "安装更新前处理传输任务";
        dialog.ShowDialog(this);
        var action = dialog.SelectedAction;
        if (action == TransferCloseAction.Return)
            return false;

        _requestStatus.Text = action switch
        {
            TransferCloseAction.Wait => "等待传输完成后安装更新...",
            TransferCloseAction.Cancel => "正在取消传输以安装更新...",
            _ => "正在暂停传输以安装更新..."
        };
        switch (action)
        {
            case TransferCloseAction.Wait:
                await AwaitShutdownStepAsync(_transfers.WaitForIdleAsync(), "等待传输完成");
                break;
            case TransferCloseAction.Cancel:
                await AwaitShutdownStepAsync(_transfers.CancelAllAsync(), "取消传输");
                await AwaitShutdownStepAsync(_transfers.WaitForIdleAsync(), "等待取消完成");
                break;
            case TransferCloseAction.Pause:
                await AwaitShutdownStepAsync(_transfers.PauseAllAsync(), "暂停传输");
                await AwaitShutdownStepAsync(_transfers.WaitForIdleAsync(), "等待暂停完成");
                break;
        }
        return true;
    }

    private void ShowPendingUpdateResult()
    {
        var result = UpdateInstallerLauncher.TryConsumeResult();
        if (result is null) return;
        var currentVersion = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        var installedTarget = GitHubUpdateChecker.NormalizeVersion(currentVersion).CompareTo(
            GitHubUpdateChecker.NormalizeVersion(result.TargetVersion)) >= 0;
        if (result.Succeeded && installedTarget)
        {
            MessageBox.Show(
                this,
                $"S3 Explorer 已成功更新到 {result.TargetVersion.ToString(3)}。" +
                (result.InstallerExitCode == 3010 ? "\nWindows 建议稍后重新启动系统。" : string.Empty),
                "更新完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var message = result.Succeeded
            ? $"安装器已完成，但当前程序版本仍是 {DisplayVersion}。"
            : SensitiveDataRedactor.Redact(result.Message);
        MessageBox.Show(
            this,
            $"S3 Explorer 更新未完成：{message}\n\n安装日志：{result.LogPath}",
            "更新需要处理",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void OpenExternalUrl(string value)
    {
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("只允许打开可信的 HTTPS 项目链接。");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _logger.Warning($"Failed to open project URL: {exception.GetType().Name}: {exception.Message}");
            ErrorDialog.ShowException(this, "无法打开链接", "项目链接", exception, value);
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
            "F5  刷新\nAlt+Left / Alt+Right  返回 / 前进\nAlt+Up  上一级\nCtrl+L  地址栏\nCtrl+F  搜索\nCtrl+U  上传文件\nCtrl+Shift+U  上传文件夹\nCtrl+D  下载\nCtrl+C / Ctrl+X / Ctrl+V  复制 / 剪切 / 粘贴\nCtrl+Shift+C / Ctrl+Shift+X  复制到 / 移动到\nF2  重命名\nDelete  删除\nAlt+Enter  属性\nCtrl+A  全选\nEscape  清除选择",
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

    private void ShowFolderSync()
    {
        using var dialog = new FolderSyncDialog(
            _syncJobStore,
            _profileStore,
            _storage,
            _transferQueue,
            _settings,
            _currentProfile,
            _currentBucket,
            _currentPrefix);
        dialog.ShowDialog(this);
        if (dialog.QueuedTransfers) SetTransferVisibility(true);
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
        var item = new ToolStripMenuItem(text, UiIcons.ForCommand(id), handler) { ShortcutKeys = shortcut };
        _commands[id] = item;
        return item;
    }

    private async Task RunUiCommandAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            _requestStatus.Text = $"{operation}已取消";
        }
        catch (Exception exception)
        {
            _logger.Error($"UI command failed operation={operation}", exception);
            ErrorDialog.ShowException(this, $"{operation}失败", operation, exception);
        }
    }

    private static ToolStripMenuItem ContextCommand(
        string id,
        string text,
        UiIconKind fallbackIcon,
        EventHandler handler) =>
        new(text, UiIcons.ForCommand(id) ?? UiIcons.Create(fallbackIcon, 16), handler);

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
        var objectCapabilities = _currentProfile is null
            ? null
            : S3ProviderCapabilityRegistry.For(_currentProfile.ServiceType).Object;
        var versionOperations = inBucket && objectCapabilities?.VersionOperations.Supported == true;
        var presignedUrl = oneFile && objectCapabilities?.PresignedUrl.Supported == true;

        SetEnabled("edit-connection", profileSelected);
        SetEnabled("copy-connection", profileSelected);
        SetEnabled("delete-connection", profileSelected);
        SetEnabled("export-connection", profileSelected);
        SetEnabled("export-all-connections", _profiles.Count > 0);
        SetEnabled("connect", profileSelected);
        SetEnabled("disconnect", connected);
        SetEnabled("create-bucket", connected);
        SetEnabled("create-bucket-toolbar", connected);
        SetEnabled("delete-bucket", bucketSelected);
        var bucketContext = bucketSelected || inBucket;
        SetEnabled("bucket-properties", bucketContext);
        SetEnabled("bucket-acl", bucketContext);
        SetEnabled("bucket-policy", bucketContext);
        SetEnabled("bucket-access-controls", bucketContext);
        SetEnabled("empty-bucket", bucketContext);
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
        SetEnabled("clipboard-copy", any);
        SetEnabled("clipboard-cut", any);
        SetEnabled("clipboard-paste", inBucket && EnsureClipboardProfile(false));
        SetEnabled("copy-object", any);
        SetEnabled("copy-toolbar", any);
        SetEnabled("move-object", any);
        SetEnabled("move-toolbar", any);
        SetEnabled("rename", oneFile);
        SetEnabled("rename-object", oneFile);
        SetEnabled("properties", oneFile);
        SetEnabled("properties-menu", oneFile);
        SetEnabled("properties-toolbar", oneFile);
        SetEnabled("metadata", oneFile);
        SetEnabled("batch-metadata", selected.Any(entry => !entry.IsDirectory));
        SetEnabled("show-versions", versionOperations);
        SetEnabled("object-versions", oneFile && versionOperations);
        SetEnabled("presign", presignedUrl);
        SetEnabled("copy-path", any);
        SetEnabled("copy-url", any);
        SetEnabled("copy-key", any);
        SetEnabled("back", _navigationHistory.CanGoBack);
        SetEnabled("forward", _navigationHistory.CanGoForward);
        SetEnabled("up", inBucket && _currentPrefix.Length > 0);
        UpdateCdnCommandStates(oneFile);
    }

    private void SetEnabled(string id, bool enabled)
    {
        if (_commands.TryGetValue(id, out var item)) item.Enabled = enabled;
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (TrayResidencePolicy.ShouldHideOnClose(
                _settings.KeepRunningInTray,
                _automation is not null,
                _exitRequested,
                e.CloseReason))
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (_closing) return;
        e.Cancel = true;

        var closeAction = TransferCloseAction.Pause;
        if (_transfers.ActiveCount > 0)
        {
            using var dialog = new TransferCloseDialog(_transfers.ActiveCount);
            dialog.ShowDialog(this);
            closeAction = dialog.SelectedAction;
            if (closeAction == TransferCloseAction.Return)
            {
                _exitRequested = false;
                return;
            }
        }

        try
        {
            CancelNavigation();
            _updateCancellation.Cancel();
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
                    await AwaitShutdownStepAsync(_transfers.WaitForIdleAsync(), "等待传输完成");
                    break;
                case TransferCloseAction.Cancel:
                    await AwaitShutdownStepAsync(_transfers.CancelAllAsync(), "取消传输");
                    await AwaitShutdownStepAsync(_transfers.WaitForIdleAsync(), "等待取消完成");
                    break;
                case TransferCloseAction.Pause:
                    await AwaitShutdownStepAsync(_transfers.PauseAllAsync(), "暂停传输");
                    await AwaitShutdownStepAsync(_transfers.WaitForIdleAsync(), "等待暂停完成");
                    break;
            }

            await AwaitShutdownStepAsync(SaveSettingsAsync(), "保存应用设置");
            await AwaitShutdownStepAsync(_cdnJobQueue.DisposeAsync().AsTask(), "保存 CDN 任务队列");
            await AwaitShutdownStepAsync(_transferQueue.DisposeAsync().AsTask(), "保存传输队列");
            _closing = true;
            if (_trayIcon is not null)
                _trayIcon.Visible = false;
            BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to close transfer queue safely", exception);
            _exitRequested = false;
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
        var proposed = _settings with
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
        await _settingsStore.SaveAsync(proposed);
        _settings = proposed;
    }

    internal static async Task AwaitShutdownStepAsync(
        Task operation,
        string operationName,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? ShutdownStepTimeout;
        try
        {
            await operation.WaitAsync(effectiveTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"{operationName}超过 {effectiveTimeout.TotalSeconds:N0} 秒仍未完成，退出已取消；任务和配置不会被强制丢弃。",
                exception);
        }
    }
}
