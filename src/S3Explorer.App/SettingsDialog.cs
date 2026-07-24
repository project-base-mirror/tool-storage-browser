using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class SettingsDialog : Form
{
    private readonly CheckBox _remember = new() { Text = "记住窗口布局", AutoSize = true };
    private readonly CheckBox _delete = new() { Text = "删除前确认", AutoSize = true };
    private readonly CheckBox _overwrite = new() { Text = "覆盖前确认", AutoSize = true };
    private readonly CheckBox _autoConnect = new() { Text = "启动时自动连接最后账户", AutoSize = true };
    private readonly TextBox _download = new();
    private readonly NumericUpDown _pageSize = new()
    {
        Minimum = ObjectListingLimits.MinimumPageSize,
        Maximum = ObjectListingLimits.MaximumPageSize,
        Increment = 100,
        ThousandsSeparator = true
    };
    private readonly NumericUpDown _cacheLimit = new()
    {
        Minimum = ObjectListingLimits.MinimumCacheLimit,
        Maximum = ObjectListingLimits.MaximumCacheLimit,
        Increment = 10_000,
        ThousandsSeparator = true
    };
    private readonly NumericUpDown _files = new() { Minimum = 1, Maximum = 32 };
    private readonly NumericUpDown _parts = new() { Minimum = 1, Maximum = 32 };
    private readonly NumericUpDown _threshold = new() { Minimum = 5, Maximum = 10240 };
    private readonly NumericUpDown _partSize = new() { Minimum = 5, Maximum = 512 };
    private readonly NumericUpDown _retries = new() { Minimum = 0, Maximum = 20 };
    private readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 300 };
    private readonly NumericUpDown _uploadLimit = new() { Minimum = 0, Maximum = 1_048_576, ThousandsSeparator = true, Increment = 1024 };
    private readonly NumericUpDown _downloadLimit = new() { Minimum = 0, Maximum = 1_048_576, ThousandsSeparator = true, Increment = 1024 };

    public AppSettings Settings { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        Settings = settings;
        Text = "选项";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(690, 500);
        MinimumSize = new Size(640, 450);
        ShowInTaskbar = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneral());
        tabs.TabPages.Add(BuildListing());
        tabs.TabPages.Add(BuildTransfer());
        tabs.TabPages.Add(BuildSecurity());
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 85 };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 85 };
        ok.Click += (_, _) => Settings = settings with
        {
            RememberLayout = _remember.Checked,
            ConfirmDelete = _delete.Checked,
            ConfirmOverwrite = _overwrite.Checked,
            AutoConnectLastProfile = _autoConnect.Checked,
            DefaultDownloadDirectory = _download.Text.Trim(),
            ObjectPageSize = (int)_pageSize.Value,
            ObjectCacheLimit = (int)_cacheLimit.Value,
            ConcurrentTransfers = (int)_files.Value,
            MultipartConcurrency = (int)_parts.Value,
            MultipartThresholdMb = (int)_threshold.Value,
            PartSizeMb = (int)_partSize.Value,
            RetryCount = (int)_retries.Value,
            RetryDelaySeconds = (int)_delay.Value,
            UploadLimitKibPerSecond = (int)_uploadLimit.Value,
            DownloadLimitKibPerSecond = (int)_downloadLimit.Value
        };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        _remember.Checked = settings.RememberLayout;
        _delete.Checked = settings.ConfirmDelete;
        _overwrite.Checked = settings.ConfirmOverwrite;
        _autoConnect.Checked = settings.AutoConnectLastProfile;
        _download.Text = settings.DefaultDownloadDirectory;
        _pageSize.Value = Math.Clamp(
            settings.ObjectPageSize,
            ObjectListingLimits.MinimumPageSize,
            ObjectListingLimits.MaximumPageSize);
        _cacheLimit.Value = Math.Clamp(
            settings.ObjectCacheLimit,
            ObjectListingLimits.MinimumCacheLimit,
            ObjectListingLimits.MaximumCacheLimit);
        _files.Value = settings.ConcurrentTransfers;
        _parts.Value = settings.MultipartConcurrency;
        _threshold.Value = settings.MultipartThresholdMb;
        _partSize.Value = settings.PartSizeMb;
        _retries.Value = settings.RetryCount;
        _delay.Value = settings.RetryDelaySeconds;
        _uploadLimit.Value = Math.Clamp(settings.UploadLimitKibPerSecond, 0, 1_048_576);
        _downloadLimit.Value = Math.Clamp(settings.DownloadLimitKibPerSecond, 0, 1_048_576);
    }

    private TabPage BuildGeneral()
    {
        var page = new TabPage("常规");
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14) };
        panel.Controls.AddRange([_remember, _delete, _overwrite, _autoConnect]);
        var downloadPanel = new FlowLayoutPanel { AutoSize = true };
        downloadPanel.Controls.Add(new Label { Text = "默认下载目录：", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        _download.Width = 390;
        downloadPanel.Controls.Add(_download);
        var browse = new Button { Text = "浏览..." };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = _download.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _download.Text = dialog.SelectedPath;
        };
        downloadPanel.Controls.Add(browse);
        panel.Controls.Add(downloadPanel);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildListing()
    {
        var page = new TabPage("对象列表");
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14), ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        var row = 0;
        AddNumber(table, ref row, "每页请求对象数：", _pageSize);
        AddNumber(table, ref row, "内存缓存对象上限：", _cacheLimit);
        table.Controls.Add(new Label
        {
            Text = "达到缓存上限后停止加载，并在状态栏显示提示。最大可配置为 1,000,000。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(3, 14, 3, 3)
        }, 0, row);
        table.SetColumnSpan(table.GetControlFromPosition(0, row)!, 2);
        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildTransfer()
    {
        var page = new TabPage("传输");
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14), ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        var row = 0;
        AddNumber(table, ref row, "同时传输文件数：", _files);
        AddNumber(table, ref row, "单文件分片并发数：", _parts);
        AddNumber(table, ref row, "分片上传阈值（MB）：", _threshold);
        AddNumber(table, ref row, "分片大小（MB）：", _partSize);
        AddNumber(table, ref row, "失败重试次数：", _retries);
        AddNumber(table, ref row, "重试基础间隔（秒）：", _delay);
        AddNumber(table, ref row, "上传限速（KiB/s）：", _uploadLimit);
        AddNumber(table, ref row, "下载限速（KiB/s）：", _downloadLimit);
        var note = new Label
        {
            Text = "限速设为 0 表示不限速；重试采用指数退避。分片大小最小为 5 MiB。",
            AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 12, 3, 3)
        };
        table.Controls.Add(note, 0, row);
        table.SetColumnSpan(note, 2);
        page.Controls.Add(table);
        return page;
    }

    private static void AddNumber(TableLayoutPanel table, ref int row, string label, NumericUpDown control)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        table.Controls.Add(control, 1, row);
        row++;
    }

    private static TabPage BuildSecurity()
    {
        var page = new TabPage("安全");
        var text = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            Text = "✓ 使用 Windows DPAPI CurrentUser 加密 SecretKey 和 SessionToken\r\n\r\n" +
                   "✓ 日志隐藏 AccessKey、SecretKey、SessionToken、Authorization Header 和预签名查询串\r\n\r\n" +
                   "✓ 导出连接默认不包含凭据\r\n\r\n" +
                   "忽略证书错误仅用于受控测试环境。",
            AutoSize = false
        };
        page.Controls.Add(text);
        return page;
    }
}
