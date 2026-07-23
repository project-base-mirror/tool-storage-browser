using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class ObjectPropertiesDialog : Form
{
    public ObjectPropertiesDialog(ObjectProperties properties, string endpoint, string? presignedUrl = null)
    {
        Text = $"属性 - {S3Path.DisplayName(properties.Key, false)}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 520);
        MinimumSize = new Size(620, 440);
        ShowInTaskbar = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneral(properties, endpoint, presignedUrl));
        tabs.TabPages.Add(BuildMetadata(properties));
        foreach (var name in new[] { "权限", "版本", "Tags", "Object Lock" })
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
        var close = new Button { Text = "关闭", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 38 };
        Controls.Add(tabs);
        Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;
    }

    private static TabPage BuildGeneral(ObjectProperties value, string endpoint, string? presignedUrl)
    {
        var page = new TabPage("常规");
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, AutoScroll = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
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
        for (var row = 0; row < fields.Length; row++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = fields[row].Item1 + "：", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 7, 3, 3) }, 0, row);
            var box = new TextBox { Text = fields[row].Item2, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = SystemColors.Window, Margin = new Padding(3, 7, 3, 3) };
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
}
