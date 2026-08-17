using S3Explorer.Core;
using S3Explorer.Infrastructure.S3;

namespace S3Explorer.App;

internal sealed class ConnectionExportOptionsDialog : Form
{
    private readonly CheckBox _includeCredentials = new()
    {
        Name = "IncludeStoredCredentialsCheckBox",
        Text = "包含所选配置引用的统一凭据",
        AutoSize = true
    };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly TextBox _confirmation = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly Label _passwordLabel = new() { Text = "迁移密码：", AutoSize = true };
    private readonly Label _confirmationLabel = new() { Text = "确认密码：", AutoSize = true };
    private readonly Label _validation = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        MaximumSize = new Size(500, 0)
    };
    private readonly Button _export = new()
    {
        Name = "ContinueConnectionExportButton",
        Text = "继续导出",
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(112, 36),
        Padding = new Padding(10, 2, 10, 2),
        Margin = new Padding(8, 0, 0, 0)
    };

    public bool IncludeCredentials => _includeCredentials.Checked;
    public string Password => _password.Text;

    public ConnectionExportOptionsDialog(
        int profileCount,
        int cdnProfileCount = 0,
        int credentialCount = 0)
    {
        Text = "导出连接";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 410);
        MinimumSize = new Size(600, 390);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        var title = new Label
        {
            Text = cdnProfileCount > 0
                ? $"将导出 {profileCount} 个对象存储连接和 {cdnProfileCount} 个 CDN 配置"
                : $"将导出 {profileCount} 个对象存储连接",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10)
        };
        var explanation = new Label
        {
            Text = "默认只导出服务地址、Region、Bucket、CDN 地址和关联等配置，不包含任何秘密值。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            ForeColor = SystemColors.GrayText
        };
        _includeCredentials.Margin = new Padding(0, 0, 0, 6);
        _includeCredentials.Enabled = credentialCount > 0;
        var credentialSummary = new Label
        {
            Text = credentialCount > 0
                ? $"可迁移统一凭据：{credentialCount} 个。共享凭据只导出一次；勾选后整包会使用密码加密。"
                : "所选内容没有可迁移的已保存凭据；未携带凭据的引用会在导出包中移除。",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Dock = DockStyle.Fill,
            Margin = new Padding(20, 0, 0, 10),
            ForeColor = SystemColors.GrayText
        };
        _passwordLabel.Anchor = AnchorStyles.Left;
        _passwordLabel.Margin = new Padding(20, 8, 12, 8);
        _password.Margin = new Padding(0, 5, 0, 5);
        _confirmationLabel.Anchor = AnchorStyles.Left;
        _confirmationLabel.Margin = new Padding(20, 8, 12, 8);
        _confirmation.Margin = new Padding(0, 5, 0, 5);
        _validation.Dock = DockStyle.Fill;
        _validation.Margin = new Padding(20, 2, 0, 6);
        var cancel = new Button
        {
            Name = "CancelConnectionExportButton",
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 36),
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
            Margin = new Padding(0, 10, 0, 0)
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(_export);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(20)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(explanation, 0, 1);
        layout.SetColumnSpan(explanation, 2);
        layout.Controls.Add(_includeCredentials, 0, 2);
        layout.SetColumnSpan(_includeCredentials, 2);
        layout.Controls.Add(credentialSummary, 0, 3);
        layout.SetColumnSpan(credentialSummary, 2);
        layout.Controls.Add(_passwordLabel, 0, 4);
        layout.Controls.Add(_password, 1, 4);
        layout.Controls.Add(_confirmationLabel, 0, 5);
        layout.Controls.Add(_confirmation, 1, 5);
        layout.Controls.Add(_validation, 0, 6);
        layout.SetColumnSpan(_validation, 2);
        layout.Controls.Add(actions, 0, 8);
        layout.SetColumnSpan(actions, 2);

        _includeCredentials.CheckedChanged += (_, _) => UpdateCredentialControls();
        _export.Click += (_, _) => ConfirmExport();
        Controls.Add(layout);
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
        Name = "StorageImportPreviewList",
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = true,
        View = View.Details,
        HideSelection = false
    };
    private readonly ListView _cdnProfiles = new()
    {
        Name = "CdnImportPreviewList",
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = true,
        View = View.Details,
        HideSelection = false
    };
    private readonly TabControl _tabs = new()
    {
        Name = "ConnectionImportTabs",
        Dock = DockStyle.Fill
    };
    private readonly CheckBox _importCredentials = new()
    {
        Name = "ImportUnifiedCredentialsCheckBox",
        Text = "导入所选对象存储连接与 CDN 配置引用的统一凭据",
        AutoSize = true
    };
    private readonly ComboBox _conflictStrategy = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180
    };
    private readonly ComboBox _targetGroup = new()
    {
        Name = "ConnectionImportTargetGroupComboBox",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180
    };
    private readonly Label _selectionSummary = new() { AutoSize = true };
    private readonly Label _dependencySummary = new()
    {
        Name = "ConnectionImportDependencySummary",
        AutoSize = true,
        ForeColor = SystemColors.GrayText
    };
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

    public IReadOnlyList<CdnProfile> SelectedCdnProfiles => _cdnProfiles.CheckedItems
        .Cast<ListViewItem>()
        .Select(item => (CdnProfile)item.Tag!)
        .ToArray();

    public bool ImportCredentials =>
        _importCredentials.Enabled && _importCredentials.Checked;

    public ConnectionImportConflictStrategy ConflictStrategy =>
        ((ConflictOption)_conflictStrategy.SelectedItem!).Strategy;

    public Guid? TargetGroupId =>
        (_targetGroup.SelectedItem as TargetGroupOption)?.GroupId;

    private readonly ConnectionArchivePackage _package;
    private readonly IReadOnlyCollection<ConnectionProfile> _existingProfiles;
    private readonly CdnConfiguration _existingCdnConfiguration;
    private readonly IReadOnlyCollection<CredentialProfile> _existingCredentials;
    private readonly ConnectionArchiveService _archiveService;
    private ConnectionArchiveImportPreview _preview;

    public ConnectionImportPreviewDialog(
        ConnectionArchivePackage package,
        IReadOnlyCollection<ConnectionProfile> existingProfiles,
        CdnConfiguration? existingCdnConfiguration = null,
        IReadOnlyCollection<CredentialProfile>? existingCredentials = null,
        ConnectionArchiveService? archiveService = null,
        IReadOnlyCollection<ConnectionGroup>? groups = null)
    {
        _package = package;
        _existingProfiles = existingProfiles;
        _existingCdnConfiguration = existingCdnConfiguration ?? CdnConfiguration.Empty;
        _existingCredentials = existingCredentials ?? [];
        _archiveService = archiveService ?? new ConnectionArchiveService();
        _preview = _archiveService.PreviewPackage(
            _existingProfiles,
            _existingCdnConfiguration,
            _existingCredentials,
            _package);
        Name = nameof(ConnectionImportPreviewDialog);
        Text = "预览导入连接";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(930, 650);
        MinimumSize = new Size(780, 560);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        _profiles.Columns.Add("连接名称", 175);
        _profiles.Columns.Add("类型", 125);
        _profiles.Columns.Add("Endpoint", 285);
        _profiles.Columns.Add("凭据", 105);
        _profiles.Columns.Add("预计处理", 170);
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
            item.SubItems.Add(string.Empty);
            _profiles.Items.Add(item);
        }

        _cdnProfiles.Columns.Add("CDN 配置", 170);
        _cdnProfiles.Columns.Add("基础 URL", 300);
        _cdnProfiles.Columns.Add("关联", 70);
        _cdnProfiles.Columns.Add("依赖", 115);
        _cdnProfiles.Columns.Add("预计处理", 170);
        foreach (var profile in package.ImportedCdnConfiguration.Profiles)
        {
            var bindings = package.ImportedCdnConfiguration.Bindings
                .Where(binding => binding.CdnProfileId == profile.Id)
                .ToArray();
            var item = new ListViewItem(profile.Name)
            {
                Checked = true,
                Tag = profile
            };
            item.SubItems.Add(profile.BaseUrl);
            item.SubItems.Add(bindings.Length.ToString());
            item.SubItems.Add($"{bindings.Select(binding => binding.StorageProfileId).Distinct().Count()} 个连接");
            item.SubItems.Add(string.Empty);
            _cdnProfiles.Items.Add(item);
        }

        var credentialCount = package.ImportedCredentials.Count;
        _importCredentials.Enabled = package.ContainsCredentials && credentialCount > 0;
        _importCredentials.Checked = false;
        _importCredentials.Text = _importCredentials.Enabled
            ? $"导入所选配置引用的统一凭据（共 {credentialCount} 个；共享凭据只导入一次）"
            : "连接包没有可迁移的统一凭据";
        _importCredentials.MaximumSize = new Size(760, 0);
        _conflictStrategy.Items.AddRange([
            new ConflictOption("自动重命名", ConnectionImportConflictStrategy.Rename),
            new ConflictOption("覆盖同名连接", ConnectionImportConflictStrategy.Replace),
            new ConflictOption("跳过同名连接", ConnectionImportConflictStrategy.Skip)
        ]);
        _conflictStrategy.SelectedIndex = 0;
        _targetGroup.Items.Add(new TargetGroupOption("未分组", null));
        foreach (var group in (groups ?? []).OrderBy(group => group.SortOrder))
            _targetGroup.Items.Add(new TargetGroupOption(group.Name, group.Id));
        _targetGroup.SelectedIndex = 0;

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
                   "请分别在两个标签页中选择；默认不导入任何秘密值。",
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
            Text = "本页全选",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(105, 36),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(0)
        };
        var selectNone = new Button
        {
            Name = "SelectNoConnectionsButton",
            Text = "本页全不选",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(115, 36),
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(8, 0, 0, 0)
        };
        _selectionSummary.Margin = new Padding(12, 8, 0, 0);
        _importCredentials.Margin = new Padding(0);
        _dependencySummary.Margin = new Padding(0, 6, 0, 0);
        var conflictLabel = new Label
        {
            Text = "同名项目：",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        _conflictStrategy.Margin = new Padding(8, 3, 0, 0);
        var targetGroupLabel = new Label
        {
            Text = "目标分组：",
            AutoSize = true,
            Margin = new Padding(18, 8, 0, 0)
        };
        _targetGroup.Margin = new Padding(8, 3, 0, 0);
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
            WrapContents = true,
            Margin = new Padding(0)
        };
        selectionActions.Controls.AddRange([selectAll, selectNone, _selectionSummary]);

        var conflictOptions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        conflictOptions.Controls.AddRange([conflictLabel, _conflictStrategy, targetGroupLabel, _targetGroup]);

        var importActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0)
        };
        importActions.Controls.Add(cancel);
        importActions.Controls.Add(_import);

        var credentialOptions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        credentialOptions.Controls.Add(_importCredentials);

        var storageTab = new TabPage("对象存储连接") { Name = "StorageImportTab" };
        storageTab.Controls.Add(_profiles);
        var cdnTab = new TabPage("CDN 配置") { Name = "CdnImportTab" };
        cdnTab.Controls.Add(_cdnProfiles);
        _tabs.TabPages.Add(storageTab);
        _tabs.TabPages.Add(cdnTab);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14, 8, 14, 10),
            Margin = new Padding(0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 5; row++)
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(selectionActions, 0, 0);
        footer.Controls.Add(conflictOptions, 0, 1);
        footer.Controls.Add(credentialOptions, 0, 2);
        footer.Controls.Add(_dependencySummary, 0, 3);
        footer.Controls.Add(importActions, 0, 4);

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
        layout.Controls.Add(_tabs, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        AcceptButton = _import;
        CancelButton = cancel;

        selectAll.Click += (_, _) => SetAllChecked(true);
        selectNone.Click += (_, _) => SetAllChecked(false);
        _profiles.ItemChecked += (_, _) => BeginInvoke(UpdateSelectionSummary);
        _cdnProfiles.ItemChecked += (_, _) => BeginInvoke(UpdateSelectionSummary);
        _tabs.SelectedIndexChanged += (_, _) => UpdateSelectionSummary();
        _importCredentials.CheckedChanged += (_, _) => RefreshPreviewStatuses();
        _import.Click += (_, _) =>
        {
            if (SelectedProfiles.Count == 0 && SelectedCdnProfiles.Count == 0) return;
            var missing = MissingDependencyCount();
            if (missing > 0 && MessageBox.Show(
                    this,
                    $"有 {missing} 个 CDN 关联缺少已选择或本机等价的对象存储连接，这些关联将被跳过。是否继续？",
                    "CDN 关联依赖不完整",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            DialogResult = DialogResult.OK;
            Close();
        };
        RefreshPreviewStatuses();
        UpdateSelectionSummary();
    }

    private void SetAllChecked(bool value)
    {
        var list = _tabs.SelectedTab?.Name == "CdnImportTab" ? _cdnProfiles : _profiles;
        foreach (ListViewItem item in list.Items)
            item.Checked = value;
    }

    private void RefreshPreviewStatuses()
    {
        _preview = _archiveService.PreviewPackage(
            _existingProfiles,
            _existingCdnConfiguration,
            _existingCredentials,
            _package,
            ImportCredentials,
            ImportCredentials);
        foreach (ListViewItem item in _profiles.Items)
        {
            var profile = (ConnectionProfile)item.Tag!;
            var preview = _preview.StorageProfiles.Single(value => value.ImportedId == profile.Id);
            item.SubItems[4].Text = PreviewStatusText(preview.Status, preview.ExistingName);
            item.ForeColor = PreviewStatusColor(preview.Status);
        }
        foreach (ListViewItem item in _cdnProfiles.Items)
        {
            var profile = (CdnProfile)item.Tag!;
            var preview = _preview.CdnProfiles.Single(value => value.ImportedId == profile.Id);
            item.SubItems[4].Text = PreviewStatusText(preview.Status, preview.ExistingName);
            item.ForeColor = PreviewStatusColor(preview.Status);
        }
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        if (IsDisposed) return;
        var current = _tabs.SelectedTab?.Name == "CdnImportTab" ? _cdnProfiles : _profiles;
        _selectionSummary.Text = $"本页已选择 {current.CheckedItems.Count} / {current.Items.Count}";
        var storageCount = _profiles.CheckedItems.Count;
        var cdnCount = _cdnProfiles.CheckedItems.Count;
        var missing = MissingDependencyCount();
        _dependencySummary.Text = missing == 0
            ? $"最终选择：对象存储 {storageCount} 个，CDN {cdnCount} 个；关联依赖完整。"
            : $"最终选择：对象存储 {storageCount} 个，CDN {cdnCount} 个；{missing} 个关联将因缺少对象存储依赖而跳过。";
        _dependencySummary.ForeColor = missing == 0 ? SystemColors.GrayText : Color.DarkOrange;
        _import.Enabled = storageCount + cdnCount > 0;
    }

    private int MissingDependencyCount()
    {
        var selectedStorageIds = SelectedProfiles.Select(profile => profile.Id).ToHashSet();
        var selectedCdnIds = SelectedCdnProfiles.Select(profile => profile.Id).ToHashSet();
        return _preview.CdnProfiles
            .Where(preview => selectedCdnIds.Contains(preview.ImportedId))
            .SelectMany(preview => preview.MissingStorageProfileIds)
            .Distinct()
            .Count(storageId => !selectedStorageIds.Contains(storageId));
    }

    private static string PreviewStatusText(ConnectionArchiveImportStatus status, string existingName) => status switch
    {
        ConnectionArchiveImportStatus.ExistingEquivalent => $"复用：{existingName}",
        ConnectionArchiveImportStatus.NameConflict => "同名但配置不同",
        _ => "新增"
    };

    private static Color PreviewStatusColor(ConnectionArchiveImportStatus status) => status switch
    {
        ConnectionArchiveImportStatus.ExistingEquivalent => Color.DarkGreen,
        ConnectionArchiveImportStatus.NameConflict => Color.DarkOrange,
        _ => SystemColors.WindowText
    };

    private sealed record ConflictOption(string Label, ConnectionImportConflictStrategy Strategy)
    {
        public override string ToString() => Label;
    }

    private sealed record TargetGroupOption(string Label, Guid? GroupId)
    {
        public override string ToString() => Label;
    }
}
