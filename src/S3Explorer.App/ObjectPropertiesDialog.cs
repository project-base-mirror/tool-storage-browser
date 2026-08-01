using S3Explorer.Core;

namespace S3Explorer.App;

internal enum ObjectPropertiesAction
{
    None,
    DownloadFromObjectStorage,
    DownloadFromCdn
}

internal sealed partial class ObjectPropertiesDialog : Form
{
    public ObjectPropertiesAction SelectedAction { get; private set; }

    public ObjectPropertiesDialog(
        ObjectProperties properties,
        string endpoint,
        string? presignedUrl = null,
        string? cdnProfileName = null,
        IS3StorageService? storage = null,
        ConnectionProfile? profile = null)
    {
        _storage = storage;
        _profile = profile;
        _objectProperties = properties;
        Name = "ObjectPropertiesDialog";
        Text = $"属性 - {S3Path.DisplayName(properties.Key, false)}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 520);
        MinimumSize = new Size(680, 480);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneral(properties, endpoint, presignedUrl));
        tabs.TabPages.Add(BuildMetadata(properties));
        foreach (var name in new[] { "权限", "版本", "Tags" })
        {
            var page = new TabPage(name);
            page.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "当前版本尚未支持此功能。",
                ForeColor = SystemColors.GrayText
            });
            tabs.TabPages.Add(page);
        }
        tabs.TabPages.Add(BuildObjectLock(properties));
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (string.Equals(tabs.SelectedTab?.Text, "Object Lock", StringComparison.Ordinal))
                await LoadObjectLockAsync();
        };

        var objectStorageDownload = ActionButton("ObjectStorageDownloadButton", "OSS 下载...");
        objectStorageDownload.Click += (_, _) => SelectAction(ObjectPropertiesAction.DownloadFromObjectStorage);
        var cdnDownload = ActionButton("CdnDownloadButton", "CDN 下载...");
        cdnDownload.Enabled = !string.IsNullOrWhiteSpace(cdnProfileName);
        cdnDownload.AccessibleDescription = cdnDownload.Enabled
            ? $"通过默认 CDN 配置 {cdnProfileName} 下载"
            : "当前对象没有匹配的 CDN 关联";
        cdnDownload.Click += (_, _) => SelectAction(ObjectPropertiesAction.DownloadFromCdn);
        var close = ActionButton("CloseObjectPropertiesButton", "关闭");
        close.DialogResult = DialogResult.Cancel;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 7, 8, 7),
            Margin = Padding.Empty
        };
        actions.Controls.Add(close);
        actions.Controls.Add(cdnDownload);
        actions.Controls.Add(objectStorageDownload);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
        AcceptButton = close;
        CancelButton = close;
        FormClosed += (_, _) => _objectLockCancellation.Cancel();
    }

    private static TabPage BuildGeneral(ObjectProperties value, string endpoint, string? presignedUrl)
    {
        var page = new TabPage("常规");
        var table = new TableLayoutPanel
        {
            Name = "ObjectPropertiesGeneralTable",
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            AutoScroll = true
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var fields = new (string, string)[]
        {
            ("名称", S3Path.DisplayName(value.Key, false)),
            ("完整 Key", value.Key),
            ("Bucket", value.Bucket),
            ("Endpoint", endpoint),
            ("大小", $"{FileSizeFormatter.Format(value.Size)} ({value.Size:N0} bytes)"),
            ("Last Modified", value.LastModified?.LocalDateTime.ToString("G") ?? "—"),
            ("ETag", value.ETag ?? "—"),
            ("Content-Type", value.ContentType ?? "—"),
            ("Storage Class", value.StorageClass ?? "—"),
            ("Version ID", value.VersionId ?? "—"),
            ("预签名 URL", presignedUrl is null ? "未生成" : "已生成（出于安全考虑不在日志中记录）")
        };
        table.RowCount = fields.Length;
        for (var row = 0; row < fields.Length; row++)
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        for (var row = 0; row < fields.Length; row++)
        {
            table.Controls.Add(new Label
            {
                Name = $"ObjectPropertyLabel{row}",
                Text = fields[row].Item1 + "：",
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Font = new Font(baseFont, FontStyle.Bold),
                Margin = new Padding(3, 7, 9, 3)
            }, 0, row);
            var box = new TextBox
            {
                Name = $"ObjectPropertyValue{row}",
                Text = fields[row].Item2,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Window,
                Margin = new Padding(3, 7, 3, 3)
            };
            table.Controls.Add(box, 1, row);
        }
        page.Controls.Add(table);
        return page;
    }

    private static TabPage BuildMetadata(ObjectProperties value)
    {
        var page = new TabPage("Metadata");
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("名称", 260);
        list.Columns.Add("值", 390);
        foreach (var pair in value.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem(pair.Key);
            item.SubItems.Add(pair.Value);
            list.Items.Add(item);
        }
        if (list.Items.Count == 0)
            list.Items.Add(new ListViewItem("(无自定义 Metadata)") { ForeColor = SystemColors.GrayText });
        page.Controls.Add(list);
        return page;
    }

    private static Button ActionButton(string name, string text) => new()
    {
        Name = name,
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(112, 36),
        Margin = new Padding(6, 0, 0, 0),
        UseVisualStyleBackColor = true
    };

    private void SelectAction(ObjectPropertiesAction action)
    {
        SelectedAction = action;
        DialogResult = DialogResult.OK;
        Close();
    }
}
