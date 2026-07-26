using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class ConnectionDialog : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly IS3StorageService _storage;
    private readonly TextBox _name = new();
    private readonly ComboBox _accountType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _endpoint = new();
    private readonly ComboBox _region = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _accessKey = new();
    private readonly TextBox _secretKey = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _useSessionToken = new() { Text = "使用临时 Session Token", AutoSize = true };
    private readonly TextBox _sessionToken = new() { UseSystemPasswordChar = true };
    private readonly RadioButton _auto = new() { Text = "自动", Checked = true, AutoSize = true };
    private readonly RadioButton _virtual = new() { Text = "Virtual Hosted", AutoSize = true };
    private readonly RadioButton _path = new() { Text = "Path Style", AutoSize = true };
    private readonly CheckBox _https = new() { Text = "使用 HTTPS", Checked = true, AutoSize = true };
    private readonly CheckBox _ignoreCert = new() { Text = "忽略证书错误（仅测试）", AutoSize = true, ForeColor = Color.DarkRed };
    private readonly TextBox _hostHeader = new();
    private readonly CheckBox _followRedirects = new() { Text = "处理临时重定向", Checked = true, AutoSize = true };
    private readonly CheckBox _multiDelete = new() { Text = "Multi-Object Delete", Checked = true, AutoSize = true };
    private readonly CheckBox _multipartCopy = new() { Text = "Multipart Copy", Checked = true, AutoSize = true };
    private readonly ComboBox _storageClass = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _defaultBucket = new();
    private readonly TextBox _externalBuckets = new()
    {
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        MinimumSize = new Size(0, 64)
    };
    private readonly NumericUpDown _connectionTimeout = new() { Minimum = 1, Maximum = 120, Value = 10 };
    private readonly NumericUpDown _timeout = new() { Minimum = 5, Maximum = 3600, Value = 100 };
    private readonly LinkLabel _advancedToggle = new() { Text = "显示高级设置", AutoSize = true };
    private readonly GroupBox _advancedGroup = new() { Text = "高级设置", Dock = DockStyle.Top, AutoSize = true, Visible = false };
    private readonly Button _test = new() { Text = "测试连接", Size = new Size(104, 32) };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(510, 0), Margin = new Padding(10, 8, 3, 3) };
    private readonly Button _save = new() { Text = "保存", DialogResult = DialogResult.OK, Size = new Size(96, 32) };
    private readonly Button _cancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(96, 32) };
    private readonly Label _providerLabel = FieldLabel("服务商模板：");
    private readonly Label _endpointLabel = FieldLabel("Endpoint：");
    private readonly Label _regionLabel = FieldLabel("Region：");
    private readonly Label _regionHint = HintLabel("Amazon S3 使用区域；不需要 Region 的服务会自动采用正确的签名值并隐藏此项。");
    private readonly Label _sessionTokenLabel = FieldLabel("Session Token：");
    private readonly Guid _id;
    private bool _loading;

    public ConnectionProfile Profile { get; private set; }

    public ConnectionDialog(IS3StorageService storage, ConnectionProfile? profile = null)
    {
        _storage = storage;
        Profile = profile ?? ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);
        _id = Profile.Id;

        Text = profile is null ? "新建对象存储连接" : "编辑对象存储连接";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 720);
        MinimumSize = new Size(700, 620);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _accountType.Items.AddRange(Enum.GetValues<S3AccountCategory>()
            .Select(category => (object)new Choice<S3AccountCategory>(category, S3ProviderCatalog.CategoryDisplayName(category)))
            .ToArray());
        _provider.Items.AddRange(S3ProviderCatalog.CompatibleProviders
            .Select(definition => (object)new Choice<S3ServiceType>(definition.ServiceType, definition.DisplayName))
            .ToArray());
        _storageClass.Items.AddRange(["STANDARD", "STANDARD_IA", "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER", "DEEP_ARCHIVE"]);
        _region.Items.AddRange(["us-east-1", "us-west-1", "us-west-2", "us-west-004", "eu-west-1", "eu-central-1", "ap-southeast-1", "ap-northeast-1", "ap-guangzhou", "oss-cn-hangzhou"]);

        BuildLayout();
        LoadProfile(Profile);

        _accountType.SelectedIndexChanged += (_, _) => ApplyAccountSelection(applyPreset: true);
        _provider.SelectedIndexChanged += (_, _) => ApplyAccountSelection(applyPreset: true);
        _useSessionToken.CheckedChanged += (_, _) => UpdateSessionTokenVisibility();
        _advancedToggle.LinkClicked += (_, _) => ToggleAdvancedSettings();
        _https.CheckedChanged += (_, _) => ApplyHttpsToEndpoint();
        _test.Click += async (_, _) => await TestConnectionAsync();
        _save.Click += (_, _) => SaveProfile();
    }

    private void BuildLayout()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(18, 12, 18, 8),
            ColumnCount = 2
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = new PictureBox
        {
            Image = UiIcons.Create(UiIconKind.Account, 40),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill
        };
        var title = new Label
        {
            Text = "连接到对象存储",
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(4, 5, 3, 0)
        };
        var subtitle = new Label
        {
            Text = "先选择账户类型，只填写该服务实际需要的参数。",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Margin = new Padding(4, 2, 3, 3)
        };
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.AddRange([title, subtitle]);
        header.Controls.Add(icon, 0, 0);
        header.Controls.Add(heading, 1, 0);

        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 0, 16, 8) };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var basic = new GroupBox
        {
            Text = "基本信息",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 8)
        };
        var basicTable = NewFormTable();
        var row = 0;
        AddField(basicTable, ref row, FieldLabel("连接名称："), _name);
        AddField(basicTable, ref row, FieldLabel("账户类型："), _accountType);
        AddField(basicTable, ref row, _providerLabel, _provider);
        AddField(basicTable, ref row, _endpointLabel, _endpoint);
        AddField(basicTable, ref row, _regionLabel, _region);
        AddHint(basicTable, ref row, _regionHint);
        AddField(basicTable, ref row, FieldLabel("Access Key："), _accessKey);

        var secretPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 28 };
        secretPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        secretPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _secretKey.Dock = DockStyle.Fill;
        secretPanel.Controls.Add(_secretKey, 0, 0);
        var show = new CheckBox { Text = "显示", AutoSize = true };
        show.CheckedChanged += (_, _) => _secretKey.UseSystemPasswordChar = !show.Checked;
        secretPanel.Controls.Add(show, 1, 0);
        AddField(basicTable, ref row, FieldLabel("Secret Key："), secretPanel);
        AddField(basicTable, ref row, new Label(), _useSessionToken);
        AddField(basicTable, ref row, _sessionTokenLabel, _sessionToken);
        basic.Controls.Add(basicTable);

        _advancedGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var advanced = NewFormTable();
        var advancedRow = 0;
        AddField(advanced, ref advancedRow, FieldLabel("默认进入 Bucket："), _defaultBucket);
        AddField(advanced, ref advancedRow, FieldLabel("外部 Bucket："), _externalBuckets);
        AddHint(advanced, ref advancedRow, "每行一个 Bucket；适用于没有 ListBuckets 权限的最小权限账户。");
        var style = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        style.Controls.AddRange([_auto, _virtual, _path]);
        AddField(advanced, ref advancedRow, FieldLabel("地址风格："), style);
        AddField(advanced, ref advancedRow, FieldLabel("自定义 Host Header："), _hostHeader);
        var security = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        security.Controls.AddRange([_https, _ignoreCert]);
        AddField(advanced, ref advancedRow, FieldLabel("连接安全："), security);
        var compatibility = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        compatibility.Controls.AddRange([_followRedirects, _multiDelete, _multipartCopy]);
        AddField(advanced, ref advancedRow, FieldLabel("兼容性："), compatibility);
        AddField(advanced, ref advancedRow, FieldLabel("默认存储类型："), _storageClass);
        AddField(advanced, ref advancedRow, FieldLabel("连接超时（秒）："), _connectionTimeout);
        AddField(advanced, ref advancedRow, FieldLabel("请求超时（秒）："), _timeout);
        _advancedGroup.Controls.Add(advanced);

        _advancedToggle.Margin = new Padding(4, 2, 0, 8);
        stack.Controls.Add(basic, 0, 0);
        stack.Controls.Add(_advancedToggle, 0, 1);
        stack.Controls.Add(_advancedGroup, 0, 2);
        content.Controls.Add(stack);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            Padding = new Padding(16, 8, 16, 10),
            ColumnCount = 2
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var testPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        testPanel.Controls.AddRange([_test, _result]);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false
        };
        buttons.Controls.AddRange([_cancel, _save]);
        footer.Controls.Add(testPanel, 0, 0);
        footer.Controls.Add(buttons, 1, 0);

        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = _save;
        CancelButton = _cancel;
    }

    private static TableLayoutPanel NewFormTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 10),
            ColumnCount = 2
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 3, 3)
    };

    private static void AddField(TableLayoutPanel table, ref int row, Control label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, 1, row);
        row++;
    }

    private static Label HintLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(650, 0),
        Margin = new Padding(6, 0, 3, 8)
    };

    private static void AddHint(TableLayoutPanel table, ref int row, string text) =>
        AddHint(table, ref row, HintLabel(text));

    private static void AddHint(TableLayoutPanel table, ref int row, Label label)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, row);
        table.SetColumnSpan(label, 2);
        row++;
    }

    private void LoadProfile(ConnectionProfile profile)
    {
        _loading = true;
        try
        {
            var definition = S3ProviderCatalog.Get(profile.ServiceType);
            SelectChoice(_accountType, definition.Category);
            SelectChoice(_provider, profile.ServiceType);
            _name.Text = profile.Name;
            _endpoint.Text = profile.Endpoint;
            _region.Text = string.IsNullOrWhiteSpace(profile.SignatureRegion) ? profile.Region : profile.SignatureRegion;
            _accessKey.Text = profile.AccessKey;
            _secretKey.Text = profile.SecretKey;
            _sessionToken.Text = profile.SessionToken;
            _useSessionToken.Checked = profile.UsesTemporarySessionCredentials;
            _defaultBucket.Text = profile.DefaultBucket;
            _externalBuckets.Lines = profile.ExternalBuckets.ToArray();
            _auto.Checked = profile.AddressingStyle == AddressingStyle.Auto;
            _virtual.Checked = profile.AddressingStyle == AddressingStyle.VirtualHosted;
            _path.Checked = profile.AddressingStyle == AddressingStyle.PathStyle;
            _https.Checked = profile.UseHttps;
            _ignoreCert.Checked = profile.IgnoreCertificateErrors;
            _hostHeader.Text = profile.CustomHostHeader;
            _followRedirects.Checked = profile.FollowTemporaryRedirects;
            _multiDelete.Checked = profile.EnableMultiObjectDelete;
            _multipartCopy.Checked = profile.EnableMultipartCopy;
            _storageClass.SelectedItem = profile.DefaultStorageClass;
            if (_storageClass.SelectedIndex < 0) _storageClass.SelectedIndex = 0;
            _connectionTimeout.Value = Math.Clamp(profile.ConnectionTimeoutSeconds, 1, 120);
            _timeout.Value = Math.Clamp(profile.RequestTimeoutSeconds, 5, 3600);
        }
        finally
        {
            _loading = false;
        }
        ApplyAccountSelection(applyPreset: false);
        UpdateSessionTokenVisibility();
    }

    private void ApplyAccountSelection(bool applyPreset)
    {
        if (_loading) return;
        var category = SelectedValue(_accountType, S3AccountCategory.AmazonS3);
        _providerLabel.Visible = _provider.Visible = category == S3AccountCategory.S3Compatible;

        var serviceType = category == S3AccountCategory.S3Compatible
            ? SelectedValue(_provider, S3ServiceType.Custom)
            : S3ProviderCatalog.DefaultServiceType(category);
        if (category == S3AccountCategory.S3Compatible && _provider.SelectedIndex < 0)
        {
            SelectChoice(_provider, S3ServiceType.Custom);
            serviceType = S3ServiceType.Custom;
        }

        var definition = S3ProviderCatalog.Get(serviceType);
        var endpointVisible = category == S3AccountCategory.S3Compatible;
        _endpointLabel.Visible = _endpoint.Visible = endpointVisible;
        _https.Enabled = endpointVisible;
        if (!endpointVisible)
            _https.Checked = true;
        var regionVisible = definition.RegionInput != RegionInputMode.Hidden;
        _regionLabel.Visible = _region.Visible = regionVisible;
        _regionLabel.Text = definition.RegionInput == RegionInputMode.Optional
            ? "签名 Region（可选）："
            : "Region：";
        _regionHint.Text = definition.RegionInput switch
        {
            RegionInputMode.Hidden => $"{definition.DisplayName} 不需要手动设置 Region；程序会自动使用签名值 {definition.DefaultRegion}。",
            RegionInputMode.Optional => "仅在服务商明确要求自定义 SigV4 Region 时填写；留空会使用模板默认值。",
            _ => "Region 同时用于服务区域与 SigV4 签名。"
        };

        if (!applyPreset) return;
        var preset = ConnectionProfile.CreatePreset(serviceType);
        _endpoint.Text = preset.Endpoint;
        _region.Text = preset.EffectiveSignatureRegion;
        _https.Checked = preset.UseHttps;
        _path.Checked = preset.AddressingStyle == AddressingStyle.PathStyle;
        _virtual.Checked = preset.AddressingStyle == AddressingStyle.VirtualHosted;
        _auto.Checked = preset.AddressingStyle == AddressingStyle.Auto;
        _result.Text = string.Empty;
    }

    private void UpdateSessionTokenVisibility()
    {
        var visible = _useSessionToken.Checked;
        _sessionTokenLabel.Visible = _sessionToken.Visible = visible;
        if (!visible) _sessionToken.Text = string.Empty;
    }

    private void ToggleAdvancedSettings()
    {
        _advancedGroup.Visible = !_advancedGroup.Visible;
        _advancedToggle.Text = _advancedGroup.Visible ? "隐藏高级设置" : "显示高级设置";
    }

    private void ApplyHttpsToEndpoint()
    {
        if (_loading || !_https.Enabled || !Uri.TryCreate(_endpoint.Text.Trim(), UriKind.Absolute, out var uri)) return;
        var targetScheme = _https.Checked ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        if (string.Equals(uri.Scheme, targetScheme, StringComparison.OrdinalIgnoreCase)) return;
        var builder = new UriBuilder(uri) { Scheme = targetScheme, Port = -1 };
        _endpoint.Text = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private ConnectionProfile ReadProfile()
    {
        var category = SelectedValue(_accountType, S3AccountCategory.AmazonS3);
        var serviceType = category == S3AccountCategory.S3Compatible
            ? SelectedValue(_provider, S3ServiceType.Custom)
            : S3ProviderCatalog.DefaultServiceType(category);
        var definition = S3ProviderCatalog.Get(serviceType);
        var signingRegion = S3ProviderCatalog.ResolveSigningRegion(serviceType, _region.Text);
        var endpoint = category == S3AccountCategory.S3Compatible
            ? _endpoint.Text.Trim()
            : definition.DefaultEndpoint;
        var externalBuckets = _externalBuckets.Lines
            .Select(bucket => bucket.Trim())
            .Where(bucket => bucket.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ConnectionProfile
        {
            Id = _id,
            Name = _name.Text.Trim(),
            ServiceType = serviceType,
            Endpoint = endpoint,
            Region = signingRegion,
            SignatureRegion = signingRegion,
            AccessKey = _accessKey.Text.Trim(),
            SecretKey = _secretKey.Text,
            SessionToken = _useSessionToken.Checked ? _sessionToken.Text : string.Empty,
            DefaultBucket = _defaultBucket.Text.Trim(),
            ExternalBuckets = externalBuckets,
            AddressingStyle = _path.Checked ? AddressingStyle.PathStyle : _virtual.Checked ? AddressingStyle.VirtualHosted : AddressingStyle.Auto,
            UseHttps = _https.Checked,
            IgnoreCertificateErrors = _ignoreCert.Checked,
            CustomHostHeader = _hostHeader.Text.Trim(),
            FollowTemporaryRedirects = _followRedirects.Checked,
            EnableMultiObjectDelete = _multiDelete.Checked,
            EnableMultipartCopy = _multipartCopy.Checked,
            DefaultStorageClass = _storageClass.Text,
            ConnectionTimeoutSeconds = (int)_connectionTimeout.Value,
            RequestTimeoutSeconds = (int)_timeout.Value
        };
    }

    private void SaveProfile()
    {
        try
        {
            var candidate = ReadProfile();
            candidate.Validate();
            if (!ConfirmInsecureTls(candidate))
            {
                DialogResult = DialogResult.None;
                return;
            }
            Profile = candidate;
        }
        catch (Exception exception)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "无法保存连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task TestConnectionAsync()
    {
        try
        {
            var profile = ReadProfile();
            profile.Validate();
            if (!ConfirmInsecureTls(profile)) return;
            _test.Enabled = false;
            _result.ForeColor = SystemColors.ControlText;
            _result.Text = $"正在连接（最多 {profile.ConnectionTimeoutSeconds} 秒）...";
            var result = await _storage.TestConnectionAsync(profile, CancellationToken.None);
            _result.ForeColor = result.Success ? Color.DarkGreen : Color.DarkRed;
            _result.Text = result.Success
                ? $"{result.Message} 耗时 {result.Elapsed.TotalMilliseconds:N0} ms"
                : $"连接失败：{result.ErrorCode ?? result.Message}（HTTP {result.HttpStatusCode?.ToString() ?? "—"}，RequestId {result.RequestId ?? "—"}）";
        }
        catch (Exception exception)
        {
            _result.ForeColor = Color.DarkRed;
            _result.Text = exception.Message;
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private bool ConfirmInsecureTls(ConnectionProfile profile)
    {
        if (!profile.IgnoreCertificateErrors) return true;
        return MessageBox.Show(
            this,
            "TLS 证书验证旁路会使连接容易遭受中间人攻击。\n\n仅应在受控测试环境中启用。是否继续？",
            "不安全的 TLS 设置",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private static void SelectChoice<T>(ComboBox comboBox, T value) where T : struct, Enum
    {
        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }
    }

    private static T SelectedValue<T>(ComboBox comboBox, T fallback) where T : struct, Enum =>
        comboBox.SelectedItem is Choice<T> choice ? choice.Value : fallback;
}
