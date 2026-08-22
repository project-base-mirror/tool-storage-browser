using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class CredentialCenterDialog : Form
{
    private readonly IReadOnlyList<ConnectionProfile> _storageProfiles;
    private readonly CdnConfiguration _cdnConfiguration;
    private readonly IReadOnlyList<ConnectionGroup> _connectionGroups;
    private readonly IS3StorageService? _storage;
    private readonly Func<CredentialProfile, PermissionCheckReport, CancellationToken, Task>? _persistCheck;
    private readonly List<CredentialProfile> _credentials;
    private readonly DataGridView _grid;
    private readonly Button _save = new()
    {
        Name = "SaveCredentialCenterButton",
        Text = "保存更改",
        AutoSize = true,
        MinimumSize = new Size(112, 36),
        Enabled = false
    };
    private readonly Button _cancel = new()
    {
        Name = "CancelCredentialCenterButton",
        Text = "取消",
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
        MinimumSize = new Size(96, 36)
    };
    private readonly Label _dirtyStatus = new()
    {
        Name = "CredentialCenterDirtyStatus",
        Text = "没有未保存的更改",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Anchor = AnchorStyles.Left
    };
    private readonly Button _check = new() { Name = "CheckCredentialPermissionsButton", Text = "检查关联权限", AutoSize = true, MinimumSize = new Size(124, 34) };
    private CancellationTokenSource? _checkCancellation;
    private bool _isDirty;
    private bool _accepting;

    public IReadOnlyList<CredentialProfile> Credentials { get; private set; }

    public CredentialCenterDialog(
        IReadOnlyList<ConnectionProfile> storageProfiles,
        IReadOnlyList<CredentialProfile> credentials,
        CdnConfiguration cdnConfiguration,
        IS3StorageService? storage = null,
        Func<CredentialProfile, PermissionCheckReport, CancellationToken, Task>? persistCheck = null,
        IReadOnlyList<ConnectionGroup>? connectionGroups = null)
    {
        _storageProfiles = storageProfiles;
        _cdnConfiguration = cdnConfiguration;
        _connectionGroups = connectionGroups ?? [];
        _storage = storage;
        _persistCheck = persistCheck;
        _credentials = [.. credentials];
        Credentials = credentials;
        Name = "CredentialCenterDialog";
        Text = "凭据中心";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(620, 420);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        _grid = new DataGridView
        {
            Name = "CredentialCenterGrid", Dock = DockStyle.Fill, ReadOnly = true,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        var add = new Button { Name = "AddCredentialButton", Text = "新增...", AutoSize = true, MinimumSize = new Size(90, 34) };
        var edit = new Button { Name = "EditCredentialButton", Text = "编辑...", AutoSize = true, MinimumSize = new Size(90, 34) };
        var delete = new Button { Name = "DeleteCredentialButton", Text = "删除", AutoSize = true, MinimumSize = new Size(90, 34) };
        add.Click += (_, _) => AddCredential();
        edit.Click += (_, _) => EditCredential();
        delete.Click += (_, _) => DeleteCredential();
        _check.Click += async (_, _) =>
        {
            if (_checkCancellation is not null) { _checkCancellation.Cancel(); return; }
            await CheckSelectedPermissionsAsync();
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditCredential(); };
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        FormClosing += CredentialCenterFormClosing;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(add); buttons.Controls.Add(edit); buttons.Controls.Add(delete); buttons.Controls.Add(_check);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_save);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_dirtyStatus, 0, 0);
        footer.Controls.Add(actions, 1, 0);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "凭据由对象存储连接和 CDN 配置共同引用；秘密值写入受保护的统一配置，列表只显示非秘密指纹。", AutoSize = true, Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        _save.Click += (_, _) => ValidateAndAccept();
        AcceptButton = _save;
        CancelButton = _cancel;
        RefreshGrid(); UpdateButtons();
    }

    private void RefreshGrid(Guid? selected = null)
    {
        _grid.Rows.Clear(); _grid.Columns.Clear();
        _grid.Columns.Add("name", "名称"); _grid.Columns.Add("provider", "提供方"); _grid.Columns.Add("type", "凭据类型"); _grid.Columns.Add("identity", "安全标识");
        foreach (var credential in _credentials.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = _grid.Rows.Add(credential.Name, credential.Provider, credential.Kind, credential.Fingerprint);
            _grid.Rows[row].Tag = credential.Id;
            if (selected == credential.Id) _grid.Rows[row].Selected = true;
        }
        UpdateButtons();
    }

    private Guid? SelectedId => _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag is Guid id ? id : null;
    private void UpdateButtons()
    {
        var busy = _checkCancellation is not null;
        foreach (var name in new[] { "AddCredentialButton", "EditCredentialButton", "DeleteCredentialButton" })
            if (Controls.Find(name, true).FirstOrDefault() is Button button)
                button.Enabled = !busy && (name == "AddCredentialButton" || SelectedId is not null);
        _check.Text = busy ? "取消权限检查" : "检查关联权限";
        _check.Enabled = busy || (_storage is not null && SelectedId is not null);
        UpdateDirtyState();
    }

    private void AddCredential()
    {
        using var dialog = new CredentialEditorDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _credentials.Add(dialog.Credential);
        MarkDirty();
        RefreshGrid(dialog.Credential.Id);
    }

    private void EditCredential()
    {
        var credential = SelectedId is Guid id ? _credentials.FirstOrDefault(x => x.Id == id) : null;
        if (credential is null) return;
        using var dialog = new CredentialEditorDialog(credential);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _credentials[_credentials.IndexOf(credential)] = dialog.Credential;
        MarkDirty();
        RefreshGrid(dialog.Credential.Id);
    }

    private void DeleteCredential()
    {
        var credential = SelectedId is Guid id ? _credentials.FirstOrDefault(x => x.Id == id) : null;
        if (credential is null) return;
        var usedBy = _cdnConfiguration.Profiles.Where(x => x.ControlCredentialId == credential.Id).Select(x => "CDN 控制面：" + x.Name)
            .Concat(_storageProfiles.Where(x => x.CredentialId == credential.Id || x.AwsExternalIdCredentialId == credential.Id).Select(x => "对象存储：" + x.Name)).ToArray();
        if (usedBy.Length > 0) { MessageBox.Show(this, $"凭据“{credential.Name}”仍被以下配置引用：{string.Join("、", usedBy)}。请先修改这些配置。", "无法删除凭据", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (MessageBox.Show(this, $"确定删除凭据“{credential.Name}”吗？", "删除凭据", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _credentials.Remove(credential);
        MarkDirty();
        RefreshGrid();
    }

    private async Task CheckSelectedPermissionsAsync()
    {
        if (_storage is null || SelectedId is not Guid id) return;
        var credential = _credentials.FirstOrDefault(x => x.Id == id); if (credential is null) return;
        using var cancellation = new CancellationTokenSource(); _checkCancellation = cancellation; UpdateButtons();
        try
        {
            var resolvedProfiles = new ExplorerConfiguration(
                    new ConnectionProfileConfiguration(_storageProfiles, _connectionGroups),
                    _cdnConfiguration,
                    _credentials)
                .ResolveCredentialReferences()
                .Storage.Profiles;
            var report = await new CredentialPermissionCoordinator(_storage).CheckAsync(
                credential,
                resolvedProfiles,
                _cdnConfiguration,
                cancellation.Token);
            if (_persistCheck is not null)
            {
                try
                {
                    await _persistCheck(credential, report, cancellation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    MessageBox.Show(
                        this,
                        $"权限检查已完成，但最近结果保存失败：{SensitiveDataRedactor.Redact(exception.Message)}",
                        "无法保存权限检查结果",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            if (!IsDisposed && !Disposing) using (var dialog = new CredentialPermissionResultDialog(credential, report)) dialog.ShowDialog(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ErrorDialog.ShowException(this, "凭据权限检查失败", "检查过程不会显示或记录秘密值。", exception); }
        finally { if (ReferenceEquals(_checkCancellation, cancellation)) _checkCancellation = null; if (!IsDisposed && !Disposing) UpdateButtons(); }
    }

    private void MarkDirty()
    {
        _isDirty = true;
        UpdateDirtyState();
    }

    private void UpdateDirtyState()
    {
        _save.Enabled = _isDirty && _checkCancellation is null;
        _dirtyStatus.Text = _isDirty ? "有未保存的凭据更改" : "没有未保存的更改";
        _dirtyStatus.ForeColor = _isDirty ? Color.DarkOrange : SystemColors.GrayText;
    }

    private void ValidateAndAccept()
    {
        try
        {
            var credentials = _credentials.ToArray();
            new ExplorerConfiguration(
                new ConnectionProfileConfiguration(_storageProfiles, _connectionGroups),
                _cdnConfiguration,
                credentials).Validate();
            Credentials = credentials;
            _accepting = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "凭据校验失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CredentialCenterFormClosing(object? sender, FormClosingEventArgs args)
    {
        _checkCancellation?.Cancel();
        if (_accepting || !_isDirty) return;
        if (MessageBox.Show(
                this,
                "当前有未保存的凭据更改。确定放弃这些更改吗？",
                "放弃未保存的更改",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            args.Cancel = true;
    }
}
