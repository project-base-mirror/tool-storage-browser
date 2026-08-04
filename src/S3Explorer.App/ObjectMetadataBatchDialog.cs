using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class ObjectMetadataBatchDialog : Form
{
    private readonly Dictionary<string, (CheckBox Apply, TextBox Value)> _headers = new(StringComparer.Ordinal);
    private readonly CheckBox _replaceMetadata = new()
    {
        Name = "ReplaceBatchObjectMetadata",
        Text = "替换自定义 Metadata（未勾选时逐对象保留现值）",
        AutoSize = true
    };
    private readonly DataGridView _metadata = new()
    {
        Name = "BatchObjectMetadataGrid",
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false
    };

    public ObjectMetadataBatchDialog(int objectCount)
    {
        Name = "ObjectMetadataBatchDialog";
        Text = $"批量 Header / Metadata - {objectCount:N0} 个对象";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(680, 500);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        _metadata.Columns.Add("Key", "Metadata Key");
        _metadata.Columns.Add("Value", "值");
        _metadata.Enabled = false;
        _replaceMetadata.CheckedChanged += (_, _) => _metadata.Enabled = _replaceMetadata.Checked;

        var headers = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(10)
        };
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddHeader(headers, "Content-Type", "content-type");
        AddHeader(headers, "Cache-Control", "cache-control");
        AddHeader(headers, "Content-Encoding", "content-encoding");
        AddHeader(headers, "Content-Disposition", "content-disposition");
        AddHeader(headers, "Expires UTC", "expires");

        var warning = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            ForeColor = Color.DarkRed,
            Text = "每个对象会执行一次服务端原地 Copy；可能产生请求费、生成新版本，并受跨区域或 Provider Copy 规则限制。空值表示清除已勾选的 Header。",
            Margin = new Padding(10, 3, 10, 8)
        };
        var metadataHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8, 3, 8, 3)
        };
        metadataHeader.Controls.Add(_replaceMetadata);

        var ok = new Button
        {
            Name = "ApplyBatchObjectMetadataButton",
            Text = "预览并应用",
            AutoSize = true,
            MinimumSize = new Size(120, 36),
            DialogResult = DialogResult.OK
        };
        var cancel = new Button
        {
            Name = "CancelBatchObjectMetadataButton",
            Text = "取消",
            AutoSize = true,
            MinimumSize = new Size(100, 36),
            DialogResult = DialogResult.Cancel
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8)
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(ok);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(headers, 0, 0);
        layout.Controls.Add(warning, 0, 1);
        layout.Controls.Add(metadataHeader, 0, 2);
        layout.Controls.Add(_metadata, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public bool Applies(string name) => _headers[name].Apply.Checked;

    public string? HeaderValue(string name)
    {
        var value = _headers[name].Value.Text.Trim();
        return value.Length == 0 ? null : value;
    }

    public DateTimeOffset? ExpiresUtc
    {
        get
        {
            if (!Applies("expires") || HeaderValue("expires") is not { } text) return null;
            if (!DateTimeOffset.TryParse(text, out var value))
                throw new ArgumentException("Expires UTC 必须为空或有效的 ISO 8601 日期时间。");
            return value.ToUniversalTime();
        }
    }

    public bool ReplacesMetadata => _replaceMetadata.Checked;

    public IReadOnlyDictionary<string, string> ReadMetadata()
    {
        var values = _metadata.Rows.Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .Select(row => new KeyValuePair<string, string>(
                row.Cells[0].Value?.ToString()?.Trim() ?? string.Empty,
                row.Cells[1].Value?.ToString()?.Trim() ?? string.Empty))
            .Where(pair => pair.Key.Length > 0 || pair.Value.Length > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return ObjectMetadataValidator.Validate(values);
    }

    public ObjectWriteHeaders ApplyTo(ObjectProperties current)
    {
        var metadata = ReplacesMetadata ? ReadMetadata() : current.Metadata;
        return new ObjectWriteHeaders(
            Applies("content-type") ? HeaderValue("content-type") : current.ContentType,
            Applies("cache-control") ? HeaderValue("cache-control") : current.CacheControl,
            Applies("content-encoding") ? HeaderValue("content-encoding") : current.ContentEncoding,
            Applies("content-disposition") ? HeaderValue("content-disposition") : current.ContentDisposition,
            Applies("expires") ? ExpiresUtc : current.ExpiresUtc,
            metadata).ValidateAndNormalize();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            try
            {
                if (!_headers.Values.Any(item => item.Apply.Checked) && !ReplacesMetadata)
                    throw new ArgumentException("至少勾选一个 Header，或勾选替换自定义 Metadata。");
                _ = ExpiresUtc;
                if (ReplacesMetadata) _ = ReadMetadata();
            }
            catch (Exception exception)
            {
                e.Cancel = true;
                ErrorDialog.ShowException(this, "批量 Metadata 无效", "校验批量 Header / Metadata", exception);
            }
        }
        base.OnFormClosing(e);
    }

    private void AddHeader(TableLayoutPanel table, string label, string name)
    {
        var apply = new CheckBox
        {
            Name = $"ApplyBatch{name.Replace("-", string.Empty, StringComparison.Ordinal)}",
            Text = "应用",
            AutoSize = true,
            Margin = new Padding(3, 7, 8, 3)
        };
        var value = new TextBox
        {
            Name = $"Batch{name.Replace("-", string.Empty, StringComparison.Ordinal)}",
            Dock = DockStyle.Fill,
            Enabled = false,
            Margin = new Padding(3, 4, 3, 3)
        };
        apply.CheckedChanged += (_, _) => value.Enabled = apply.Checked;
        _headers[name] = (apply, value);
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(apply, 0, row);
        table.Controls.Add(new Label
        {
            Text = label + "：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 8, 3)
        }, 1, row);
        table.Controls.Add(value, 2, row);
    }
}
