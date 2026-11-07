using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record ObjectTransferOptions(
    string DestinationBucket,
    string DestinationPrefix,
    ObjectConflictPolicy ConflictPolicy,
    ConnectionProfile? DestinationProfile = null);

internal sealed class ObjectTransferDialog : Form
{
    private readonly BucketPicker _bucket;
    private readonly ComboBox _profile = new()
    {
        Name = "ObjectTransferDestinationProfile", Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(ConnectionProfile.Name)
    };
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
        BucketDiscoveryCache? bucketDiscoveryCache = null,
        IReadOnlyList<ConnectionProfile>? profiles = null)
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
        ClientSize = new Size(560, move ? 500 : 450);
        MinimumSize = new Size(500, move ? 485 : 435);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        var choices = move ? Array.Empty<ConnectionProfile>() : (profiles ?? []).ToArray();
        if (profile is not null && !choices.Any(item => item.Id == profile.Id))
            choices = [profile, .. choices];
        _profile.Items.AddRange(choices.Cast<object>().ToArray());
        _profile.SelectedItem = choices.FirstOrDefault(item => item.Id == profile?.Id) ?? choices.FirstOrDefault();
        _profile.Enabled = !move && choices.Length > 1;
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
            Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 6
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddRow(table, 0, "目标连接：", _profile);
        AddRow(table, 1, "目标 Bucket：", _bucket);
        AddRow(table, 2, "目标前缀：", _prefix);
        AddRow(table, 3, "冲突处理：", _conflict);
        var notice = new Label
        {
            Name = "ObjectTransferNotice",
            Text = BuildTransferNotice(move, itemCount, profile?.ServiceType ?? S3ServiceType.AmazonS3),
            Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft,
            ForeColor = SystemColors.GrayText, Margin = new Padding(3, 12, 3, 3)
        };
        table.Controls.Add(notice, 1, 4);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Fill
        };
        buttons.Controls.AddRange([_cancel, _ok]);
        table.Controls.Add(buttons, 1, 5);
        Controls.Add(table);
        AcceptButton = _ok;
        CancelButton = _cancel;
        _ok.Click += (_, _) => Confirm();
        _profile.SelectedIndexChanged += async (_, _) =>
        {
            if (_profile.SelectedItem is not ConnectionProfile selected) return;
            var crossConnection = selected.Id != profile?.Id;
            notice.Text = BuildTransferNotice(move, itemCount, selected.ServiceType) +
                (crossConnection ? "\n\n跨连接复制经本机传输，使用源和目标各自的凭据；源对象保留。" : string.Empty);
            _bucket.BucketText = string.Empty;
            // Clear the old connection immediately, then preserve any target the user
            // types while remote bucket discovery is still running.
            await _bucket.RefreshAsync(selected, preserve: true);
        };
        Shown += async (_, _) =>
        {
            if (_profile.SelectedItem is ConnectionProfile selected)
                await _bucket.RefreshAsync(selected, preserve: true);
        };
    }

    internal static string BuildTransferNotice(bool move, int itemCount, S3ServiceType serviceType = S3ServiceType.AmazonS3)
    {
        var text = $"将{(move ? "移动" : "复制")} {itemCount:N0} 个选中项。目标前缀可留空；文件夹会保留层级。";
        if (!move && serviceType is not (S3ServiceType.AmazonS3 or S3ServiceType.MinIO or S3ServiceType.CloudflareR2))
            text += "\n\n此服务商使用写入前检查目标是否存在；检查不是原子的，请避免其他客户端同时写入目标。";
        if (serviceType is S3ServiceType.CloudflareR2 or S3ServiceType.SupabaseStorage)
            text += "\n\n超过单次复制上限的对象会经本机分片中转，使用下载与上传流量，可暂停续传。中转分片提交前会检查目标存在性，但不保证并发时原子非覆盖。";
        if (move && !S3ProviderCapabilityRegistry.For(serviceType).ConditionalDelete.Supported)
            return text + "\n\n" + S3ProviderCapabilityRegistry.For(serviceType).ConditionalDelete.Reason;
        return move
            ? text + "\n\n移动先复制，再按内容 ETag 条件删除，不是原子重命名。请避免并发修改源属性或目标；Metadata、标签和 ACL 的变化不受 ETag 保护。"
            : text;
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
            bucket, ObjectTransferPlanner.NormalizePrefix(_prefix.Text), option.Policy,
            _profile.SelectedItem as ConnectionProfile);
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
