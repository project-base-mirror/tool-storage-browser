using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class CdnConfigurationDialog : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly IReadOnlyList<ConnectionProfile> _storageProfiles;
    private readonly ICdnCertificateInspector? _certificateInspector;
    private readonly IS3StorageService? _storage;
    private readonly ICdnDeliveryService? _deliveryService;
    private readonly Func<Guid, CdnCertificateCheckResult, CancellationToken, Task>? _persistCertificateResult;
    private readonly List<CdnProfile> _profiles;
    private readonly List<CdnCredential> _credentials;
    private readonly List<CdnBinding> _bindings;
    private readonly DataGridView _profileGrid;
    private readonly DataGridView _credentialGrid;
    private readonly DataGridView _bindingGrid;
    private readonly Dictionary<Guid, string> _certificateStatuses = [];
    private readonly Dictionary<Guid, string> _bindingStatuses = [];
    private readonly Button _checkCertificate = new()
    {
        Name = "CheckCdnCertificateButton",
        Text = "检测 HTTPS 证书",
        AutoSize = true,
        MinimumSize = new Size(140, 34)
    };
    private readonly Button _copyProfile = new()
    {
        Name = "CopyCdnProfileButton",
        Text = "复制...",
        AutoSize = true,
        MinimumSize = new Size(90, 34)
    };
    private readonly Button _checkBindings = new()
    {
        Name = "CheckCdnBindingsButton",
        Text = "检测关联",
        AutoSize = true,
        MinimumSize = new Size(108, 34)
    };
    private readonly Button _copyBinding = new()
    {
        Name = "CopyCdnBindingButton",
        Text = "复制...",
        AutoSize = true,
        MinimumSize = new Size(90, 34)
    };
    private CancellationTokenSource? _certificateCancellation;
    private CancellationTokenSource? _bindingCancellation;
    private readonly Button _save = new()
    {
        Name = "SaveCdnConfigurationButton",
        Text = "保存全部更改",
        AutoSize = true,
        MinimumSize = new Size(136, 36),
        Enabled = false
    };
    private readonly Button _cancel = new()
    {
        Name = "CancelCdnConfigurationButton",
        Text = "取消",
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
        MinimumSize = new Size(96, 36)
    };
    private readonly Label _dirtyStatus = new()
    {
        Name = "CdnConfigurationDirtyStatus",
        Text = "没有未保存的更改",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Anchor = AnchorStyles.Left
    };
    private bool _isDirty;
    private bool _accepting;

    public CdnConfiguration Configuration { get; private set; }
    public IReadOnlyList<CdnCredential> Credentials { get; private set; }

    public CdnConfigurationDialog(
        IReadOnlyList<ConnectionProfile> storageProfiles,
        CdnConfiguration configuration,
        IReadOnlyList<CdnCredential> credentials,
        ConnectionProfile? initialProfile = null,
        string? initialBucket = null,
        ICdnCertificateInspector? certificateInspector = null,
        IS3StorageService? storage = null,
        ICdnDeliveryService? deliveryService = null,
        Func<Guid, CdnCertificateCheckResult, CancellationToken, Task>? persistCertificateResult = null)
    {
        _storageProfiles = storageProfiles;
        _certificateInspector = certificateInspector;
        _storage = storage;
        _deliveryService = deliveryService;
        _persistCertificateResult = persistCertificateResult;
        _profiles = [.. configuration.Profiles];
        _credentials = [.. credentials];
        _bindings = [.. configuration.Bindings];
        Configuration = configuration;
        Credentials = credentials;

        Name = "CdnConfigurationDialog";
        Text = "CDN 配置中心";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 680);
        MinimumSize = new Size(820, 560);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        var tabs = new TabControl
        {
            Name = "CdnConfigurationTabs",
            Dock = DockStyle.Fill
        };
        var profileTab = CreateTab(
            "CDN 配置",
            "CdnProfilesTab",
            "AddCdnProfileButton",
            "EditCdnProfileButton",
            "DeleteCdnProfileButton",
            AddProfile,
            EditProfile,
            DeleteProfile);
        _profileGrid = profileTab.Grid;
        _profileGrid.MultiSelect = true;
        profileTab.Buttons.Controls.Add(_copyProfile);
        profileTab.Buttons.Controls.SetChildIndex(_copyProfile, 2);
        profileTab.Buttons.Controls.Add(_checkCertificate);
        tabs.TabPages.Add(profileTab.Page);

        var credentialTab = CreateTab(
            "独立凭据",
            "CdnCredentialsTab",
            "AddCdnCredentialButton",
            "EditCdnCredentialButton",
            "DeleteCdnCredentialButton",
            AddCredential,
            EditCredential,
            DeleteCredential);
        _credentialGrid = credentialTab.Grid;
        tabs.TabPages.Add(credentialTab.Page);

        var bindingTab = CreateTab(
            "Bucket / 前缀关联",
            "CdnBindingsTab",
            "AddCdnBindingButton",
            "EditCdnBindingButton",
            "DeleteCdnBindingButton",
            () => AddBinding(initialProfile, initialBucket),
            EditBinding,
            DeleteBinding);
        _bindingGrid = bindingTab.Grid;
        _bindingGrid.MultiSelect = true;
        bindingTab.Buttons.Controls.Add(_copyBinding);
        bindingTab.Buttons.Controls.SetChildIndex(_copyBinding, 2);
        bindingTab.Buttons.Controls.Add(_checkBindings);
        tabs.TabPages.Add(bindingTab.Page);

        var root = new TableLayoutPanel
        {
            Name = "CdnConfigurationLayout",
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = "CDN 是对象存储的独立交付层。配置分发域名和 HTTP 行为，再按对象存储连接、Bucket 与最长前缀建立关联。",
            AutoSize = true,
            MaximumSize = new Size(920, 0),
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        root.Controls.Add(tabs, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_save);
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_dirtyStatus, 0, 0);
        footer.Controls.Add(actions, 1, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        _save.Click += (_, _) => ValidateAndAccept();
        _checkCertificate.Click += async (_, _) =>
        {
            if (_certificateCancellation is not null)
            {
                _certificateCancellation.Cancel();
                return;
            }
            await CheckSelectedCertificateAsync();
        };
        _copyProfile.Click += (_, _) => CopyProfile();
        _copyBinding.Click += (_, _) => CopyBinding();
        _checkBindings.Click += async (_, _) =>
        {
            if (_bindingCancellation is not null)
            {
                _bindingCancellation.Cancel();
                return;
            }
            await CheckSelectedBindingsAsync();
        };
        _profileGrid.SelectionChanged += (_, _) =>
        {
            UpdateOperationButtons();
        };
        _credentialGrid.SelectionChanged += (_, _) =>
        {
            UpdateOperationButtons();
        };
        _bindingGrid.SelectionChanged += (_, _) =>
        {
            UpdateOperationButtons();
        };
        FormClosing += ConfigurationFormClosing;
        AcceptButton = _save;
        CancelButton = _cancel;
        RefreshAll();
        UpdateDirtyState();
    }

    private static (TabPage Page, DataGridView Grid, FlowLayoutPanel Buttons) CreateTab(
        string text,
        string pageName,
        string addName,
        string editName,
        string deleteName,
        Action add,
        Action edit,
        Action delete)
    {
        var page = new TabPage(text)
        {
            Name = pageName,
            Padding = new Padding(8)
        };
        var grid = new DataGridView
        {
            Name = pageName + "Grid",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        var addButton = new Button
        {
            Name = addName,
            Text = "新增...",
            AutoSize = true,
            MinimumSize = new Size(90, 34)
        };
        var editButton = new Button
        {
            Name = editName,
            Text = "编辑...",
            AutoSize = true,
            MinimumSize = new Size(90, 34)
        };
        var deleteButton = new Button
        {
            Name = deleteName,
            Text = "删除",
            AutoSize = true,
            MinimumSize = new Size(90, 34)
        };
        addButton.Click += (_, _) => add();
        editButton.Click += (_, _) => edit();
        deleteButton.Click += (_, _) => delete();
        grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex >= 0) edit();
        };
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(editButton);
        buttons.Controls.Add(deleteButton);
        page.Controls.Add(grid);
        page.Controls.Add(buttons);
        return (page, grid, buttons);
    }

    private void RefreshAll(
        IReadOnlyCollection<Guid>? selectedProfiles = null,
        IReadOnlyCollection<Guid>? selectedCredentials = null,
        IReadOnlyCollection<Guid>? selectedBindings = null)
    {
        selectedProfiles ??= SelectedIds(_profileGrid);
        selectedCredentials ??= SelectedIds(_credentialGrid);
        selectedBindings ??= SelectedIds(_bindingGrid);
        RefreshProfiles(selectedProfiles);
        RefreshCredentials(selectedCredentials);
        RefreshBindings(selectedBindings);
    }

    private void RefreshProfiles(IReadOnlyCollection<Guid> selectedIds)
    {
        _profileGrid.Columns.Clear();
        _profileGrid.Columns.Add("name", "名称");
        _profileGrid.Columns.Add("base", "基础 URL");
        _profileGrid.Columns.Add("certificate", "HTTPS 证书");
        _profileGrid.Columns.Add("notes", "备注");
        _profileGrid.Columns.Add("warmup", "预热");
        _profileGrid.Columns.Add("purge", "刷新");
        _profileGrid.Columns.Add("credential", "凭据");
        foreach (var profile in _profiles.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            var credentialName = profile.CredentialId is Guid credentialId
                ? _credentials.FirstOrDefault(value => value.Id == credentialId)?.Name ?? "缺失"
                : "无";
            var index = _profileGrid.Rows.Add(
                profile.Name,
                profile.BaseUrl,
                CertificateStatus(profile),
                NotesPreview(profile.Notes),
                WarmupText(profile),
                profile.Capabilities.HasFlag(CdnCapabilities.Purge) ? profile.PurgeHttpMethod : "未配置",
                credentialName);
            _profileGrid.Rows[index].Tag = profile.Id;
            if (!string.IsNullOrWhiteSpace(profile.Notes))
                _profileGrid.Rows[index].Cells["notes"].ToolTipText = profile.Notes;
        }
        RestoreSelection(_profileGrid, selectedIds);
        UpdateOperationButtons();
    }

    private void RefreshCredentials(IReadOnlyCollection<Guid> selectedIds)
    {
        _credentialGrid.Columns.Clear();
        _credentialGrid.Columns.Add("name", "名称");
        _credentialGrid.Columns.Add("type", "认证类型");
        _credentialGrid.Columns.Add("header", "Header");
        foreach (var credential in _credentials.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
        {
            var index = _credentialGrid.Rows.Add(
                credential.Name,
                AuthenticationText(credential.AuthenticationType),
                credential.AuthenticationType == CdnAuthenticationType.CustomHeader
                    ? credential.HeaderName
                    : "-");
            _credentialGrid.Rows[index].Tag = credential.Id;
        }
        RestoreSelection(_credentialGrid, selectedIds);
    }

    private void RefreshBindings(IReadOnlyCollection<Guid> selectedIds)
    {
        _bindingGrid.Columns.Clear();
        _bindingGrid.Columns.Add("storage", "对象存储连接");
        _bindingGrid.Columns.Add("bucket", "Bucket");
        _bindingGrid.Columns.Add("source", "源前缀");
        _bindingGrid.Columns.Add("cdn", "CDN");
        _bindingGrid.Columns.Add("target", "CDN 路径前缀");
        _bindingGrid.Columns.Add("newObject", "新对象");
        _bindingGrid.Columns.Add("overwrite", "覆盖对象");
        _bindingGrid.Columns.Add("default", "默认");
        _bindingGrid.Columns.Add("check", "检测状态");
        foreach (var binding in _bindings
            .OrderBy(value => StorageName(value.StorageProfileId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Bucket, StringComparer.Ordinal)
            .ThenBy(value => value.SourcePrefix, StringComparer.Ordinal))
        {
            var index = _bindingGrid.Rows.Add(
                StorageName(binding.StorageProfileId),
                binding.Bucket,
                CdnUrlMapper.NormalizePrefix(binding.SourcePrefix),
                _profiles.FirstOrDefault(value => value.Id == binding.CdnProfileId)?.Name ?? "缺失",
                CdnUrlMapper.NormalizePrefix(binding.CdnPathPrefix),
                UploadActionText(binding.NewObjectAction),
                UploadActionText(binding.OverwriteAction),
                binding.IsDefault ? "是" : "否",
                _bindingStatuses.GetValueOrDefault(binding.Id, "尚未检测"));
            _bindingGrid.Rows[index].Tag = binding.Id;
        }
        RestoreSelection(_bindingGrid, selectedIds);
        UpdateOperationButtons();
    }

    private static string UploadActionText(CdnUploadAction action) => action switch
    {
        CdnUploadAction.None => "不处理",
        CdnUploadAction.Warmup => "预热",
        CdnUploadAction.Purge => "刷新",
        CdnUploadAction.PurgeThenWarmup => "刷新后预热",
        _ => "未知"
    };

    private void AddProfile()
    {
        using var dialog = new CdnProfileEditorDialog(null, _credentials);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _profiles.Add(dialog.Profile);
        MarkDirty();
        RefreshAll(selectedProfiles: [dialog.Profile.Id]);
    }

    private void EditProfile()
    {
        var id = SelectedId(_profileGrid);
        var profile = id is Guid value ? _profiles.FirstOrDefault(item => item.Id == value) : null;
        if (profile is null) return;
        using var dialog = new CdnProfileEditorDialog(profile, _credentials);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _profiles[_profiles.IndexOf(profile)] = dialog.Profile;
        _certificateStatuses.Remove(profile.Id);
        MarkDirty();
        RefreshAll(selectedProfiles: [dialog.Profile.Id]);
    }

    private void CopyProfile()
    {
        var id = SelectedId(_profileGrid);
        var profile = id is Guid value ? _profiles.FirstOrDefault(item => item.Id == value) : null;
        if (profile is null) return;
        var copy = profile with
        {
            Id = Guid.NewGuid(),
            Name = UniqueCopyName(profile.Name, _profiles.Select(item => item.Name)),
            LastCertificateCheck = null
        };
        using var dialog = new CdnProfileEditorDialog(copy, _credentials, copying: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _profiles.Add(dialog.Profile);
        MarkDirty();
        RefreshAll(selectedProfiles: [dialog.Profile.Id]);
    }

    private void DeleteProfile()
    {
        var selectedIds = SelectedIds(_profileGrid);
        var profiles = _profiles.Where(item => selectedIds.Contains(item.Id)).ToArray();
        if (profiles.Length == 0) return;
        var profileIds = profiles.Select(item => item.Id).ToHashSet();
        var affected = _bindings.Count(value => profileIds.Contains(value.CdnProfileId));
        var suffix = affected == 0 ? string.Empty : $"，并删除 {affected} 条 Bucket/前缀关联";
        if (MessageBox.Show(
                this,
                $"确定删除选中的 {profiles.Length:N0} 个 CDN 配置{suffix}吗？\n\n删除将在点击“保存全部更改”后写入磁盘。",
                "删除 CDN 配置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _profiles.RemoveAll(item => profileIds.Contains(item.Id));
        foreach (var profileId in profileIds) _certificateStatuses.Remove(profileId);
        var removedBindings = _bindings.Where(value => profileIds.Contains(value.CdnProfileId)).Select(value => value.Id).ToArray();
        _bindings.RemoveAll(value => profileIds.Contains(value.CdnProfileId));
        foreach (var bindingId in removedBindings) _bindingStatuses.Remove(bindingId);
        MarkDirty();
        RefreshAll();
    }

    private void AddCredential()
    {
        using var dialog = new CdnCredentialEditorDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _credentials.Add(dialog.Credential);
        MarkDirty();
        RefreshAll(selectedCredentials: [dialog.Credential.Id]);
    }

    private void EditCredential()
    {
        var id = SelectedId(_credentialGrid);
        var credential = id is Guid value ? _credentials.FirstOrDefault(item => item.Id == value) : null;
        if (credential is null) return;
        using var dialog = new CdnCredentialEditorDialog(credential);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _credentials[_credentials.IndexOf(credential)] = dialog.Credential;
        MarkDirty();
        RefreshAll(selectedCredentials: [dialog.Credential.Id]);
    }

    private void DeleteCredential()
    {
        var id = SelectedId(_credentialGrid);
        var credential = id is Guid value ? _credentials.FirstOrDefault(item => item.Id == value) : null;
        if (credential is null) return;
        var usedBy = _profiles.Where(value => value.CredentialId == credential.Id).Select(value => value.Name).ToArray();
        if (usedBy.Length > 0)
        {
            MessageBox.Show(
                this,
                $"凭据“{credential.Name}”仍被以下 CDN 配置引用：{string.Join("、", usedBy)}。请先修改这些配置。",
                "无法删除凭据",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(
                this,
                $"确定删除独立凭据“{credential.Name}”吗？",
                "删除 CDN 凭据",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _credentials.Remove(credential);
        MarkDirty();
        RefreshAll();
    }

    private void AddBinding(ConnectionProfile? initialProfile = null, string? initialBucket = null)
    {
        if (_storageProfiles.Count == 0 || _profiles.Count == 0)
        {
            MessageBox.Show(
                this,
                "建立关联前至少需要一个对象存储连接和一个 CDN 配置。",
                "无法新增关联",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        using var dialog = new CdnBindingEditorDialog(
            null,
            _storageProfiles,
            _profiles,
            initialProfile,
            initialBucket);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _bindings.Add(dialog.Binding);
        MarkDirty();
        RefreshAll(selectedBindings: [dialog.Binding.Id]);
    }

    private void EditBinding()
    {
        var id = SelectedId(_bindingGrid);
        var binding = id is Guid value ? _bindings.FirstOrDefault(item => item.Id == value) : null;
        if (binding is null) return;
        using var dialog = new CdnBindingEditorDialog(binding, _storageProfiles, _profiles, null, null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _bindings[_bindings.IndexOf(binding)] = dialog.Binding;
        _bindingStatuses.Remove(binding.Id);
        MarkDirty();
        RefreshAll(selectedBindings: [dialog.Binding.Id]);
    }

    private void CopyBinding()
    {
        var id = SelectedId(_bindingGrid);
        var binding = id is Guid value ? _bindings.FirstOrDefault(item => item.Id == value) : null;
        if (binding is null) return;
        var copy = binding with { Id = Guid.NewGuid(), IsDefault = false };
        using var dialog = new CdnBindingEditorDialog(
            copy, _storageProfiles, _profiles, null, null, copying: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var candidate = new CdnConfiguration([.. _profiles], [.. _bindings, dialog.Binding]);
        var errors = CdnConfigurationValidator.Validate(candidate, _credentials);
        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                "复制后的关联仍与现有配置冲突，请修改对象存储连接、Bucket、源前缀或 CDN 后再复制：\n\n" +
                string.Join(Environment.NewLine, errors),
                "无法复制关联", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _bindings.Add(dialog.Binding);
        MarkDirty();
        RefreshAll(selectedBindings: [dialog.Binding.Id]);
    }

    private void DeleteBinding()
    {
        var selectedIds = SelectedIds(_bindingGrid);
        var bindings = _bindings.Where(item => selectedIds.Contains(item.Id)).ToArray();
        if (bindings.Length == 0) return;
        if (MessageBox.Show(
                this,
                $"确定删除选中的 {bindings.Length:N0} 条 CDN 关联吗？\n\n删除将在点击“保存全部更改”后写入磁盘。",
                "删除 CDN 关联",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _bindings.RemoveAll(item => selectedIds.Contains(item.Id));
        foreach (var bindingId in selectedIds) _bindingStatuses.Remove(bindingId);
        MarkDirty();
        RefreshAll();
    }

    private void ValidateAndAccept()
    {
        try
        {
            var configuration = new CdnConfiguration([.. _profiles], [.. _bindings]);
            CdnConfigurationValidator.EnsureValid(configuration, _credentials);
            Configuration = configuration;
            Credentials = [.. _credentials];
            _accepting = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "CDN 配置校验失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private string StorageName(Guid id) =>
        _storageProfiles.FirstOrDefault(value => value.Id == id)?.Name ?? "缺失";

    private static Guid? SelectedId(DataGridView grid) =>
        grid.SelectedRows.Count == 1 && grid.SelectedRows[0].Tag is Guid id ? id : null;

    private static Guid[] SelectedIds(DataGridView grid) =>
        grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.Tag)
            .OfType<Guid>()
            .Distinct()
            .ToArray();

    private static void RestoreSelection(DataGridView grid, IReadOnlyCollection<Guid> selectedIds)
    {
        if (grid.Rows.Count == 0) return;
        grid.ClearSelection();
        foreach (DataGridViewRow row in grid.Rows)
            row.Selected = row.Tag is Guid id && selectedIds.Contains(id);
        if (grid.SelectedRows.Count == 0)
            grid.Rows[0].Selected = true;
    }

    private static string UniqueCopyName(string original, IEnumerable<string> existingNames)
    {
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = original + " - 副本";
        if (!names.Contains(candidate)) return candidate;
        for (var index = 2; ; index++)
        {
            candidate = $"{original} - 副本 {index}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private void MarkDirty()
    {
        _isDirty = true;
        UpdateDirtyState();
    }

    private void UpdateDirtyState()
    {
        _save.Enabled = _isDirty && _certificateCancellation is null && _bindingCancellation is null;
        _dirtyStatus.Text = _isDirty
            ? "有未保存的更改：新增、编辑、复制和删除将在此统一写入磁盘。"
            : "没有未保存的更改";
        _dirtyStatus.ForeColor = _isDirty ? Color.DarkOrange : SystemColors.GrayText;
    }

    private void ConfigurationFormClosing(object? sender, FormClosingEventArgs args)
    {
        _certificateCancellation?.Cancel();
        _bindingCancellation?.Cancel();
        if (_accepting || !_isDirty) return;
        using var confirmation = new DiscardCdnChangesDialog();
        if (confirmation.ShowDialog(this) != DialogResult.Yes)
            args.Cancel = true;
    }

    private static string WarmupText(CdnProfile profile) => profile.WarmupMode switch
    {
        CdnWarmupMode.Head => "HEAD",
        CdnWarmupMode.FullGet => "完整 GET",
        _ => $"Range GET ({profile.WarmupRangeBytes / 1024d / 1024d:N0} MiB)"
    };

    private static string NotesPreview(string notes)
    {
        var value = notes.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 80 ? value : value[..77] + "...";
    }

    private string CertificateStatus(CdnProfile profile)
    {
        if (_certificateStatuses.TryGetValue(profile.Id, out var status)) return status;
        if (profile.LastCertificateCheck is { } result)
            return $"{result.StatusText} · {result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        return Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) &&
               string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? "尚未检测"
            : "非 HTTPS";
    }

    private void UpdateOperationButtons()
    {
        var busy = _certificateCancellation is not null || _bindingCancellation is not null;
        SetButtonEnabled("AddCdnProfileButton", !busy);
        SetButtonEnabled("EditCdnProfileButton", !busy && SelectedId(_profileGrid) is not null);
        SetButtonEnabled("DeleteCdnProfileButton", !busy && SelectedIds(_profileGrid).Length > 0);
        SetButtonEnabled("AddCdnCredentialButton", !busy);
        SetButtonEnabled("EditCdnCredentialButton", !busy && SelectedId(_credentialGrid) is not null);
        SetButtonEnabled("DeleteCdnCredentialButton", !busy && SelectedIds(_credentialGrid).Length > 0);
        SetButtonEnabled("AddCdnBindingButton", !busy);
        SetButtonEnabled("EditCdnBindingButton", !busy && SelectedId(_bindingGrid) is not null);
        SetButtonEnabled("DeleteCdnBindingButton", !busy && SelectedIds(_bindingGrid).Length > 0);

        if (_certificateCancellation is not null)
        {
            _checkCertificate.Text = "取消证书检测";
            _checkCertificate.Enabled = true;
        }
        else
        {
            _checkCertificate.Text = "检测 HTTPS 证书";
            var selected = SelectedIds(_profileGrid).ToHashSet();
            _checkCertificate.Enabled = _bindingCancellation is null &&
                _certificateInspector is not null &&
                _profiles.Any(profile => selected.Contains(profile.Id) && IsHttps(profile.BaseUrl));
        }

        if (_bindingCancellation is not null)
        {
            _checkBindings.Text = "取消关联检测";
            _checkBindings.Enabled = true;
        }
        else
        {
            _checkBindings.Text = "检测关联";
            _checkBindings.Enabled = _certificateCancellation is null &&
                _storage is not null && _deliveryService is not null &&
                SelectedIds(_bindingGrid).Length > 0;
        }
        _copyProfile.Enabled = _certificateCancellation is null && _bindingCancellation is null && SelectedId(_profileGrid) is not null;
        _copyBinding.Enabled = _certificateCancellation is null && _bindingCancellation is null && SelectedId(_bindingGrid) is not null;
        UpdateDirtyState();
    }

    private void SetButtonEnabled(string name, bool enabled)
    {
        if (Controls.Find(name, searchAllChildren: true).FirstOrDefault() is Button button)
            button.Enabled = enabled;
    }

    private void SetCertificateStatus(Guid profileId, string status)
    {
        _certificateStatuses[profileId] = status;
        foreach (DataGridViewRow row in _profileGrid.Rows)
        {
            if (row.Tag is Guid id && id == profileId)
            {
                row.Cells["certificate"].Value = status;
                row.Cells["certificate"].ToolTipText = status;
                break;
            }
        }
    }

    private async Task CheckSelectedCertificateAsync()
    {
        if (_certificateInspector is null) return;
        var selectedIds = SelectedIds(_profileGrid).ToHashSet();
        var profiles = _profiles
            .Where(profile => selectedIds.Contains(profile.Id) && IsHttps(profile.BaseUrl))
            .ToArray();
        if (profiles.Length == 0) return;

        using var manualCancellation = new CancellationTokenSource();
        _certificateCancellation = manualCancellation;
        var completed = 0;
        var failed = 0;
        CdnCertificateCheckResult? singleResult = null;
        UpdateOperationButtons();

        try
        {
            for (var index = 0; index < profiles.Length; index++)
            {
                manualCancellation.Token.ThrowIfCancellationRequested();
                var profile = profiles[index];
                SetCertificateStatus(profile.Id, $"检测中 {index + 1}/{profiles.Length}...");
                var timeoutSeconds = Math.Clamp(profile.TimeoutSeconds, 1, 30);
                using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    manualCancellation.Token, timeoutCancellation.Token);
                try
                {
                    var result = await _certificateInspector.InspectAsync(profile, linkedCancellation.Token);
                    if (IsDisposed || Disposing) return;
                    var profileIndex = _profiles.FindIndex(item => item.Id == profile.Id);
                    if (profileIndex < 0) continue;
                    if (_persistCertificateResult is not null)
                        await _persistCertificateResult(profile.Id, result, manualCancellation.Token);
                    _profiles[profileIndex] = _profiles[profileIndex] with { LastCertificateCheck = result };
                    _certificateStatuses.Remove(profile.Id);
                    SetCertificateStatus(profile.Id, CertificateStatus(_profiles[profileIndex]));
                    completed++;
                    singleResult = result;
                }
                catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !manualCancellation.IsCancellationRequested)
                {
                    SetCertificateStatus(profile.Id, $"检测超时（{timeoutSeconds} 秒）");
                    failed++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    SetCertificateStatus(profile.Id, "检测失败：" + SensitiveDataRedactor.Redact(exception.Message));
                    failed++;
                }
            }
            if (profiles.Length == 1 && singleResult is not null)
            {
                using var dialog = new CdnCertificateResultDialog(singleResult);
                dialog.ShowDialog(this);
            }
            else if (!IsDisposed && !Disposing)
            {
                MessageBox.Show(this,
                    $"HTTPS 证书批量检测完成：成功 {completed:N0}，失败 {failed:N0}。\n\n成功结果已直接保存。",
                    "证书检测完成", MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var profile in profiles.Where(item =>
                         _certificateStatuses.GetValueOrDefault(item.Id)?.StartsWith("检测中", StringComparison.Ordinal) == true))
                SetCertificateStatus(profile.Id, "检测已取消");
        }
        finally
        {
            if (ReferenceEquals(_certificateCancellation, manualCancellation))
                _certificateCancellation = null;
            if (!IsDisposed && !Disposing)
                UpdateOperationButtons();
        }
    }

    private async Task CheckSelectedBindingsAsync()
    {
        if (_storage is null || _deliveryService is null) return;
        var selectedIds = SelectedIds(_bindingGrid).ToHashSet();
        var bindings = _bindings.Where(binding => selectedIds.Contains(binding.Id)).ToArray();
        if (bindings.Length == 0) return;

        using var manualCancellation = new CancellationTokenSource();
        _bindingCancellation = manualCancellation;
        var succeeded = 0;
        var failed = 0;
        UpdateOperationButtons();
        try
        {
            for (var index = 0; index < bindings.Length; index++)
            {
                manualCancellation.Token.ThrowIfCancellationRequested();
                var binding = bindings[index];
                SetBindingStatus(binding.Id, $"检测中 {index + 1}/{bindings.Length}...");
                try
                {
                    var storageProfile = _storageProfiles.FirstOrDefault(item => item.Id == binding.StorageProfileId)
                        ?? throw new InvalidOperationException("对象存储连接不存在");
                    var cdnProfile = _profiles.FirstOrDefault(item => item.Id == binding.CdnProfileId)
                        ?? throw new InvalidOperationException("CDN 配置不存在");
                    var credential = cdnProfile.CredentialId is Guid credentialId
                        ? _credentials.FirstOrDefault(item => item.Id == credentialId)
                        : null;
                    if (cdnProfile.CredentialId is not null && credential is null)
                        throw new InvalidOperationException("CDN 凭据不存在");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(cdnProfile.TimeoutSeconds, 1, 3600)));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(manualCancellation.Token, timeout.Token);
                    var inspection = await CdnBindingInspector.InspectAsync(
                        cdnProfile,
                        binding,
                        (prefix, continuationToken, token) => _storage.ListObjectsAsync(
                            storageProfile, binding.Bucket, prefix, continuationToken, 100, token),
                        (url, token) => _deliveryService.ProbeHeadAsync(cdnProfile, credential, url, token),
                        linked.Token);
                    if (inspection is null)
                    {
                        SetBindingStatus(binding.Id, "源前缀没有可检测文件");
                        failed++;
                        continue;
                    }
                    var result = inspection.Probe;
                    SetBindingStatus(binding.Id,
                        $"HTTP {result.StatusCode}" + (string.IsNullOrWhiteSpace(result.CacheStatus) ? string.Empty : $" · {result.CacheStatus}"),
                        $"对象：{inspection.SourceObject.Key}{Environment.NewLine}URL：{inspection.Url.AbsoluteUri}");
                    if (result.Success) succeeded++; else failed++;
                }
                catch (OperationCanceledException) when (!manualCancellation.IsCancellationRequested)
                {
                    SetBindingStatus(binding.Id, "检测超时");
                    failed++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var safeMessage = SensitiveDataRedactor.Redact(exception.Message);
                    SetBindingStatus(binding.Id, safeMessage, safeMessage);
                    failed++;
                }
            }
            if (!IsDisposed && !Disposing)
                MessageBox.Show(this,
                    $"关联批量检测完成：成功 {succeeded:N0}，失败或无测试对象 {failed:N0}。\n\n检测会读取每个源前缀中的首个文件，并对映射后的 CDN URL 发送 HEAD 请求。",
                    "关联检测完成", MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            foreach (var binding in bindings.Where(item =>
                         _bindingStatuses.GetValueOrDefault(item.Id)?.StartsWith("检测中", StringComparison.Ordinal) == true))
                SetBindingStatus(binding.Id, "检测已取消");
        }
        finally
        {
            if (ReferenceEquals(_bindingCancellation, manualCancellation))
                _bindingCancellation = null;
            if (!IsDisposed && !Disposing)
                UpdateOperationButtons();
        }
    }

    private void SetBindingStatus(Guid bindingId, string status, string? details = null)
    {
        _bindingStatuses[bindingId] = status;
        foreach (DataGridViewRow row in _bindingGrid.Rows)
        {
            if (row.Tag is Guid id && id == bindingId)
            {
                row.Cells["check"].Value = status;
                row.Cells["check"].ToolTipText = details ?? status;
                break;
            }
        }
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var endpoint) &&
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string AuthenticationText(CdnAuthenticationType type) => type switch
    {
        CdnAuthenticationType.BearerToken => "Bearer Token",
        CdnAuthenticationType.CustomHeader => "自定义 Header",
        _ => "无"
    };
}
