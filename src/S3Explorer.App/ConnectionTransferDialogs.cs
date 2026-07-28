using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

internal sealed class ConnectionExportOptionsDialog : Form
{
    private readonly CheckBox _includeCredentials = new()
    {
        Name = "IncludeStoredCredentialsCheckBox",
        Text = "包含已保存凭据（S3 密钥及 CDN Token/Header 密钥）",
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

    public ConnectionExportOptionsDialog(
        int profileCount,
        int profilesWithCredentials,
        int cdnProfileCount = 0,
        int cdnCredentials = 0)
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
            Text = cdnProfileCount > 0
                ? $"将导出 {profileCount} 个对象存储连接和 {cdnProfileCount} 个 CDN 配置"
                : $"将导出 {profileCount} 个对象存储连接",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 18)
        };
        var explanation = new Label
        {
            Text = "默认只导出服务地址、Region、Bucket、CDN 地址和关联等配置，不包含任何秘密值。",
            AutoSize = true,
            MaximumSize = new Size(525, 0),
            Location = new Point(20, 49),
            ForeColor = SystemColors.GrayText
        };
        _includeCredentials.Location = new Point(20, 92);
        _includeCredentials.Enabled = profilesWithCredentials + cdnCredentials > 0;
        var credentialCount = new Label
        {
            Text = profilesWithCredentials + cdnCredentials > 0
                ? $"可迁移凭据：S3 连接 {profilesWithCredentials} 个，CDN 凭据 {cdnCredentials} 个。勾选后整包会使用密码加密。"
                : "所选内容没有可迁移的已保存密钥；AWS 外部来源只导出非敏感引用，CDN 认证引用会移除。",
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
    private readonly TextBox _password = new()
    {
        UseSystemPasswordChar = true,
        Dock = DockStyle.Fill
    };

    internal ConnectionArchivePasswordDialog()
    {
        Name = nameof(ConnectionArchivePasswordDialog);
        Text = "解锁连接包";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(510, 200);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        var label = new Label
        {
            Text = "该连接包包含加密凭据。请输入导出时设置的迁移密码：",
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(470, 0),
            Margin = new Padding(0)
        };
        _password.Margin = new Padding(0, 12, 0, 0);
        var unlock = new Button
        {
            Name = "UnlockConnectionArchiveButton",
            Text = "解锁并预览",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(126, 34),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(8, 0, 0, 0)
        };
        var cancel = new Button
        {
            Name = "CancelUnlockButton",
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 34),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(8, 0, 0, 0)
        };
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 18, 0, 0)
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(unlock);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(20)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(_password, 0, 1);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
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
    private readonly Button _import = new()
    {
        Name = "ImportConnectionsButton",
        Text = "导入所选连接",
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(132, 34),
        Padding = new Padding(10, 2, 10, 2),
        Margin = new Padding(8, 0, 0, 0)
    };

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
        Name = nameof(ConnectionImportPreviewDialog);
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
            item.SubItems.Add(profile.UsesExternalAwsCredentials
                ? profile.CredentialSourceDisplayName
                : profile.HasStoredCredentials ? "已包含" : "未包含");
            item.SubItems.Add(existingNames.Contains(profile.Name) ? "是" : "否");
            if (existingNames.Contains(profile.Name))
                item.ForeColor = Color.DarkOrange;
            _profiles.Items.Add(item);
        }

        _importCredentials.Enabled = package.ContainsCredentials;
        _importCredentials.Checked = false;
        _importCredentials.Text = package.ContainsCredentials
            ? $"导入包内凭据：S3 已保存密钥及 {package.ImportedCdnCredentials.Count} 个 CDN Token/Header 密钥"
            : "连接包不包含秘密值；AWS 外部来源引用仍会导入，CDN 认证需重新关联";
        _conflictStrategy.Items.AddRange([
            new ConflictOption("自动重命名", ConnectionImportConflictStrategy.Rename),
            new ConflictOption("覆盖同名连接", ConnectionImportConflictStrategy.Replace),
            new ConflictOption("跳过同名连接", ConnectionImportConflictStrategy.Skip)
        ]);
        _conflictStrategy.SelectedIndex = 0;

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14, 12, 14, 8),
            Margin = new Padding(0)
        };
        var cdnConfiguration = package.ImportedCdnConfiguration;
        var title = new Label
        {
            Name = "ConnectionImportSummary",
            Text = $"连接包包含 {package.Profiles.Count} 个对象存储连接、" +
                   $"{cdnConfiguration.Profiles.Count} 个 CDN 配置和 {cdnConfiguration.Bindings.Count} 个关联。" +
                   "勾选连接后，相关 CDN 项会一并导入。",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        var exported = new Label
        {
            Text = $"导出时间：{package.ExportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0)
        };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(exported, 0, 1);

        var selectAll = new Button
        {
            Name = "SelectAllConnectionsButton",
            Text = "全选",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(84, 34),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(0)
        };
        var selectNone = new Button
        {
            Name = "SelectNoConnectionsButton",
            Text = "全不选",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(92, 34),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(8, 0, 0, 0)
        };
        _selectionSummary.Margin = new Padding(12, 8, 0, 0);
        _importCredentials.Margin = new Padding(0, 12, 0, 0);
        var conflictLabel = new Label
        {
            Text = "同名连接：",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        _conflictStrategy.Margin = new Padding(8, 3, 0, 0);
        var cancel = new Button
        {
            Name = "CancelConnectionImportButton",
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 34),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(8, 0, 0, 0)
        };

        var selectionActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        selectionActions.Controls.AddRange([selectAll, selectNone, _selectionSummary]);

        var conflictOptions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(12, 0, 0, 0)
        };
        conflictOptions.Controls.AddRange([conflictLabel, _conflictStrategy]);

        var importActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(12, 12, 0, 0)
        };
        importActions.Controls.Add(cancel);
        importActions.Controls.Add(_import);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(14, 8, 14, 10),
            Margin = new Padding(0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(selectionActions, 0, 0);
        footer.Controls.Add(conflictOptions, 1, 0);
        footer.Controls.Add(_importCredentials, 0, 1);
        footer.Controls.Add(importActions, 1, 1);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_profiles, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
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
