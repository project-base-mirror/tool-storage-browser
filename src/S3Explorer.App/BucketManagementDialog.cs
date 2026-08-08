using System.Text;
using S3Explorer.Core;

namespace S3Explorer.App;

internal enum BucketManagementPage
{
    Overview,
    Policy,
    Acl,
    AccessControls,
    Cors,
    Versioning,
    Encryption,
    Tags,
    Lifecycle,
    ObjectLock,
    EmptyBucket
}

internal sealed partial class BucketManagementDialog : Form
{
    private readonly IS3StorageService _storage;
    private readonly ConnectionProfile _profile;
    private readonly string _bucket;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _overview = ReadOnlyTextBox();
    private readonly TextBox _policy = new()
    {
        Dock = DockStyle.Fill, Multiline = true, AcceptsTab = true,
        ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 10f)
    };
    private readonly Label _policyReason = ReasonLabel();
    private readonly Button _policyReload = ActionButton("重新读取");
    private readonly Button _policyFormat = ActionButton("格式化");
    private readonly Button _policySave = ActionButton("保存 Policy");
    private readonly Button _policyDelete = ActionButton("删除 Policy");
    private readonly Label _aclSummary = ReasonLabel();
    private readonly ComboBox _aclMode = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 220
    };
    private readonly ListView _aclGrants = DetailsList("主体", 470, "权限", 180);
    private readonly Button _aclSave = ActionButton("保存 ACL");
    private readonly Label _accessReason = ReasonLabel();
    private readonly CheckBox _blockPublicAcls = new() { Text = "阻止新的公开 ACL", AutoSize = true };
    private readonly CheckBox _ignorePublicAcls = new() { Text = "忽略已有公开 ACL", AutoSize = true };
    private readonly CheckBox _blockPublicPolicy = new() { Text = "阻止公开 Bucket Policy", AutoSize = true };
    private readonly CheckBox _restrictPublicBuckets = new() { Text = "限制公开 Bucket", AutoSize = true };
    private readonly Button _accessSave = ActionButton("保存 Public Access Block");
    private readonly ComboBox _ownership = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 240
    };
    private readonly Button _ownershipSave = ActionButton("保存 Object Ownership");
    private readonly Label _emptySummary = ReasonLabel();
    private readonly Label _versionWarning = ReasonLabel();
    private readonly TextBox _emptyConfirmation = new() { Width = 320 };
    private readonly Button _scanBucket = ActionButton("扫描 Bucket");
    private readonly Button _emptyBucket = ActionButton("清空 Bucket");
    private BucketPropertiesSnapshot? _properties;
    private BucketEmptySummary? _lastSummary;
    private bool _busy;

    public BucketManagementDialog(
        IS3StorageService storage, ConnectionProfile profile, string bucket,
        BucketManagementPage initialPage)
    {
        _storage = storage;
        _profile = profile;
        _bucket = bucket;

        Text = $"Bucket 管理 - {bucket}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 650);
        MinimumSize = new Size(760, 540);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _aclMode.Items.AddRange(["私有", "公开读取"]);
        _aclMode.SelectedIndex = 0;
        _ownership.Items.AddRange(Enum.GetValues<BucketObjectOwnershipMode>().Cast<object>().ToArray());

        _tabs.TabPages.AddRange([
            BuildOverviewTab(), BuildPolicyTab(), BuildAclTab(),
            BuildAccessTab(), BuildCorsTab(), BuildVersioningTab(),
            BuildEncryptionTab(), BuildTagsTab(), BuildLifecycleTab(),
            BuildObjectLockTab(), BuildEmptyTab()
        ]);
        _tabs.SelectedIndex = Math.Clamp((int)initialPage, 0, _tabs.TabPages.Count - 1);

        var close = new Button
        {
            Text = "关闭", DialogResult = DialogResult.OK, Size = new Size(100, 32)
        };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        footer.Controls.Add(close);
        Controls.Add(_tabs);
        Controls.Add(footer);
        AcceptButton = close;
        CancelButton = close;

        Shown += async (_, _) =>
        {
            await LoadAllAsync();
            await LoadSelectedConfigurationAsync();
        };
        _tabs.SelectedIndexChanged += async (_, _) => await LoadSelectedConfigurationAsync();
        FormClosed += (_, _) => _cancellation.Cancel();
        _policyReload.Click += async (_, _) => await LoadPolicyAsync();
        _policyFormat.Click += (_, _) => FormatPolicy();
        _policySave.Click += async (_, _) => await SavePolicyAsync();
        _policyDelete.Click += async (_, _) => await DeletePolicyAsync();
        _aclSave.Click += async (_, _) => await SaveAclAsync();
        _accessSave.Click += async (_, _) => await SavePublicAccessAsync();
        _ownershipSave.Click += async (_, _) => await SaveOwnershipAsync();
        _scanBucket.Click += async (_, _) => await ScanBucketAsync();
        _emptyBucket.Click += async (_, _) => await EmptyBucketAsync();
        _emptyConfirmation.TextChanged += (_, _) => UpdateEmptyButton();
        UpdateEmptyButton();
    }

    public bool BucketEmptied { get; private set; }

    private TabPage BuildOverviewTab()
    {
        var page = NewPage("概览");
        page.Controls.Add(_overview);
        return page;
    }

    private TabPage BuildPolicyTab()
    {
        var page = NewPage("Policy");
        var buttons = ButtonBar(_policyDelete, _policySave, _policyFormat, _policyReload);
        page.Controls.Add(_policy);
        page.Controls.Add(_policyReason);
        page.Controls.Add(buttons);
        _policyReason.Dock = DockStyle.Top;
        buttons.Dock = DockStyle.Bottom;
        return page;
    }

    private TabPage BuildAclTab()
    {
        var page = NewPage("ACL");
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 48, Padding = new Padding(8),
            WrapContents = false
        };
        top.Controls.Add(new Label { Text = "ACL：", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        top.Controls.Add(_aclMode);
        top.Controls.Add(_aclSave);
        page.Controls.Add(_aclGrants);
        page.Controls.Add(_aclSummary);
        page.Controls.Add(top);
        _aclSummary.Dock = DockStyle.Top;
        return page;
    }

    private TabPage BuildAccessTab()
    {
        var page = NewPage("访问控制");
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(16), AutoScroll = true, WrapContents = false
        };
        layout.Controls.Add(_accessReason);
        layout.Controls.Add(new Label { Text = "Public Access Block", AutoSize = true, Font = BoldFont() });
        layout.Controls.Add(_blockPublicAcls);
        layout.Controls.Add(_ignorePublicAcls);
        layout.Controls.Add(_blockPublicPolicy);
        layout.Controls.Add(_restrictPublicBuckets);
        layout.Controls.Add(_accessSave);
        layout.Controls.Add(new Label { Text = "Object Ownership", AutoSize = true, Font = BoldFont(), Margin = new Padding(0, 18, 0, 4) });
        layout.Controls.Add(_ownership);
        layout.Controls.Add(_ownershipSave);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildEmptyTab()
    {
        var page = NewPage("安全清空");
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(16), AutoScroll = true, WrapContents = false
        };
        layout.Controls.Add(new Label
        {
            Text = "将扫描并删除普通对象、历史版本、Delete Marker，并终止未完成 Multipart。此操作不可撤销。",
            AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Color.DarkRed
        });
        layout.Controls.Add(_scanBucket);
        layout.Controls.Add(_emptySummary);
        layout.Controls.Add(_versionWarning);
        layout.Controls.Add(new Label
        {
            Text = $"输入 Bucket 名称“{_bucket}”以确认：", AutoSize = true, Margin = new Padding(0, 16, 0, 4)
        });
        layout.Controls.Add(_emptyConfirmation);
        layout.Controls.Add(_emptyBucket);
        page.Controls.Add(layout);
        return page;
    }

    private async Task LoadAllAsync()
    {
        await ExecuteAsync("读取 Bucket 属性", async token =>
        {
            _properties = await _storage.GetBucketPropertiesAsync(_profile, _bucket, token);
            ApplyProperties();
            if (_properties.Capabilities.Policy.Supported)
                _policy.Text = await _storage.GetBucketPolicyAsync(_profile, _bucket, token) ?? string.Empty;
        });
    }

    private void ApplyProperties()
    {
        if (_properties is null) return;
        var value = _properties;
        var text = new StringBuilder();
        text.AppendLine($"Bucket：{value.Bucket}");
        text.AppendLine($"Endpoint：{value.Endpoint}");
        text.AppendLine($"服务类型：{value.ServiceType}");
        text.AppendLine($"签名 Region：{value.Region}");
        text.AppendLine($"版本控制：{value.VersioningStatus}");
        text.AppendLine($"加密：{value.EncryptionSummary}");
        text.AppendLine($"Policy：{(value.HasPolicy ? "已配置" : "未配置")}");
        text.AppendLine($"ACL：{value.Acl.Summary}");
        text.AppendLine();
        text.AppendLine("服务能力：");
        AppendCapability(text, "Bucket Policy", value.Capabilities.Policy);
        AppendCapability(text, "Bucket ACL", value.Capabilities.Acl);
        AppendCapability(text, "Public Access Block", value.Capabilities.PublicAccessBlock);
        AppendCapability(text, "Object Ownership", value.Capabilities.ObjectOwnership);
        AppendCapability(text, "CORS", value.Capabilities.Cors);
        AppendCapability(text, "Versioning", value.Capabilities.Versioning);
        AppendCapability(text, "Encryption", value.Capabilities.Encryption);
        AppendCapability(text, "SSE-KMS", value.Capabilities.KmsEncryption);
        AppendCapability(text, "Tagging", value.Capabilities.Tagging);
        AppendCapability(text, "Lifecycle", value.Capabilities.Lifecycle);
        AppendCapability(text, "Lifecycle Storage Transitions", value.Capabilities.LifecycleStorageTransitions);
        AppendCapability(text, "Lifecycle Multipart Cleanup", value.Capabilities.LifecycleMultipartCleanup);
        AppendCapability(text, "Object Lock", value.Capabilities.ObjectLock);
        AppendCapability(text, "Bucket Logging", value.Capabilities.Logging);
        AppendCapability(text, "安全清空", value.Capabilities.EmptyBucket);
        _overview.Text = text.ToString();

        _policyReason.Text = value.Capabilities.Policy.Reason;
        SetControls(value.Capabilities.Policy.Supported, _policy, _policyReload, _policyFormat);
        SetControls(value.Capabilities.Policy.CanWrite, _policySave, _policyDelete);

        _aclSummary.Text = $"所有者：{value.Acl.Owner}；当前：{value.Acl.Summary}；{value.Capabilities.Acl.Reason}";
        _aclMode.SelectedIndex = value.Acl.Mode == BucketAclMode.PublicRead ? 1 : 0;
        _aclGrants.Items.Clear();
        foreach (var grant in value.Acl.Grants)
        {
            var item = new ListViewItem(grant.Grantee);
            item.SubItems.Add(grant.Permission);
            _aclGrants.Items.Add(item);
        }
        SetControls(value.Capabilities.Acl.Supported, _aclMode);
        SetControls(value.Capabilities.Acl.CanWrite, _aclSave);

        var pab = value.PublicAccessBlock;
        _blockPublicAcls.Checked = pab?.BlockPublicAcls ?? false;
        _ignorePublicAcls.Checked = pab?.IgnorePublicAcls ?? false;
        _blockPublicPolicy.Checked = pab?.BlockPublicPolicy ?? false;
        _restrictPublicBuckets.Checked = pab?.RestrictPublicBuckets ?? false;
        var pabSupported = value.Capabilities.PublicAccessBlock.Supported;
        SetControls(pabSupported, _blockPublicAcls, _ignorePublicAcls, _blockPublicPolicy, _restrictPublicBuckets);
        SetControls(value.Capabilities.PublicAccessBlock.CanWrite, _accessSave);
        var ownershipSupported = value.Capabilities.ObjectOwnership.Supported;
        SetControls(ownershipSupported, _ownership);
        SetControls(value.Capabilities.ObjectOwnership.CanWrite, _ownershipSave);
        if (value.ObjectOwnership is not null) _ownership.SelectedItem = value.ObjectOwnership.Value;
        _accessReason.Text = $"Public Access Block：{value.Capabilities.PublicAccessBlock.Reason}\r\nObject Ownership：{value.Capabilities.ObjectOwnership.Reason}";
        ApplyConfigurationCapabilities(value.Capabilities);
    }

    private async Task LoadPolicyAsync()
    {
        await ExecuteAsync("读取 Bucket Policy", async token =>
            _policy.Text = await _storage.GetBucketPolicyAsync(_profile, _bucket, token) ?? string.Empty);
    }

    private void FormatPolicy()
    {
        try { _policy.Text = BucketPolicyDocument.ValidateAndNormalize(_policy.Text); }
        catch (Exception exception) { ErrorDialog.ShowException(this, "Policy 无效", "校验 Policy", exception, _bucket); }
    }

    private async Task SavePolicyAsync()
    {
        await ExecuteAsync("保存 Bucket Policy", async token =>
        {
            var normalized = BucketPolicyDocument.ValidateAndNormalize(_policy.Text);
            await _storage.PutBucketPolicyAsync(_profile, _bucket, normalized, token);
            _policy.Text = await _storage.GetBucketPolicyAsync(_profile, _bucket, token) ?? normalized;
            await ReloadPropertiesAsync(token);
        });
    }

    private async Task DeletePolicyAsync()
    {
        if (MessageBox.Show(this, "确定删除当前 Bucket Policy 吗？", "删除 Policy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await ExecuteAsync("删除 Bucket Policy", async token =>
        {
            await _storage.DeleteBucketPolicyAsync(_profile, _bucket, token);
            _policy.Clear();
            await ReloadPropertiesAsync(token);
        });
    }

    private async Task SaveAclAsync()
    {
        var mode = _aclMode.SelectedIndex == 1 ? BucketAclMode.PublicRead : BucketAclMode.Private;
        if (mode == BucketAclMode.PublicRead && MessageBox.Show(this, "公开读取会允许匿名读取对象。仍要继续吗？", "公开 ACL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await ExecuteAsync("保存 Bucket ACL", async token =>
        {
            await _storage.PutBucketAclAsync(_profile, _bucket, mode, token);
            await ReloadPropertiesAsync(token);
        });
    }

    private async Task SavePublicAccessAsync()
    {
        var configuration = new BucketPublicAccessBlockSnapshot(
            _blockPublicAcls.Checked, _ignorePublicAcls.Checked,
            _blockPublicPolicy.Checked, _restrictPublicBuckets.Checked);
        await ExecuteAsync("保存 Public Access Block", async token =>
        {
            await _storage.PutBucketPublicAccessBlockAsync(_profile, _bucket, configuration, token);
            await ReloadPropertiesAsync(token);
        });
    }

    private async Task SaveOwnershipAsync()
    {
        if (_ownership.SelectedItem is not BucketObjectOwnershipMode mode) return;
        await ExecuteAsync("保存 Object Ownership", async token =>
        {
            await _storage.PutBucketObjectOwnershipAsync(_profile, _bucket, mode, token);
            await ReloadPropertiesAsync(token);
        });
    }

    private async Task ScanBucketAsync()
    {
        await ExecuteAsync("扫描 Bucket", async token =>
        {
            _lastSummary = await _storage.ScanBucketAsync(_profile, _bucket, token);
            _emptySummary.Text = $"普通对象 {_lastSummary.ObjectCount:N0}；版本 {_lastSummary.VersionCount:N0}；Delete Marker {_lastSummary.DeleteMarkerCount:N0}；未完成 Multipart {_lastSummary.MultipartUploadCount:N0}；当前对象大小 {FileSizeFormatter.Format(_lastSummary.TotalBytes)}";
            _versionWarning.Text = _lastSummary.VersionListingSupported
                ? "已扫描版本与 Delete Marker。"
                : "服务端不支持版本列表；只能确认普通对象与未完成 Multipart。";
            UpdateEmptyButton();
        });
    }

    private async Task EmptyBucketAsync()
    {
        if (_lastSummary is null || !string.Equals(_emptyConfirmation.Text, _bucket, StringComparison.Ordinal)) return;
        if (MessageBox.Show(this, $"确定永久清空 Bucket “{_bucket}”吗？此操作不可撤销。", "清空 Bucket", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await ExecuteAsync("清空 Bucket", async token =>
        {
            var result = await _storage.EmptyBucketAsync(_profile, _bucket, token);
            BucketEmptied = true;
            _emptyConfirmation.Clear();
            _lastSummary = await _storage.ScanBucketAsync(_profile, _bucket, token);
            _emptySummary.Text = $"已删除普通对象 {result.DeletedObjects:N0}、版本 {result.DeletedVersions:N0}、Delete Marker {result.DeletedDeleteMarkers:N0}，并终止 Multipart {result.AbortedMultipartUploads:N0}。当前 Bucket {(_lastSummary.IsEmpty ? "为空" : "仍有内容")}。";
            UpdateEmptyButton();
        });
    }

    private async Task ReloadPropertiesAsync(CancellationToken token)
    {
        _properties = await _storage.GetBucketPropertiesAsync(_profile, _bucket, token);
        ApplyProperties();
    }

    private async Task ExecuteAsync(string operation, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception exception) { ErrorDialog.ShowException(this, $"{operation}失败", operation, exception, $"s3://{_profile.Name}/{_bucket}"); }
        finally
        {
            UseWaitCursor = false;
            _busy = false;
            UpdateEmptyButton();
        }
    }

    private void UpdateEmptyButton() => _emptyBucket.Enabled =
        !_busy && _lastSummary is not null &&
        string.Equals(_emptyConfirmation.Text, _bucket, StringComparison.Ordinal);

    private static void AppendCapability(StringBuilder text, string name, BucketFeatureSupport support) =>
        text.AppendLine($"- {name}：{support.Access switch
        {
            ProviderCapabilityAccess.ReadWrite => "支持",
            ProviderCapabilityAccess.ReadOnly => "只读",
            _ => "不支持"
        }}；{support.Reason}");

    private static void SetControls(bool enabled, params Control[] controls)
    {
        foreach (var control in controls) control.Enabled = enabled;
    }

    private static TabPage NewPage(string text) => new(text) { Padding = new Padding(8) };
    private static TextBox ReadOnlyTextBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = SystemColors.Window
    };
    private static Label ReasonLabel() => new()
    {
        AutoSize = true, MaximumSize = new Size(820, 0), Padding = new Padding(8)
    };
    private static Button ActionButton(string text) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(4)
    };
    private static Font BoldFont() => new(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold);
    private static FlowLayoutPanel ButtonBar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Height = 48, Padding = new Padding(4), FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        panel.Controls.AddRange(controls);
        return panel;
    }
    private static ListView DetailsList(string first, int firstWidth, string second, int secondWidth)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        list.Columns.Add(first, firstWidth);
        list.Columns.Add(second, secondWidth);
        return list;
    }
}
