using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

internal sealed class ConnectionExportOptionsDialog : Form
{
    private readonly CheckBox _includeCredentials = new()
    {
        Text = "包含 Access Key、Secret Key 和 Session Token",
        AutoSize = true
    };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Width = 310 };
    private readonly TextBox _confirmation = new() { UseSystemPasswordChar = true, Width = 310 };
    private readonly Label _passwordLabel = new() { Text = "迁移密码：", AutoSize = true };
    private readonly Label _confirmationLabel = new() { Text = "确认密码：", AutoSize = true };
    private readonly Label _validation = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        MaximumSize = new Size(500, 0)
    };
    private readonly Button _export = new() { Text = "继续导出", Width = 90 };

    public bool IncludeCredentials => _includeCredentials.Checked;
    public string Password => _password.Text;

    public ConnectionExportOptionsDialog(int profileCount, int profilesWithCredentials)
    {
        Text = "导出连接";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(570, 330);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        var title = new Label
        {
            Text = $"将导出 {profileCount} 个连接",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 18)
        };
        var explanation = new Label
        {
            Text = "默认只导出服务地址、Region 和 Bucket 等配置，不包含任何凭据。",
            AutoSize = true,
            MaximumSize = new Size(525, 0),
            Location = new Point(20, 49),
            ForeColor = SystemColors.GrayText
        };
        _includeCredentials.Location = new Point(20, 92);
        var credentialCount = new Label
        {
            Text = $"其中 {profilesWithCredentials} 个连接具有可迁移的已保存凭据。勾选后整包会使用密码加密。",
            AutoSize = true,
            MaximumSize = new Size(525, 0),
            Location = new Point(39, 120),
            ForeColor = SystemColors.GrayText
        };
        _passwordLabel.Location = new Point(39, 165);
        _password.Location = new Point(148, 161);
        _confirmationLabel.Location = new Point(39, 201);
        _confirmation.Location = new Point(148, 197);
        _validation.Location = new Point(39, 235);

        _export.Location = new Point(365, 280);
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = 80,
            Location = new Point(465, 280)
        };

        _includeCredentials.CheckedChanged += (_, _) => UpdateCredentialControls();
        _export.Click += (_, _) => ConfirmExport();
        Controls.AddRange([
            title, explanation, _includeCredentials, credentialCount,
            _passwordLabel, _password, _confirmationLabel, _confirmation,
            _validation, _export, cancel
        ]);
        AcceptButton = _export;
        CancelButton = cancel;
        UpdateCredentialControls();
    }

    private void UpdateCredentialControls()
    {
        var enabled = _includeCredentials.Checked;
        _passwordLabel.Enabled = enabled;
        _confirmationLabel.Enabled = enabled;
        _password.Enabled = enabled;
        _confirmation.Enabled = enabled;
        _validation.Text = string.Empty;
        if (!enabled)
        {
            _password.Clear();
            _confirmation.Clear();
        }
    }

    private void ConfirmExport()
    {
        if (_includeCredentials.Checked)
        {
            if (_password.Text.Length < ConnectionArchiveService.PasswordMinimumLength)
            {
                _validation.Text = $"迁移密码至少需要 {ConnectionArchiveService.PasswordMinimumLength} 个字符。";
                _password.Focus();
                return;
            }
            if (!string.Equals(_password.Text, _confirmation.Text, StringComparison.Ordinal))
            {
                _validation.Text = "两次输入的迁移密码不一致。";
                _confirmation.Focus();
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class ConnectionArchivePasswordDialog : Form
{
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Width = 410 };

    private ConnectionArchivePasswordDialog()
    {
        Text = "解锁连接包";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(470, 170);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        var label = new Label
        {
            Text = "该连接包包含加密凭据。请输入导出时设置的迁移密码：",
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Location = new Point(20, 20)
        };
        _password.Location = new Point(20, 66);
        var unlock = new Button
        {
            Text = "解锁并预览",
            DialogResult = DialogResult.OK,
            Width = 105,
            Location = new Point(255, 116)
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = 80,
            Location = new Point(370, 116)
        };
        Controls.AddRange([label, _password, unlock, cancel]);
        AcceptButton = unlock;
        CancelButton = cancel;
        Shown += (_, _) => _password.Focus();
    }

    public static string? RequestPassword(IWin32Window owner)
    {
        using var dialog = new ConnectionArchivePasswordDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._password.Text : null;
    }
}

internal sealed class ConnectionImportPreviewDialog : Form
{
    private readonly ListView _profiles = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = true,
        View = View.Details,
        HideSelection = false
    };
    private readonly CheckBox _importCredentials = new()
    {
        Text = "导入连接包中的凭据（导入后可直接使用）",
        AutoSize = true
    };
    private readonly ComboBox _conflictStrategy = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180
    };
    private readonly Label _selectionSummary = new() { AutoSize = true };
    private readonly Button _import = new() { Text = "导入所选连接", Width = 105 };

    public IReadOnlyList<ConnectionProfile> SelectedProfiles => _profiles.CheckedItems
        .Cast<ListViewItem>()
        .Select(item => (ConnectionProfile)item.Tag!)
        .ToArray();

    public bool ImportCredentials => _importCredentials.Enabled && _importCredentials.Checked;

    public ConnectionImportConflictStrategy ConflictStrategy =>
        ((ConflictOption)_conflictStrategy.SelectedItem!).Strategy;

    public ConnectionImportPreviewDialog(
        ConnectionArchivePackage package,
        IReadOnlyCollection<ConnectionProfile> existingProfiles)
    {
        Text = "预览导入连接";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(850, 525);
        MinimumSize = new Size(720, 450);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _profiles.Columns.Add("连接名称", 175);
        _profiles.Columns.Add("类型", 125);
        _profiles.Columns.Add("Endpoint", 295);
        _profiles.Columns.Add("凭据", 85);
        _profiles.Columns.Add("名称冲突", 100);

        var existingNames = existingProfiles
            .Select(profile => profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in package.Profiles)
        {
            var item = new ListViewItem(profile.Name)
            {
                Checked = true,
                Tag = profile
            };
            item.SubItems.Add(profile.ServiceType.ToString());
            item.SubItems.Add(profile.Endpoint);
            item.SubItems.Add(profile.HasStoredCredentials ? "已包含" : "未包含");
            item.SubItems.Add(existingNames.Contains(profile.Name) ? "是" : "否");
            if (existingNames.Contains(profile.Name))
                item.ForeColor = Color.DarkOrange;
            _profiles.Items.Add(item);
        }

        _importCredentials.Enabled = package.ContainsCredentials;
        _importCredentials.Checked = false;
        _importCredentials.Text = package.ContainsCredentials
            ? "导入连接包中的凭据（导入后可直接使用）"
            : "连接包不包含凭据；导入后需要编辑连接补充凭据";
        _conflictStrategy.Items.AddRange([
            new ConflictOption("自动重命名", ConnectionImportConflictStrategy.Rename),
            new ConflictOption("覆盖同名连接", ConnectionImportConflictStrategy.Replace),
            new ConflictOption("跳过同名连接", ConnectionImportConflictStrategy.Skip)
        ]);
        _conflictStrategy.SelectedIndex = 0;

        var header = new Panel { Dock = DockStyle.Top, Height = 77, Padding = new Padding(14, 12, 14, 6) };
        var title = new Label
        {
            Text = $"连接包包含 {package.Profiles.Count} 个连接。勾选要导入的连接，然后确认凭据和重名策略。",
            AutoSize = true,
            Location = new Point(14, 12)
        };
        var exported = new Label
        {
            Text = $"导出时间：{package.ExportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(14, 39)
        };
        header.Controls.AddRange([title, exported]);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 105, Padding = new Padding(14, 8, 14, 10) };
        var selectAll = new Button { Text = "全选", Width = 65, Location = new Point(14, 8) };
        var selectNone = new Button { Text = "全不选", Width = 70, Location = new Point(86, 8) };
        _selectionSummary.Location = new Point(170, 13);
        _importCredentials.Location = new Point(14, 48);
        var conflictLabel = new Label { Text = "同名连接：", AutoSize = true, Location = new Point(460, 13) };
        _conflictStrategy.Location = new Point(540, 8);
        _import.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _import.Location = new Point(625, 64);
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = 80,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Location = new Point(742, 64)
        };
        footer.Controls.AddRange([
            selectAll, selectNone, _selectionSummary, conflictLabel,
            _conflictStrategy, _importCredentials, _import, cancel
        ]);

        Controls.Add(_profiles);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = _import;
        CancelButton = cancel;

        selectAll.Click += (_, _) => SetAllChecked(true);
        selectNone.Click += (_, _) => SetAllChecked(false);
        _profiles.ItemChecked += (_, _) => BeginInvoke(UpdateSelectionSummary);
        _import.Click += (_, _) =>
        {
            if (SelectedProfiles.Count == 0) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        UpdateSelectionSummary();
    }

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in _profiles.Items)
            item.Checked = value;
    }

    private void UpdateSelectionSummary()
    {
        if (IsDisposed) return;
        var count = _profiles.CheckedItems.Count;
        _selectionSummary.Text = $"已选择 {count} / {_profiles.Items.Count}";
        _import.Enabled = count > 0;
    }

    private sealed record ConflictOption(string Label, ConnectionImportConflictStrategy Strategy)
    {
        public override string ToString() => Label;
    }
}
