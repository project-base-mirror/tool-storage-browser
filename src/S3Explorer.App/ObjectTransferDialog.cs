using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record ObjectTransferOptions(
    string DestinationBucket,
    string DestinationPrefix,
    ObjectConflictPolicy ConflictPolicy);

internal sealed class ObjectTransferDialog : Form
{
    private readonly BucketPicker _bucket;
    private readonly TextBox _prefix = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _conflict = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _ok = new() { Text = "确定", Size = new Size(92, 32) };
    private readonly Button _cancel = new() { Text = "取消", Size = new Size(92, 32), DialogResult = DialogResult.Cancel };

    public ObjectTransferDialog(
        bool move,
        string bucket,
        string prefix,
        int itemCount,
        ConnectionProfile? profile = null,
        IS3StorageService? storage = null,
        BucketDiscoveryCache? bucketDiscoveryCache = null)
    {
        _bucket = new BucketPicker(
            bucketDiscoveryCache ?? new BucketDiscoveryCache(),
            storage is null
                ? null
                : async (selectedProfile, token) =>
                    (await storage.ListBucketsAsync(selectedProfile, token).ConfigureAwait(true))
                    .Select(value => value.Name)
                    .ToArray())
        {
            Name = "ObjectTransferDestinationBucket",
            Dock = DockStyle.Fill
        };
        Text = move ? "移动到" : "复制到";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 275);
        MinimumSize = new Size(500, 260);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _bucket.BucketText = bucket;
        _prefix.Text = prefix;
        _conflict.DisplayMember = nameof(ConflictOption.Text);
        _conflict.ValueMember = nameof(ConflictOption.Policy);
        _conflict.Items.AddRange([
            new ConflictOption("覆盖已有对象", ObjectConflictPolicy.Overwrite),
            new ConflictOption("跳过已有对象", ObjectConflictPolicy.Skip),
            new ConflictOption("自动重命名", ObjectConflictPolicy.AutoRename),
            new ConflictOption("逐项询问", ObjectConflictPolicy.Ask)
        ]);
        _conflict.SelectedIndex = 3;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 5
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddRow(table, 0, "目标 Bucket：", _bucket);
        AddRow(table, 1, "目标前缀：", _prefix);
        AddRow(table, 2, "冲突处理：", _conflict);
        table.Controls.Add(new Label
        {
            Text = $"将{(move ? "移动" : "复制")} {itemCount:N0} 个选中项。目标前缀可留空；文件夹会保留层级。",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 12, 3, 3)
        }, 1, 3);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Fill
        };
        buttons.Controls.AddRange([_cancel, _ok]);
        table.Controls.Add(buttons, 1, 4);
        Controls.Add(table);
        AcceptButton = _ok;
        CancelButton = _cancel;
        _ok.Click += (_, _) => Confirm();
        if (profile is not null)
        {
            Shown += async (_, _) => await _bucket.RefreshAsync(profile, preserve: true);
        }
    }

    public ObjectTransferOptions? Options { get; private set; }

    private void Confirm()
    {
        var bucket = _bucket.BucketText.Trim();
        if (string.IsNullOrWhiteSpace(bucket))
        {
            MessageBox.Show(this, "目标 Bucket 不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var option = (ConflictOption)_conflict.SelectedItem!;
        Options = new ObjectTransferOptions(
            bucket, ObjectTransferPlanner.NormalizePrefix(_prefix.Text), option.Policy);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control control)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 8) }, 0, row);
        control.Margin = new Padding(3, 5, 3, 5);
        table.Controls.Add(control, 1, row);
    }

    private sealed record ConflictOption(string Text, ObjectConflictPolicy Policy);
}
