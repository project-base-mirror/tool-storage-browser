using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class StoragePermissionProbeDialog : Form
{
    private readonly ComboBox _profile = new() { Name = "StoragePermissionProbeProfile", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _bucket = new() { Name = "StoragePermissionProbeBucket" };
    private readonly TextBox _prefix = new() { Name = "StoragePermissionProbePrefix" };
    private readonly CheckBox _acl = new() { Name = "StoragePermissionProbeAcl", Text = "同时探测 PutObjectAcl", AutoSize = true };
    private readonly TextBox _confirmation = new() { Name = "StoragePermissionProbeConfirmation" };
    private readonly CheckBox _confirm = new() { Name = "StoragePermissionProbeConfirm", Text = "我确认这会在目标前缀写入并删除临时探针对象", AutoSize = true };
    private readonly Button _ok = new() { Name = "ConfirmStoragePermissionProbeButton", Text = "执行探针", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly Label _target = new() { Name = "StoragePermissionProbeTarget", AutoSize = true, ForeColor = SystemColors.GrayText };

    public StoragePermissionCheckRequest? Request { get; private set; }

    public StoragePermissionProbeDialog(IReadOnlyList<ConnectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        Name = nameof(StoragePermissionProbeDialog);
        Text = "执行存储写入权限探针";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 430);
        MinimumSize = new Size(600, 340);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        _ok.Enabled = false;
        foreach (var profile in profiles.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            _profile.Items.Add(new ProfileChoice(profile));
        if (_profile.Items.Count > 0) _profile.SelectedIndex = 0;

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 9, AutoSize = true };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, 0, "连接：", _profile);
        AddField(fields, 1, "Bucket：", _bucket);
        AddField(fields, 2, "隔离 Prefix：", _prefix);
        fields.Controls.Add(new Label { Text = "逻辑目标：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        fields.Controls.Add(_target, 1, 3);
        fields.Controls.Add(new Label { Text = "操作：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        fields.Controls.Add(new Label { Text = "上传临时对象 → 可选 Private ACL → 删除临时对象", AutoSize = true, Anchor = AnchorStyles.Left }, 1, 4);
        fields.Controls.Add(_acl, 1, 5);
        fields.Controls.Add(new Label { Text = "安全确认：输入 PROBE", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        fields.Controls.Add(_confirmation, 1, 6);
        fields.Controls.Add(_confirm, 1, 7);
        fields.Controls.Add(new Label { Text = "每次打开都必须重新确认；此操作会产生真实远端写入和删除。仅应使用专用隔离前缀。", AutoSize = true, ForeColor = Color.DarkRed, MaximumSize = new Size(500, 0) }, 1, 8);
        var cancel = new Button { Name = "CancelStoragePermissionProbeButton", Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(_ok);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(fields, 0, 0); root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
        _bucket.TextChanged += (_, _) => UpdateState();
        _prefix.TextChanged += (_, _) => UpdateState();
        _profile.SelectedIndexChanged += (_, _) => UpdateState();
        _confirmation.TextChanged += (_, _) => UpdateState();
        _confirm.CheckedChanged += (_, _) => UpdateState();
        _ok.Click += (_, e) =>
        {
            if (!TryBuildRequest())
            {
                DialogResult = DialogResult.None;
            }
        };
        AcceptButton = _ok; CancelButton = cancel;
        UpdateState();
    }

    private static void AddField(TableLayoutPanel panel, int row, string label, Control control)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(control, 1, row);
    }

    private void UpdateState()
    {
        var choice = _profile.SelectedItem as ProfileChoice;
        var bucket = _bucket.Text.Trim();
        var prefix = _prefix.Text.Replace('\\', '/').Trim('/').Trim();
        _target.Text = choice is null || bucket.Length == 0 || prefix.Length == 0
            ? "请选择连接并填写 Bucket 与隔离 Prefix"
            : $"s3://{choice.Profile.Name}/{bucket}/{prefix}/  ·  Endpoint: {choice.Profile.Endpoint}";
        _ok.Enabled = choice is not null && bucket.Length > 0 && prefix.Length > 0 &&
            string.Equals(_confirmation.Text.Trim(), "PROBE", StringComparison.Ordinal) && _confirm.Checked;
    }

    private bool TryBuildRequest()
    {
        if (!(_profile.SelectedItem is ProfileChoice choice) || !UpdateAndValidate()) return false;
        Request = new StoragePermissionCheckRequest(
            choice.Profile,
            _bucket.Text.Trim(),
            _prefix.Text.Trim(),
            StoragePermissionOperation.Publish | StoragePermissionOperation.Mirror |
                (_acl.Checked ? StoragePermissionOperation.PutObjectAcl : (StoragePermissionOperation)0),
            AllowMutation: true);
        return true;
    }

    private bool UpdateAndValidate() => _ok.Enabled;

    private sealed class ProfileChoice(ConnectionProfile profile)
    {
        public ConnectionProfile Profile { get; } = profile;
        public override string ToString() => Profile.Name;
    }
}
