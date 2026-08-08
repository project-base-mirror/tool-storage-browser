using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class ObjectPropertiesDialog
{
    private IS3StorageService? _storage;
    private ConnectionProfile? _profile;
    private ObjectProperties? _objectProperties;
    private readonly CancellationTokenSource _objectLockCancellation = new();
    private readonly Label _objectLockReason = new()
    {
        Name = "ObjectLockCapabilityReason",
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = SystemColors.GrayText
    };
    private readonly TextBox _objectLockStatus = new()
    {
        Name = "ObjectLockStatus",
        Multiline = true,
        ReadOnly = true,
        Width = 610,
        Height = 94,
        BackColor = SystemColors.Window,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly ComboBox _retentionMode = new()
    {
        Name = "ObjectRetentionMode",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220
    };
    private readonly DateTimePicker _retainUntil = new()
    {
        Name = "ObjectRetainUntilDate",
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm:ss",
        Width = 220
    };
    private readonly CheckBox _retentionAuthorized = new()
    {
        Name = "ObjectRetentionAuthorization",
        Text = "我已获得修改此对象 Retention 的明确授权",
        AutoSize = true
    };
    private readonly Button _retentionSave = ActionButton("SaveObjectRetentionButton", "应用 Retention");
    private readonly ComboBox _legalHoldMode = new()
    {
        Name = "ObjectLegalHoldMode",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220
    };
    private readonly CheckBox _legalHoldAuthorized = new()
    {
        Name = "ObjectLegalHoldAuthorization",
        Text = "我已获得修改此对象 Legal Hold 的明确授权",
        AutoSize = true
    };
    private readonly Button _legalHoldSave = ActionButton("SaveObjectLegalHoldButton", "应用 Legal Hold");
    private readonly Button _objectLockReload = ActionButton("ReloadObjectLockButton", "重新读取");
    private BucketObjectLockSnapshot? _bucketObjectLock;
    private ObjectLockSnapshot? _objectLock;
    private bool _objectLockBusy;

    private TabPage BuildObjectLock(ObjectProperties properties)
    {
        _retentionMode.Items.AddRange(["Governance", "Compliance"]);
        _retentionMode.SelectedIndex = 0;
        _retainUntil.MinDate = DateTime.Today;
        _retainUntil.Value = DateTime.Now.AddDays(30);
        _legalHoldMode.Items.AddRange(["Off", "On"]);
        _legalHoldMode.SelectedIndex = 0;

        var capability = _profile is null
            ? BucketFeatureSupport.No("当前调用未提供连接上下文，仅显示属性占位")
            : S3ProviderCapabilityRegistry.For(_profile.ServiceType).Object.ObjectLock;
        _objectLockReason.Text = capability.Reason;

        _retentionAuthorized.CheckedChanged += (_, _) => UpdateObjectLockActions();
        _legalHoldAuthorized.CheckedChanged += (_, _) => UpdateObjectLockActions();
        _retentionSave.Click += async (_, _) => await SaveRetentionAsync();
        _legalHoldSave.Click += async (_, _) => await SaveLegalHoldAsync();
        _objectLockReload.Click += async (_, _) => await LoadObjectLockAsync(force: true);

        var retentionRow = Row(
            new Label { Text = "模式：", AutoSize = true, Padding = new Padding(0, 8, 4, 0) },
            _retentionMode,
            new Label { Text = "保留到：", AutoSize = true, Padding = new Padding(12, 8, 4, 0) },
            _retainUntil);
        var retentionAuthorizationRow = Row(_retentionAuthorized, _retentionSave);
        var legalHoldRow = Row(
            new Label { Text = "状态：", AutoSize = true, Padding = new Padding(0, 8, 4, 0) },
            _legalHoldMode,
            _legalHoldSave);
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16)
        };
        layout.Controls.Add(new Label { Text = "Object Lock 状态", AutoSize = true, Font = BoldFont() });
        layout.Controls.Add(_objectLockReason);
        layout.Controls.Add(_objectLockStatus);
        layout.Controls.Add(_objectLockReload);
        layout.Controls.Add(new Label
        {
            Text = "Retention",
            AutoSize = true,
            Font = BoldFont(),
            Margin = new Padding(0, 14, 0, 2)
        });
        layout.Controls.Add(new Label
        {
            Text = "Compliance 模式在截止时间前不能删除、缩短或降级；本客户端不会使用 Governance Bypass。",
            AutoSize = true,
            MaximumSize = new Size(610, 0),
            ForeColor = Color.DarkRed
        });
        layout.Controls.Add(retentionRow);
        layout.Controls.Add(retentionAuthorizationRow);
        layout.Controls.Add(new Label
        {
            Text = "Legal Hold",
            AutoSize = true,
            Font = BoldFont(),
            Margin = new Padding(0, 14, 0, 2)
        });
        layout.Controls.Add(legalHoldRow);
        layout.Controls.Add(_legalHoldAuthorized);

        var page = new TabPage("Object Lock");
        page.Controls.Add(layout);
        SetObjectLockControls(capability.Supported);
        _objectLockStatus.Text = capability.Supported
            ? $"尚未读取 s3://{properties.Bucket}/{properties.Key} 的锁定状态。"
            : capability.Reason;
        return page;
    }

    private async Task LoadObjectLockAsync(bool force = false)
    {
        if (_objectLockBusy || _storage is null || _profile is null || _objectProperties is null)
            return;
        if (!force && _bucketObjectLock is not null)
            return;
        var capability = S3ProviderCapabilityRegistry.For(_profile.ServiceType).Object.ObjectLock;
        if (!capability.Supported)
            return;

        await ExecuteObjectLockAsync("读取 Object Lock", async token =>
        {
            _bucketObjectLock = await _storage.GetBucketObjectLockAsync(
                _profile, _objectProperties.Bucket, token);
            if (!_bucketObjectLock.Enabled)
            {
                _objectLock = null;
                _objectLockStatus.Text =
                    $"Bucket Object Lock：未启用\r\n对象：{_objectProperties.Key}\r\n" +
                    "不能对该 Bucket 中的对象设置 Retention 或 Legal Hold。";
                UpdateObjectLockActions();
                return;
            }

            _objectLock = await _storage.GetObjectLockAsync(
                _profile,
                _objectProperties.Bucket,
                _objectProperties.Key,
                _objectProperties.VersionId,
                token);
            ApplyObjectLockState();
        });
    }

    private void ApplyObjectLockState()
    {
        if (_bucketObjectLock is null || _objectProperties is null) return;
        var retentionText = _objectLock?.HasRetention == true
            ? $"{_objectLock.RetentionMode}，至 {_objectLock.RetainUntilDate?.LocalDateTime:G}"
            : "未设置";
        var legalHoldText = _objectLock?.LegalHoldEnabled == true ? "On" : "Off";
        _objectLockStatus.Text =
            $"Bucket Object Lock：{_bucketObjectLock.Summary}\r\n" +
            $"对象版本：{_objectLock?.VersionId ?? _objectProperties.VersionId ?? "当前版本"}\r\n" +
            $"Retention：{retentionText}\r\n" +
            $"Legal Hold：{legalHoldText}";

        if (_objectLock?.RetentionMode is not null)
            _retentionMode.SelectedIndex = _objectLock.RetentionMode == ObjectRetentionMode.Compliance ? 1 : 0;
        if (_objectLock?.RetainUntilDate is not null)
        {
            var local = _objectLock.RetainUntilDate.Value.LocalDateTime;
            _retainUntil.Value = local < _retainUntil.MinDate ? _retainUntil.MinDate : local;
        }
        _legalHoldMode.SelectedIndex = _objectLock?.LegalHoldEnabled == true ? 1 : 0;
        UpdateObjectLockActions();
    }

    private async Task SaveRetentionAsync()
    {
        if (_storage is null || _profile is null || _objectProperties is null ||
            !_retentionAuthorized.Checked || _bucketObjectLock?.Enabled != true)
            return;
        var value = new ObjectRetentionConfiguration(
            _retentionMode.SelectedIndex == 1 ? ObjectRetentionMode.Compliance : ObjectRetentionMode.Governance,
            new DateTimeOffset(_retainUntil.Value));
        try { value.Validate(_objectLock); }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "Retention 配置无效", "校验 Object Retention", exception, ObjectLocation());
            return;
        }

        var warning = value.Mode == ObjectRetentionMode.Compliance
            ? "\r\n\r\nCompliance 在截止时间前不能缩短、取消或降级。"
            : "\r\n\r\n本客户端不会发送 Governance Bypass。";
        if (MessageBox.Show(
                this,
                $"确定修改以下单个对象的 Retention 吗？\r\n\r\n{ObjectLocation()}\r\n版本：{_objectProperties.VersionId ?? "当前版本"}\r\n模式：{value.Mode}\r\n保留到：{value.RetainUntilDate.LocalDateTime:G}{warning}",
                "确认 Object Retention",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        await ExecuteObjectLockAsync("设置 Object Retention", async token =>
        {
            await _storage.PutObjectRetentionAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, value, token);
            _objectLock = await _storage.GetObjectLockAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, token);
            ApplyObjectLockState();
        });
    }

    private async Task SaveLegalHoldAsync()
    {
        if (_storage is null || _profile is null || _objectProperties is null ||
            !_legalHoldAuthorized.Checked || _bucketObjectLock?.Enabled != true)
            return;
        var enabled = _legalHoldMode.SelectedIndex == 1;
        if (MessageBox.Show(
                this,
                $"确定将以下单个对象的 Legal Hold 设置为 {(enabled ? "On" : "Off")} 吗？\r\n\r\n{ObjectLocation()}\r\n版本：{_objectProperties.VersionId ?? "当前版本"}",
                "确认 Object Legal Hold",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        await ExecuteObjectLockAsync("设置 Object Legal Hold", async token =>
        {
            await _storage.PutObjectLegalHoldAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, enabled, token);
            _objectLock = await _storage.GetObjectLockAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, token);
            ApplyObjectLockState();
        });
    }

    private async Task ExecuteObjectLockAsync(string operation, Func<CancellationToken, Task> action)
    {
        if (_objectLockBusy) return;
        _objectLockBusy = true;
        UseWaitCursor = true;
        UpdateObjectLockActions();
        try { await action(_objectLockCancellation.Token); }
        catch (OperationCanceledException) when (_objectLockCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, $"{operation}失败", operation, exception, ObjectLocation());
        }
        finally
        {
            UseWaitCursor = false;
            _objectLockBusy = false;
            UpdateObjectLockActions();
        }
    }

    private void SetObjectLockControls(bool supported)
    {
        foreach (var control in new Control[]
                 {
                     _objectLockReload, _retentionMode, _retainUntil, _retentionAuthorized,
                     _legalHoldMode, _legalHoldAuthorized
                 })
            control.Enabled = supported;
        UpdateObjectLockActions();
    }

    private void UpdateObjectLockActions()
    {
        var available = !_objectLockBusy && _bucketObjectLock?.Enabled == true;
        _retentionSave.Enabled = available && _retentionAuthorized.Checked;
        _legalHoldSave.Enabled = available && _legalHoldAuthorized.Checked;
    }

    private string ObjectLocation() => _objectProperties is null
        ? "对象"
        : $"s3://{_objectProperties.Bucket}/{_objectProperties.Key}";

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 4)
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Font BoldFont() =>
        new(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold);
}
