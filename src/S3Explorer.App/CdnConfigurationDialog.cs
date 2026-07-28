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
    private readonly List<CdnProfile> _profiles;
    private readonly List<CdnCredential> _credentials;
    private readonly List<CdnBinding> _bindings;
    private readonly DataGridView _profileGrid;
    private readonly DataGridView _credentialGrid;
    private readonly DataGridView _bindingGrid;
    private readonly Dictionary<Guid, string> _certificateStatuses = [];
    private readonly Button _checkCertificate = new()
    {
        Name = "CheckCdnCertificateButton",
        Text = "检测 HTTPS 证书",
        AutoSize = true,
        MinimumSize = new Size(140, 34)
    };
    private CancellationTokenSource? _certificateCancellation;
    private readonly Button _save = new()
    {
        Name = "SaveCdnConfigurationButton",
        Text = "保存配置",
        AutoSize = true,
        MinimumSize = new Size(112, 36)
    };
    private readonly Button _cancel = new()
    {
        Name = "CancelCdnConfigurationButton",
        Text = "取消",
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
        MinimumSize = new Size(96, 36)
    };

    public CdnConfiguration Configuration { get; private set; }
    public IReadOnlyList<CdnCredential> Credentials { get; private set; }

    public CdnConfigurationDialog(
        IReadOnlyList<ConnectionProfile> storageProfiles,
        CdnConfiguration configuration,
        IReadOnlyList<CdnCredential> credentials,
        ConnectionProfile? initialProfile = null,
        string? initialBucket = null,
        ICdnCertificateInspector? certificateInspector = null)
    {
        _storageProfiles = storageProfiles;
        _certificateInspector = certificateInspector;
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
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_save);
        root.Controls.Add(actions, 0, 2);
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
        _profileGrid.SelectionChanged += (_, _) => UpdateCertificateButton();
        FormClosing += (_, _) => _certificateCancellation?.Cancel();
        AcceptButton = _save;
        CancelButton = _cancel;
        RefreshAll();
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

    private void RefreshAll()
    {
        RefreshProfiles();
        RefreshCredentials();
        RefreshBindings();
    }

    private void RefreshProfiles()
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
        SelectFirst(_profileGrid);
        UpdateCertificateButton();
    }

    private void RefreshCredentials()
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
        SelectFirst(_credentialGrid);
    }

    private void RefreshBindings()
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
                binding.IsDefault ? "是" : "否");
            _bindingGrid.Rows[index].Tag = binding.Id;
        }
        SelectFirst(_bindingGrid);
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
        RefreshAll();
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
        RefreshAll();
    }

    private void DeleteProfile()
    {
        var id = SelectedId(_profileGrid);
        var profile = id is Guid value ? _profiles.FirstOrDefault(item => item.Id == value) : null;
        if (profile is null) return;
        var affected = _bindings.Count(value => value.CdnProfileId == profile.Id);
        var suffix = affected == 0 ? string.Empty : $"，并删除 {affected} 条 Bucket/前缀关联";
        if (MessageBox.Show(
                this,
                $"确定删除 CDN 配置“{profile.Name}”{suffix}吗？",
                "删除 CDN 配置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _profiles.Remove(profile);
        _certificateStatuses.Remove(profile.Id);
        _bindings.RemoveAll(value => value.CdnProfileId == profile.Id);
        RefreshAll();
    }

    private void AddCredential()
    {
        using var dialog = new CdnCredentialEditorDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _credentials.Add(dialog.Credential);
        RefreshAll();
    }

    private void EditCredential()
    {
        var id = SelectedId(_credentialGrid);
        var credential = id is Guid value ? _credentials.FirstOrDefault(item => item.Id == value) : null;
        if (credential is null) return;
        using var dialog = new CdnCredentialEditorDialog(credential);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _credentials[_credentials.IndexOf(credential)] = dialog.Credential;
        RefreshAll();
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
        RefreshAll();
    }

    private void EditBinding()
    {
        var id = SelectedId(_bindingGrid);
        var binding = id is Guid value ? _bindings.FirstOrDefault(item => item.Id == value) : null;
        if (binding is null) return;
        using var dialog = new CdnBindingEditorDialog(binding, _storageProfiles, _profiles, null, null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _bindings[_bindings.IndexOf(binding)] = dialog.Binding;
        RefreshAll();
    }

    private void DeleteBinding()
    {
        var id = SelectedId(_bindingGrid);
        var binding = id is Guid value ? _bindings.FirstOrDefault(item => item.Id == value) : null;
        if (binding is null) return;
        if (MessageBox.Show(
                this,
                $"确定删除 {StorageName(binding.StorageProfileId)} / {binding.Bucket} / " +
                $"{CdnUrlMapper.NormalizePrefix(binding.SourcePrefix)} 的 CDN 关联吗？",
                "删除 CDN 关联",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _bindings.Remove(binding);
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

    private static void SelectFirst(DataGridView grid)
    {
        if (grid.Rows.Count == 0) return;
        grid.ClearSelection();
        grid.Rows[0].Selected = true;
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
        return Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) &&
               string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? "尚未检测"
            : "非 HTTPS";
    }

    private void UpdateCertificateButton()
    {
        if (_certificateCancellation is not null)
        {
            _checkCertificate.Text = "取消证书检测";
            _checkCertificate.Enabled = true;
            return;
        }

        _checkCertificate.Text = "检测 HTTPS 证书";
        var id = SelectedId(_profileGrid);
        var profile = id is Guid value ? _profiles.FirstOrDefault(item => item.Id == value) : null;
        _checkCertificate.Enabled = _certificateInspector is not null &&
            profile is not null &&
            Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) &&
            string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private void SetCertificateStatus(Guid profileId, string status)
    {
        _certificateStatuses[profileId] = status;
        foreach (DataGridViewRow row in _profileGrid.Rows)
        {
            if (row.Tag is Guid id && id == profileId)
            {
                row.Cells["certificate"].Value = status;
                break;
            }
        }
    }

    private async Task CheckSelectedCertificateAsync()
    {
        if (_certificateInspector is null) return;
        var id = SelectedId(_profileGrid);
        var profile = id is Guid value ? _profiles.FirstOrDefault(item => item.Id == value) : null;
        if (profile is null) return;

        var timeoutSeconds = Math.Clamp(profile.TimeoutSeconds, 1, 30);
        using var manualCancellation = new CancellationTokenSource();
        using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            manualCancellation.Token,
            timeoutCancellation.Token);
        _certificateCancellation = manualCancellation;
        SetCertificateStatus(profile.Id, "检测中...");
        UpdateCertificateButton();

        try
        {
            var result = await _certificateInspector.InspectAsync(profile, linkedCancellation.Token);
            if (IsDisposed || Disposing) return;
            SetCertificateStatus(profile.Id, result.StatusText);
            using var dialog = new CdnCertificateResultDialog(result);
            dialog.ShowDialog(this);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            if (!IsDisposed && !Disposing)
                SetCertificateStatus(profile.Id, $"检测超时（{timeoutSeconds} 秒）");
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
                SetCertificateStatus(profile.Id, "检测已取消");
        }
        catch (Exception exception)
        {
            if (IsDisposed || Disposing) return;
            SetCertificateStatus(profile.Id, "检测失败");
            MessageBox.Show(
                this,
                exception.Message,
                "HTTPS 证书检测失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            if (ReferenceEquals(_certificateCancellation, manualCancellation))
                _certificateCancellation = null;
            if (!IsDisposed && !Disposing)
                UpdateCertificateButton();
        }
    }

    private static string AuthenticationText(CdnAuthenticationType type) => type switch
    {
        CdnAuthenticationType.BearerToken => "Bearer Token",
        CdnAuthenticationType.CustomHeader => "自定义 Header",
        _ => "无"
    };
}

internal sealed class CdnCertificateResultDialog : Form
{
    public CdnCertificateResultDialog(CdnCertificateCheckResult result)
    {
        Name = "CdnCertificateResultDialog";
        Text = "HTTPS 证书检测结果";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 500);
        MinimumSize = new Size(620, 420);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Icon = UiIcons.CreateApplicationIcon();

        var status = new Label
        {
            Name = "CdnCertificateResultStatus",
            Text = result.StatusText,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            ForeColor = result.Problems == CdnCertificateProblems.None
                ? result.IsExpiringSoon ? Color.DarkOrange : Color.DarkGreen
                : Color.Firebrick,
            Margin = new Padding(0, 0, 0, 10)
        };
        var details = new TextBox
        {
            Name = "CdnCertificateResultDetails",
            Text = FormatDetails(result),
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = SystemColors.Window
        };
        var copy = new Button
        {
            Name = "CopyCdnCertificateResultButton",
            Text = "复制结果",
            AutoSize = true,
            MinimumSize = new Size(104, 36)
        };
        var close = new Button
        {
            Name = "CloseCdnCertificateResultButton",
            Text = "关闭",
            AutoSize = true,
            MinimumSize = new Size(96, 36),
            DialogResult = DialogResult.OK
        };
        copy.Click += (_, _) => Clipboard.SetText(details.Text);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        actions.Controls.Add(close);
        actions.Controls.Add(copy);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 3
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(status, 0, 0);
        root.Controls.Add(details, 0, 1);
        root.Controls.Add(actions, 0, 2);
        Controls.Add(root);
        AcceptButton = close;
        CancelButton = close;
    }

    private static string FormatDetails(CdnCertificateCheckResult result)
    {
        var problemText = result.Problems == CdnCertificateProblems.None
            ? "无"
            : string.Join("、", Enum.GetValues<CdnCertificateProblems>()
                .Where(value => value != CdnCertificateProblems.None && result.Problems.HasFlag(value))
                .Select(ProblemText));
        var lines = new List<string>
        {
            $"检测端点：{result.Endpoint.Scheme}://{result.Endpoint.Authority}",
            $"检测时间：{result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            $"生效时间：{result.NotBefore.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            $"到期时间：{result.NotAfter.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            $"剩余天数：{result.DaysRemaining}",
            $"TLS 协议：{result.TlsProtocol}",
            $"主题：{result.Subject}",
            $"颁发者：{result.Issuer}",
            $"SHA-256 指纹：{result.Sha256Fingerprint}",
            $"验证问题：{problemText}",
            "吊销状态：未检查（避免证书检测被外部 CRL/OCSP 服务长时间阻塞）"
        };
        if (result.ChainErrors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("证书链详情：");
            lines.AddRange(result.ChainErrors.Select(value => "- " + value));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string ProblemText(CdnCertificateProblems problem) => problem switch
    {
        CdnCertificateProblems.NotYetValid => "尚未生效",
        CdnCertificateProblems.Expired => "已过期",
        CdnCertificateProblems.NameMismatch => "域名不匹配",
        CdnCertificateProblems.UntrustedChain => "证书链不受信任",
        _ => problem.ToString()
    };
}

internal sealed class CdnProfileEditorDialog : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly Guid _id;
    private readonly TextBox _name = new() { Name = "CdnProfileName" };
    private readonly TextBox _baseUrl = new() { Name = "CdnProfileBaseUrl" };
    private readonly TextBox _notes = new()
    {
        Name = "CdnProfileNotes",
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        MaxLength = CdnProfile.MaximumNotesLength,
        MinimumSize = new Size(0, 72)
    };
    private readonly ComboBox _credential = new()
    {
        Name = "CdnProfileCredential",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox _warmupMode = new()
    {
        Name = "CdnWarmupMode",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly NumericUpDown _rangeMiB = new()
    {
        Name = "CdnWarmupRangeMiB",
        Minimum = 1,
        Maximum = 1024,
        Value = 1
    };
    private readonly NumericUpDown _timeout = new()
    {
        Name = "CdnTimeoutSeconds",
        Minimum = 1,
        Maximum = 3600,
        Value = 100
    };
    private readonly CheckBox _followRedirects = new()
    {
        Name = "CdnFollowRedirects",
        Text = "跟随 HTTP 重定向",
        Checked = true,
        AutoSize = true
    };
    private readonly CheckBox _enabled = new()
    {
        Name = "CdnProfileEnabled",
        Text = "启用此 CDN 配置",
        Checked = true,
        AutoSize = true
    };
    private readonly TextBox _purgeEndpoint = new() { Name = "CdnPurgeEndpoint" };
    private readonly ComboBox _purgeMethod = new()
    {
        Name = "CdnPurgeMethod",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _purgeBody = new()
    {
        Name = "CdnPurgeBody",
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        MinimumSize = new Size(0, 90)
    };
    private readonly TextBox _purgeContentType = new() { Name = "CdnPurgeContentType" };
    private readonly Button _save = EditorLayout.SaveButton("SaveCdnProfileButton");
    private readonly Button _cancel = EditorLayout.CancelButton("CancelCdnProfileButton");

    public CdnProfile Profile { get; private set; }

    public CdnProfileEditorDialog(CdnProfile? profile, IReadOnlyList<CdnCredential> credentials)
    {
        Profile = profile ?? new CdnProfile();
        _id = Profile.Id;
        Name = "CdnProfileEditorDialog";
        Text = profile is null ? "新增 CDN 配置" : "编辑 CDN 配置";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 680);
        MinimumSize = new Size(640, 580);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Icon = UiIcons.CreateApplicationIcon();

        _credential.Items.Add(new Choice<Guid?>(null, "(无独立凭据)"));
        foreach (var item in credentials.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            _credential.Items.Add(new Choice<Guid?>(item.Id, item.Name));
        _warmupMode.Items.AddRange([
            new Choice<CdnWarmupMode>(CdnWarmupMode.Head, "HEAD（轻量，但部分 CDN 与 GET 行为不同）"),
            new Choice<CdnWarmupMode>(CdnWarmupMode.RangeGet, "Range GET（推荐）"),
            new Choice<CdnWarmupMode>(CdnWarmupMode.FullGet, "完整 GET")
        ]);
        _purgeMethod.Items.AddRange(["GET", "POST", "PUT", "PATCH", "DELETE"]);

        var fields = EditorLayout.Fields();
        EditorLayout.AddField(fields, "名称：", _name);
        EditorLayout.AddField(fields, "CDN 基础 URL：", _baseUrl);
        EditorLayout.AddField(fields, "备注：", _notes);
        EditorLayout.AddField(fields, "独立凭据：", _credential);
        EditorLayout.AddField(fields, "预热模式：", _warmupMode);
        EditorLayout.AddField(fields, "Range 大小 (MiB)：", _rangeMiB);
        EditorLayout.AddField(fields, "请求超时 (秒)：", _timeout);
        EditorLayout.AddWide(fields, _followRedirects);
        EditorLayout.AddWide(fields, _enabled);
        EditorLayout.AddSection(fields, "通用 HTTP 刷新（可选）");
        EditorLayout.AddField(fields, "端点模板：", _purgeEndpoint);
        EditorLayout.AddWide(fields, new Label
        {
            Text = "支持 {url}（URL 编码后的完整 CDN URL）和 {path}（URL 编码后的路径）。留空表示不支持手动刷新。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(600, 0)
        });
        EditorLayout.AddField(fields, "HTTP 方法：", _purgeMethod);
        EditorLayout.AddField(fields, "Content-Type：", _purgeContentType);
        EditorLayout.AddField(fields, "Body 模板：", _purgeBody);
        EditorLayout.AddWide(fields, new Label
        {
            Text = "Body 中的 {url}/{path} 会按 JSON 字符串内容转义，不包含外围引号。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        });

        Controls.Add(EditorLayout.Root(fields, _save, _cancel));
        _save.Click += (_, _) => Save();
        _warmupMode.SelectedIndexChanged += (_, _) =>
            _rangeMiB.Enabled = Selected(_warmupMode, CdnWarmupMode.RangeGet) == CdnWarmupMode.RangeGet;
        AcceptButton = _save;
        CancelButton = _cancel;
        LoadProfile(Profile);
    }

    private void LoadProfile(CdnProfile profile)
    {
        _name.Text = profile.Name;
        _baseUrl.Text = profile.BaseUrl;
        _notes.Text = profile.Notes;
        SelectValue(_credential, profile.CredentialId);
        SelectValue(_warmupMode, profile.WarmupMode);
        _rangeMiB.Value = Math.Clamp(
            (decimal)Math.Ceiling(profile.WarmupRangeBytes / 1024d / 1024d),
            _rangeMiB.Minimum,
            _rangeMiB.Maximum);
        _timeout.Value = Math.Clamp(profile.TimeoutSeconds, (int)_timeout.Minimum, (int)_timeout.Maximum);
        _followRedirects.Checked = profile.FollowRedirects;
        _enabled.Checked = profile.Enabled;
        _purgeEndpoint.Text = profile.PurgeEndpointTemplate;
        _purgeMethod.SelectedItem = profile.PurgeHttpMethod;
        if (_purgeMethod.SelectedIndex < 0) _purgeMethod.SelectedItem = "POST";
        _purgeBody.Text = profile.PurgeBodyTemplate;
        _purgeContentType.Text = profile.PurgeContentType;
    }

    private void Save()
    {
        var candidate = new CdnProfile
        {
            Id = _id,
            Name = _name.Text.Trim(),
            Notes = _notes.Text.Trim(),
            ProviderId = CdnProfile.GenericHttpProviderId,
            BaseUrl = _baseUrl.Text.Trim(),
            CredentialId = Selected(_credential, (Guid?)null),
            WarmupMode = Selected(_warmupMode, CdnWarmupMode.RangeGet),
            WarmupRangeBytes = decimal.ToInt64(_rangeMiB.Value) * 1024L * 1024L,
            PurgeEndpointTemplate = _purgeEndpoint.Text.Trim(),
            PurgeHttpMethod = _purgeMethod.SelectedItem?.ToString() ?? "POST",
            PurgeBodyTemplate = _purgeBody.Text,
            PurgeContentType = string.IsNullOrWhiteSpace(_purgeContentType.Text)
                ? "application/json"
                : _purgeContentType.Text.Trim(),
            TimeoutSeconds = decimal.ToInt32(_timeout.Value),
            FollowRedirects = _followRedirects.Checked,
            Enabled = _enabled.Checked
        };
        var errors = CdnConfigurationValidator.Validate(new CdnConfiguration([candidate], []));
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "配置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Profile = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static T Selected<T>(ComboBox combo, T fallback) =>
        combo.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private static void SelectValue<T>(ComboBox combo, T value)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }
}

internal sealed class CdnCredentialEditorDialog : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly Guid _id;
    private readonly TextBox _name = new() { Name = "CdnCredentialName" };
    private readonly ComboBox _type = new()
    {
        Name = "CdnCredentialType",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _header = new() { Name = "CdnCredentialHeader" };
    private readonly TextBox _secret = new()
    {
        Name = "CdnCredentialSecret",
        UseSystemPasswordChar = true
    };
    private readonly CheckBox _showSecret = new()
    {
        Name = "ShowCdnCredentialSecret",
        Text = "显示秘密值",
        AutoSize = true
    };
    private readonly Button _save = EditorLayout.SaveButton("SaveCdnCredentialButton");
    private readonly Button _cancel = EditorLayout.CancelButton("CancelCdnCredentialButton");

    public CdnCredential Credential { get; private set; }

    public CdnCredentialEditorDialog(CdnCredential? credential)
    {
        Credential = credential ?? new CdnCredential();
        _id = Credential.Id;
        Name = "CdnCredentialEditorDialog";
        Text = credential is null ? "新增 CDN 独立凭据" : "编辑 CDN 独立凭据";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 380);
        MinimumSize = new Size(560, 340);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Icon = UiIcons.CreateApplicationIcon();

        _type.Items.AddRange([
            new Choice<CdnAuthenticationType>(CdnAuthenticationType.None, "无认证"),
            new Choice<CdnAuthenticationType>(CdnAuthenticationType.BearerToken, "Bearer Token"),
            new Choice<CdnAuthenticationType>(CdnAuthenticationType.CustomHeader, "自定义 Header")
        ]);

        var fields = EditorLayout.Fields();
        EditorLayout.AddField(fields, "名称：", _name);
        EditorLayout.AddField(fields, "认证类型：", _type);
        EditorLayout.AddField(fields, "Header 名称：", _header);
        EditorLayout.AddField(fields, "秘密值：", _secret);
        EditorLayout.AddWide(fields, _showSecret);
        EditorLayout.AddWide(fields, new Label
        {
            Text = "秘密值与 S3 Access Key/SecretKey 分开保存，并使用 Windows DPAPI CurrentUser 加密。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(520, 0)
        });
        Controls.Add(EditorLayout.Root(fields, _save, _cancel));

        _name.Text = Credential.Name;
        _header.Text = Credential.HeaderName;
        _secret.Text = Credential.Secret;
        SelectType(Credential.AuthenticationType);
        _type.SelectedIndexChanged += (_, _) => UpdateState();
        _showSecret.CheckedChanged += (_, _) => _secret.UseSystemPasswordChar = !_showSecret.Checked;
        _save.Click += (_, _) => Save();
        AcceptButton = _save;
        CancelButton = _cancel;
        UpdateState();
    }

    private void UpdateState()
    {
        var type = _type.SelectedItem is Choice<CdnAuthenticationType> choice
            ? choice.Value
            : CdnAuthenticationType.None;
        _header.Enabled = type == CdnAuthenticationType.CustomHeader;
        _secret.Enabled = type != CdnAuthenticationType.None;
    }

    private void Save()
    {
        var type = _type.SelectedItem is Choice<CdnAuthenticationType> choice
            ? choice.Value
            : CdnAuthenticationType.None;
        var candidate = new CdnCredential
        {
            Id = _id,
            Name = _name.Text.Trim(),
            AuthenticationType = type,
            HeaderName = type == CdnAuthenticationType.CustomHeader ? _header.Text.Trim() : string.Empty,
            Secret = type == CdnAuthenticationType.None ? string.Empty : _secret.Text
        };
        var errors = CdnConfigurationValidator.Validate(CdnConfiguration.Empty, [candidate]);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "凭据无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Credential = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SelectType(CdnAuthenticationType type)
    {
        for (var index = 0; index < _type.Items.Count; index++)
        {
            if (_type.Items[index] is Choice<CdnAuthenticationType> choice && choice.Value == type)
            {
                _type.SelectedIndex = index;
                return;
            }
        }
        _type.SelectedIndex = 0;
    }
}

internal sealed class CdnBindingEditorDialog : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly Guid _id;
    private readonly ComboBox _storage = new()
    {
        Name = "CdnBindingStorageProfile",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _bucket = new() { Name = "CdnBindingBucket" };
    private readonly TextBox _sourcePrefix = new() { Name = "CdnBindingSourcePrefix" };
    private readonly ComboBox _cdn = new()
    {
        Name = "CdnBindingProfile",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _targetPrefix = new() { Name = "CdnBindingTargetPrefix" };
    private readonly ComboBox _newObjectAction = new()
    {
        Name = "CdnBindingNewObjectAction",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox _overwriteAction = new()
    {
        Name = "CdnBindingOverwriteAction",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly CheckBox _default = new()
    {
        Name = "CdnBindingDefault",
        Text = "作为此匹配范围的默认 CDN",
        Checked = true,
        AutoSize = true
    };
    private readonly CheckBox _enabled = new()
    {
        Name = "CdnBindingEnabled",
        Text = "启用此关联",
        Checked = true,
        AutoSize = true
    };
    private readonly Button _save = EditorLayout.SaveButton("SaveCdnBindingButton");
    private readonly Button _cancel = EditorLayout.CancelButton("CancelCdnBindingButton");

    public CdnBinding Binding { get; private set; }

    public CdnBindingEditorDialog(
        CdnBinding? binding,
        IReadOnlyList<ConnectionProfile> storageProfiles,
        IReadOnlyList<CdnProfile> cdnProfiles,
        ConnectionProfile? initialProfile,
        string? initialBucket)
    {
        Binding = binding ?? new CdnBinding();
        _id = Binding.Id;
        Name = "CdnBindingEditorDialog";
        Text = binding is null ? "新增 CDN 关联" : "编辑 CDN 关联";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 500);
        MinimumSize = new Size(600, 450);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Icon = UiIcons.CreateApplicationIcon();

        foreach (var profile in storageProfiles.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            _storage.Items.Add(new Choice<Guid>(profile.Id, profile.Name));
        foreach (var profile in cdnProfiles.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            _cdn.Items.Add(new Choice<Guid>(profile.Id, profile.Name));
        _newObjectAction.Items.AddRange([
            new Choice<CdnUploadAction>(CdnUploadAction.None, "不自动处理"),
            new Choice<CdnUploadAction>(CdnUploadAction.Warmup, "HTTP 预热")
        ]);
        _overwriteAction.Items.AddRange([
            new Choice<CdnUploadAction>(CdnUploadAction.None, "不自动处理"),
            new Choice<CdnUploadAction>(CdnUploadAction.Purge, "刷新缓存"),
            new Choice<CdnUploadAction>(CdnUploadAction.PurgeThenWarmup, "刷新后预热")
        ]);

        var fields = EditorLayout.Fields();
        EditorLayout.AddField(fields, "对象存储连接：", _storage);
        EditorLayout.AddField(fields, "Bucket：", _bucket);
        EditorLayout.AddField(fields, "对象 Key 源前缀：", _sourcePrefix);
        EditorLayout.AddField(fields, "CDN 配置：", _cdn);
        EditorLayout.AddField(fields, "CDN 路径前缀：", _targetPrefix);
        EditorLayout.AddField(fields, "上传新对象后：", _newObjectAction);
        EditorLayout.AddField(fields, "覆盖对象后：", _overwriteAction);
        EditorLayout.AddWide(fields, _default);
        EditorLayout.AddWide(fields, _enabled);
        EditorLayout.AddWide(fields, new Label
        {
            Text = "解析时只使用最长的匹配源前缀；同一范围可关联多个 CDN，但只能有一个默认项。自动化默认关闭；覆盖刷新需要 CDN 配置提供刷新端点。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(550, 0)
        });
        Controls.Add(EditorLayout.Root(fields, _save, _cancel));
        _save.Click += (_, _) => Save();
        AcceptButton = _save;
        CancelButton = _cancel;

        var storageId = binding?.StorageProfileId ?? initialProfile?.Id ?? Guid.Empty;
        SelectValue(_storage, storageId);
        _bucket.Text = binding?.Bucket ?? initialBucket ?? string.Empty;
        _sourcePrefix.Text = binding?.SourcePrefix ?? string.Empty;
        SelectValue(_cdn, binding?.CdnProfileId ?? cdnProfiles.FirstOrDefault()?.Id ?? Guid.Empty);
        _targetPrefix.Text = binding?.CdnPathPrefix ?? string.Empty;
        SelectAction(_newObjectAction, binding?.NewObjectAction ?? CdnUploadAction.None);
        SelectAction(_overwriteAction, binding?.OverwriteAction ?? CdnUploadAction.None);
        _default.Checked = binding?.IsDefault ?? true;
        _enabled.Checked = binding?.Enabled ?? true;
    }

    private void Save()
    {
        var storageId = Selected(_storage);
        var cdnId = Selected(_cdn);
        if (storageId == Guid.Empty || cdnId == Guid.Empty || string.IsNullOrWhiteSpace(_bucket.Text))
        {
            MessageBox.Show(
                this,
                "必须选择对象存储连接、填写 Bucket 并选择 CDN 配置。",
                "关联无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        Binding = new CdnBinding
        {
            Id = _id,
            StorageProfileId = storageId,
            Bucket = _bucket.Text.Trim(),
            SourcePrefix = CdnUrlMapper.NormalizePrefix(_sourcePrefix.Text),
            CdnProfileId = cdnId,
            CdnPathPrefix = CdnUrlMapper.NormalizePrefix(_targetPrefix.Text),
            NewObjectAction = SelectedAction(_newObjectAction),
            OverwriteAction = SelectedAction(_overwriteAction),
            IsDefault = _default.Checked,
            Enabled = _enabled.Checked
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Guid Selected(ComboBox combo) =>
        combo.SelectedItem is Choice<Guid> choice ? choice.Value : Guid.Empty;

    private static CdnUploadAction SelectedAction(ComboBox combo) =>
        combo.SelectedItem is Choice<CdnUploadAction> choice ? choice.Value : CdnUploadAction.None;

    private static void SelectValue(ComboBox combo, Guid value)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is Choice<Guid> choice && choice.Value == value)
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }

    private static void SelectAction(ComboBox combo, CdnUploadAction value)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is Choice<CdnUploadAction> choice && choice.Value == value)
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }
}

internal static class EditorLayout
{
    public static TableLayoutPanel Fields()
    {
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(14)
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return fields;
    }

    public static void AddField(TableLayoutPanel fields, string label, Control control)
    {
        var row = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 8)
        }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 5, 0, 5);
        fields.Controls.Add(control, 1, row);
    }

    public static void AddWide(TableLayoutPanel fields, Control control)
    {
        var row = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, 5, 0, 5);
        fields.Controls.Add(control, 0, row);
        fields.SetColumnSpan(control, 2);
    }

    public static void AddSection(TableLayoutPanel fields, string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = new Padding(0, 16, 0, 5)
        };
        AddWide(fields, label);
    }

    public static Control Root(TableLayoutPanel fields, Button save, Button cancel)
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        scroll.Controls.Add(fields);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12)
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        var root = new Panel { Dock = DockStyle.Fill };
        root.Controls.Add(scroll);
        root.Controls.Add(actions);
        return root;
    }

    public static Button SaveButton(string name) => new()
    {
        Name = name,
        Text = "保存",
        AutoSize = true,
        MinimumSize = new Size(96, 36)
    };

    public static Button CancelButton(string name) => new()
    {
        Name = name,
        Text = "取消",
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
        MinimumSize = new Size(96, 36)
    };
}
