using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class ConnectionDialog : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record ConnectionDraft(
        string Endpoint,
        string Region,
        bool UseHttps,
        AddressingStyle AddressingStyle);

    private readonly IS3StorageService _storage;
    private readonly TextBox _name = new();
    private readonly ComboBox _accountType = new() { Name = "AccountTypeComboBox", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _provider = new() { Name = "ProviderComboBox", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _endpoint = new() { Name = "EndpointTextBox" };
    private readonly ComboBox _region = new() { Name = "RegionComboBox", DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _credentialSource = new()
    {
        Name = "CredentialSourceComboBox",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _awsProfileName = new() { Name = "AwsProfileNameTextBox" };
    private readonly TextBox _awsSourceProfileName = new() { Name = "AwsSourceProfileNameTextBox" };
    private readonly TextBox _awsRoleArn = new() { Name = "AwsRoleArnTextBox" };
    private readonly TextBox _awsRoleSessionName = new() { Name = "AwsRoleSessionNameTextBox" };
    private readonly TextBox _awsRoleSourceIdentity = new() { Name = "AwsRoleSourceIdentityTextBox" };
    private readonly TextBox _awsExternalId = new() { Name = "AwsExternalIdTextBox", UseSystemPasswordChar = true };
    private readonly NumericUpDown _awsSessionDuration = new()
    {
        Name = "AwsSessionDurationNumericUpDown",
        Minimum = 900,
        Maximum = 43200,
        Increment = 900,
        Value = 3600
    };
    private readonly TextBox _awsWebIdentityTokenFile = new() { Name = "AwsWebIdentityTokenFileTextBox" };
    private readonly TextBox _accessKey = new() { Name = "AccessKeyTextBox" };
    private readonly TextBox _secretKey = new() { Name = "SecretKeyTextBox", UseSystemPasswordChar = true };
    private readonly CheckBox _useSessionToken = new() { Name = "UseSessionTokenCheckBox", Text = "使用临时 Session Token", AutoSize = true };
    private readonly TextBox _sessionToken = new() { Name = "SessionTokenTextBox", UseSystemPasswordChar = true };
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
    private readonly GroupBox _advancedGroup = new() { Text = "高级设置", Dock = DockStyle.Top, AutoSize = true };
    private readonly Button _test = new() { Name = "TestConnectionButton", Text = "测试连接", Size = new Size(104, 32) };
    private readonly TextBox _result = new()
    {
        Name = "ConnectionTestResultTextBox",
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Window,
        MinimumSize = new Size(0, 62),
        Height = 62,
        Visible = false,
        TabStop = true
    };
    private readonly Button _save = new() { Text = "保存", DialogResult = DialogResult.OK, Size = new Size(96, 32) };
    private readonly Button _cancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(96, 32) };
    private readonly Label _providerLabel = FieldLabel("服务商模板：");
    private readonly Label _endpointLabel = FieldLabel("Endpoint：");
    private readonly Label _regionLabel = FieldLabel("Region：");
    private readonly Label _regionHint = HintLabel("Amazon S3 使用区域；不需要 Region 的服务会自动采用正确的签名值并隐藏此项。");
    private readonly Label _sessionTokenLabel = FieldLabel("Session Token：");
    private readonly Label _accessKeyLabel = FieldLabel("Access Key：");
    private readonly Label _secretKeyLabel = FieldLabel("Secret Key：");
    private readonly Label _awsProfileLabel = FieldLabel("AWS Profile：");
    private readonly Label _awsSourceProfileLabel = FieldLabel("源 AWS Profile：");
    private readonly Label _awsRoleArnLabel = FieldLabel("目标 Role ARN：");
    private readonly Label _awsRoleSessionNameLabel = FieldLabel("角色会话名称：");
    private readonly Label _awsRoleSourceIdentityLabel = FieldLabel("Source Identity：");
    private readonly Label _awsExternalIdLabel = FieldLabel("External ID：");
    private readonly Label _awsSessionDurationLabel = FieldLabel("会话时长（秒）：");
    private readonly Label _awsWebIdentityTokenFileLabel = FieldLabel("Token 文件：");
    private readonly Label _credentialHint = HintLabel("选择凭据的实际来源；环境和角色凭据不会保存到连接文件。");
    private readonly TableLayoutPanel _secretPanel = new() { Dock = DockStyle.Fill, ColumnCount = 2, Height = 28 };
    private readonly Dictionary<S3ServiceType, ConnectionDraft> _connectionDrafts = [];
    private readonly Guid _id;
    private bool _loading;
    private bool _applyingSelection;
    private S3ServiceType? _activeServiceType;

    public ConnectionProfile Profile { get; private set; }

    public ConnectionDialog(IS3StorageService storage, ConnectionProfile? profile = null)
    {
        _storage = storage;
        Profile = S3ProviderCatalog.RepairLegacyServiceType(
            profile ?? ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3));
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
        _region.Items.AddRange(["auto", "us-east-1", "us-west-1", "us-west-2", "us-west-004", "eu-west-1", "eu-central-1", "ap-southeast-1", "ap-northeast-1", "ap-guangzhou", "oss-cn-hangzhou"]);
        _credentialSource.Items.AddRange([
            new Choice<CredentialSourceKind>(CredentialSourceKind.StoredKeys, "已保存的 Access Key / Secret Key"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsSharedProfile, "AWS shared credentials/config Profile"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsEnvironmentVariables, "AWS 环境变量"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsContainerRole, "AWS 容器角色（ECS/EKS）"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsInstanceRole, "AWS EC2 实例角色"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsDefaultChain, "AWS SDK 默认凭据链"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsSso, "AWS IAM Identity Center（SSO Profile）"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsAssumeRole, "AWS AssumeRole（源 Profile → Role）"),
            new Choice<CredentialSourceKind>(CredentialSourceKind.AwsWebIdentity, "AWS Web Identity（Token 文件 → Role）")
        ]);

        BuildLayout();
        LoadProfile(Profile);

        _accountType.SelectedIndexChanged += (_, _) => ApplyAccountSelection(applyPreset: true);
        _provider.SelectedIndexChanged += (_, _) => ApplyAccountSelection(applyPreset: true);
        _credentialSource.SelectedIndexChanged += (_, _) => UpdateCredentialSourceVisibility();
        _useSessionToken.CheckedChanged += (_, _) => UpdateSessionTokenVisibility();
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
            RowCount = 2,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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
        AddField(basicTable, ref row, FieldLabel("凭据来源："), _credentialSource);
        AddHint(basicTable, ref row, _credentialHint);
        AddField(basicTable, ref row, _awsProfileLabel, _awsProfileName);
        AddField(basicTable, ref row, _awsSourceProfileLabel, _awsSourceProfileName);
        AddField(basicTable, ref row, _awsRoleArnLabel, _awsRoleArn);
        AddField(basicTable, ref row, _awsRoleSessionNameLabel, _awsRoleSessionName);
        AddField(basicTable, ref row, _awsRoleSourceIdentityLabel, _awsRoleSourceIdentity);
        AddField(basicTable, ref row, _awsExternalIdLabel, _awsExternalId);
        AddField(basicTable, ref row, _awsSessionDurationLabel, _awsSessionDuration);
        AddField(basicTable, ref row, _awsWebIdentityTokenFileLabel, _awsWebIdentityTokenFile);
        AddField(basicTable, ref row, _accessKeyLabel, _accessKey);

        _secretPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _secretPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _secretKey.Dock = DockStyle.Fill;
        _secretPanel.Controls.Add(_secretKey, 0, 0);
        var show = new CheckBox { Text = "显示", AutoSize = true };
        show.CheckedChanged += (_, _) => _secretKey.UseSystemPasswordChar = !show.Checked;
        _secretPanel.Controls.Add(show, 1, 0);
        AddField(basicTable, ref row, _secretKeyLabel, _secretPanel);
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

        stack.Controls.Add(basic, 0, 0);
        stack.Controls.Add(_advancedGroup, 0, 1);
        content.Controls.Add(stack);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 64),
            Padding = new Padding(16, 8, 16, 10),
            ColumnCount = 2,
            RowCount = 2
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var testPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        testPanel.Controls.Add(_test);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false
        };
        buttons.Controls.AddRange([_cancel, _save]);
        _result.Dock = DockStyle.Fill;
        _result.Margin = new Padding(0, 0, 0, 8);
        footer.Controls.Add(_result, 0, 0);
        footer.SetColumnSpan(_result, 2);
        footer.Controls.Add(testPanel, 0, 1);
        footer.Controls.Add(buttons, 1, 1);

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
            _region.Text = string.IsNullOrWhiteSpace(profile.Region) ? definition.DefaultRegion : profile.Region;
            _accessKey.Text = profile.AccessKey;
            _secretKey.Text = profile.SecretKey;
            _sessionToken.Text = profile.SessionToken;
            SelectChoice(_credentialSource, profile.CredentialSource);
            _awsProfileName.Text = profile.AwsProfileName;
            _awsSourceProfileName.Text = profile.AwsSourceProfileName;
            _awsRoleArn.Text = profile.AwsRoleArn;
            _awsRoleSessionName.Text = profile.AwsRoleSessionName;
            _awsRoleSourceIdentity.Text = profile.AwsRoleSourceIdentity;
            _awsExternalId.Text = profile.AwsExternalId;
            _awsSessionDuration.Value = Math.Clamp(profile.AwsSessionDurationSeconds, 900, 43200);
            _awsWebIdentityTokenFile.Text = profile.AwsWebIdentityTokenFile;
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
        _activeServiceType = profile.ServiceType;
        _connectionDrafts[profile.ServiceType] = CaptureConnectionDraft();
        ApplyAccountSelection(applyPreset: false);
        UpdateCredentialSourceVisibility();
        UpdateSessionTokenVisibility();
    }

    private void ApplyAccountSelection(bool applyPreset)
    {
        if (_loading || _applyingSelection) return;

        _applyingSelection = true;
        try
        {
            if (applyPreset && _activeServiceType is { } previousServiceType)
                _connectionDrafts[previousServiceType] = CaptureConnectionDraft();

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
            var regionVisible = definition.RegionInput != RegionInputMode.Hidden;
            _regionLabel.Visible = _region.Visible = regionVisible;
            _regionLabel.Text = definition.RegionInput == RegionInputMode.Optional
                ? "签名 Region（可选）："
                : "Region：";
            _regionHint.Text = definition.RegionInput switch
            {
                RegionInputMode.Hidden => $"{definition.DisplayName} 不需要手动设置 Region；程序会自动使用签名值 {definition.EffectiveDefaultSigningRegion}。",
                RegionInputMode.Optional => $"默认 auto；仅在服务商明确要求时填写 SigV4 Region，auto 会使用安全签名值 {definition.EffectiveDefaultSigningRegion}。",
                _ when serviceType == S3ServiceType.AmazonS3 => "默认 auto；连接使用全局 Endpoint，并以 us-east-1 签名。需要固定区域时再选择具体 Region。",
                _ => "Region 同时用于服务区域与 SigV4 签名。"
            };

            if (serviceType != S3ServiceType.AmazonS3)
                SelectChoice(_credentialSource, CredentialSourceKind.StoredKeys);
            _credentialSource.Enabled = serviceType == S3ServiceType.AmazonS3;

            if (applyPreset && _activeServiceType != serviceType)
            {
                if (_connectionDrafts.TryGetValue(serviceType, out var draft))
                    ApplyConnectionDraft(draft);
                else
                    ApplyConnectionPreset(serviceType);
                ClearConnectionTestResult();
            }
            else if (!endpointVisible)
            {
                _https.Checked = true;
            }

            _activeServiceType = serviceType;
            UpdateCredentialSourceVisibility();
        }
        finally
        {
            _applyingSelection = false;
        }
    }

    private void ApplyConnectionPreset(S3ServiceType serviceType)
    {
        var preset = ConnectionProfile.CreatePreset(serviceType);
        _endpoint.Text = preset.Endpoint;
        _region.Text = preset.Region;
        _https.Checked = preset.UseHttps;
        _path.Checked = preset.AddressingStyle == AddressingStyle.PathStyle;
        _virtual.Checked = preset.AddressingStyle == AddressingStyle.VirtualHosted;
        _auto.Checked = preset.AddressingStyle == AddressingStyle.Auto;
    }

    private ConnectionDraft CaptureConnectionDraft() => new(
        _endpoint.Text,
        _region.Text,
        _https.Checked,
        _path.Checked ? AddressingStyle.PathStyle :
            _virtual.Checked ? AddressingStyle.VirtualHosted : AddressingStyle.Auto);

    private void ApplyConnectionDraft(ConnectionDraft draft)
    {
        _endpoint.Text = draft.Endpoint;
        _region.Text = draft.Region;
        _https.Checked = draft.UseHttps;
        _path.Checked = draft.AddressingStyle == AddressingStyle.PathStyle;
        _virtual.Checked = draft.AddressingStyle == AddressingStyle.VirtualHosted;
        _auto.Checked = draft.AddressingStyle == AddressingStyle.Auto;
    }

    private void UpdateSessionTokenVisibility()
    {
        var storedKeysVisible = _useSessionToken.Visible;
        var visible = storedKeysVisible && _useSessionToken.Checked;
        _sessionTokenLabel.Visible = _sessionToken.Visible = visible;
        if (storedKeysVisible && !visible) _sessionToken.Text = string.Empty;
    }

    private void UpdateCredentialSourceVisibility()
    {
        var serviceType = SelectedServiceType();
        var source = serviceType == S3ServiceType.AmazonS3
            ? SelectedValue(_credentialSource, CredentialSourceKind.StoredKeys)
            : CredentialSourceKind.StoredKeys;
        var storedKeys = source == CredentialSourceKind.StoredKeys;
        var sharedProfile = source is CredentialSourceKind.AwsSharedProfile or CredentialSourceKind.AwsSso;
        var assumeRole = source == CredentialSourceKind.AwsAssumeRole;
        var webIdentity = source == CredentialSourceKind.AwsWebIdentity;
        var roleSession = assumeRole || webIdentity;

        _awsProfileLabel.Visible = _awsProfileName.Visible = sharedProfile;
        _awsSourceProfileLabel.Visible = _awsSourceProfileName.Visible = assumeRole;
        _awsRoleArnLabel.Visible = _awsRoleArn.Visible = roleSession;
        _awsRoleSessionNameLabel.Visible = _awsRoleSessionName.Visible = roleSession;
        _awsRoleSourceIdentityLabel.Visible = _awsRoleSourceIdentity.Visible = assumeRole;
        _awsExternalIdLabel.Visible = _awsExternalId.Visible = assumeRole;
        _awsSessionDurationLabel.Visible = _awsSessionDuration.Visible = roleSession;
        _awsWebIdentityTokenFileLabel.Visible = _awsWebIdentityTokenFile.Visible = webIdentity;
        _accessKeyLabel.Visible = _accessKey.Visible = storedKeys;
        _secretKeyLabel.Visible = _secretPanel.Visible = storedKeys;
        _useSessionToken.Visible = storedKeys;

        _credentialHint.Text = source switch
        {
            CredentialSourceKind.StoredKeys => "Secret Key 与 Session Token 使用 Windows DPAPI CurrentUser 加密保存。",
            CredentialSourceKind.AwsSharedProfile => "只保存 Profile 名称；凭据从 ~/.aws/credentials 与 ~/.aws/config 读取。",
            CredentialSourceKind.AwsEnvironmentVariables => "读取 AWS_ACCESS_KEY_ID、AWS_SECRET_ACCESS_KEY 和可选 AWS_SESSION_TOKEN，不写入磁盘。",
            CredentialSourceKind.AwsContainerRole => "锁定容器凭据端点；缺少 AWS_CONTAINER_CREDENTIALS_* 时会直接报错，不回退到其他身份。",
            CredentialSourceKind.AwsInstanceRole => "锁定 EC2 Instance Metadata 角色；不会回退到本机 Profile。",
            CredentialSourceKind.AwsDefaultChain => "按 AWS SDK 顺序解析并在连接测试中显示实际来源；需要固定身份时请选择上面的明确来源。",
            CredentialSourceKind.AwsSso => "只保存 SSO Profile 名称；测试连接可由用户触发登录，浏览器令牌由 AWS SDK 独立缓存且不会进入连接包。",
            CredentialSourceKind.AwsAssumeRole => "源 Profile 与角色配置分开保存；External ID 使用 DPAPI 加密，仅显示是否已配置。",
            CredentialSourceKind.AwsWebIdentity => "只保存 Token 文件的绝对路径；令牌内容由 AWS SDK 按需读取，不写入连接配置或日志。",
            _ => string.Empty
        };
        UpdateSessionTokenVisibility();
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
        var region = definition.RegionInput == RegionInputMode.Hidden
            ? definition.DefaultRegion
            : _region.Text.Trim();
        if (region.Length == 0)
            region = definition.DefaultRegion;
        var signingRegion = S3ProviderCatalog.ResolveSigningRegion(serviceType, region);
        var endpoint = category == S3AccountCategory.S3Compatible
            ? _endpoint.Text.Trim()
            : definition.DefaultEndpoint;
        var externalBuckets = _externalBuckets.Lines
            .Select(bucket => bucket.Trim())
            .Where(bucket => bucket.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var credentialSource = serviceType == S3ServiceType.AmazonS3
            ? SelectedValue(_credentialSource, CredentialSourceKind.StoredKeys)
            : CredentialSourceKind.StoredKeys;
        var storedKeys = credentialSource == CredentialSourceKind.StoredKeys;

        return new ConnectionProfile
        {
            Id = _id,
            GroupId = Profile.GroupId,
            SortOrder = Profile.SortOrder,
            Name = _name.Text.Trim(),
            ServiceType = serviceType,
            Endpoint = endpoint,
            Region = region,
            SignatureRegion = signingRegion,
            AccessKey = storedKeys ? _accessKey.Text.Trim() : string.Empty,
            SecretKey = storedKeys ? _secretKey.Text : string.Empty,
            SessionToken = storedKeys && _useSessionToken.Checked ? _sessionToken.Text : string.Empty,
            CredentialSource = credentialSource,
            AwsProfileName = credentialSource is CredentialSourceKind.AwsSharedProfile or CredentialSourceKind.AwsSso
                ? _awsProfileName.Text.Trim()
                : string.Empty,
            AwsSourceProfileName = credentialSource == CredentialSourceKind.AwsAssumeRole
                ? _awsSourceProfileName.Text.Trim()
                : string.Empty,
            AwsRoleArn = credentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                ? _awsRoleArn.Text.Trim()
                : string.Empty,
            AwsRoleSessionName = credentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                ? _awsRoleSessionName.Text.Trim()
                : string.Empty,
            AwsRoleSourceIdentity = credentialSource == CredentialSourceKind.AwsAssumeRole
                ? _awsRoleSourceIdentity.Text.Trim()
                : string.Empty,
            AwsExternalId = credentialSource == CredentialSourceKind.AwsAssumeRole ? _awsExternalId.Text : string.Empty,
            AwsSessionDurationSeconds = credentialSource is CredentialSourceKind.AwsAssumeRole or CredentialSourceKind.AwsWebIdentity
                ? (int)_awsSessionDuration.Value
                : 3600,
            AwsWebIdentityTokenFile = credentialSource == CredentialSourceKind.AwsWebIdentity
                ? _awsWebIdentityTokenFile.Text.Trim()
                : string.Empty,
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
            ShowConnectionTestResult(
                $"正在连接（最多 {profile.ConnectionTimeoutSeconds} 秒）...",
                SystemColors.ControlText);
            var result = await _storage.TestConnectionAsync(profile, CancellationToken.None);
            ShowConnectionTestResult(
                ConnectionTestResultFormatter.Format(result, profile),
                result.Success ? Color.DarkGreen : Color.DarkRed);
        }
        catch (Exception exception)
        {
            ShowConnectionTestResult($"连接失败：{exception.Message}", Color.DarkRed);
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private void ShowConnectionTestResult(string text, Color color)
    {
        _result.ForeColor = color;
        _result.Text = text;
        _result.Visible = true;
        _result.SelectionStart = 0;
        _result.SelectionLength = 0;
        PerformLayout();
    }

    private void ClearConnectionTestResult()
    {
        _result.Clear();
        _result.Visible = false;
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

    private S3ServiceType SelectedServiceType()
    {
        var category = SelectedValue(_accountType, S3AccountCategory.AmazonS3);
        return category == S3AccountCategory.S3Compatible
            ? SelectedValue(_provider, S3ServiceType.Custom)
            : S3ProviderCatalog.DefaultServiceType(category);
    }
}

internal static class ConnectionTestResultFormatter
{
    public static string Format(ConnectionTestResult result, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(profile);

        if (result.Success)
        {
            var identity = FormatIdentity(result.AwsIdentity);
            return $"{result.Message}{Environment.NewLine}" +
                $"凭据：{result.CredentialSource ?? profile.CredentialSourceDisplayName}；" +
                $"Bucket：{result.BucketCount:N0}；耗时：{result.Elapsed.TotalMilliseconds:N0} ms" +
                (identity.Length == 0 ? string.Empty : $"{Environment.NewLine}{identity}");
        }

        return $"连接失败：{result.Message}{Environment.NewLine}" +
            $"错误代码：{result.ErrorCode ?? "—"}；HTTP：{result.HttpStatusCode?.ToString() ?? "—"}；" +
            $"Request ID：{result.RequestId ?? "—"}；耗时：{result.Elapsed.TotalMilliseconds:N0} ms";
    }

    private static string FormatIdentity(AwsIdentitySummary? identity)
    {
        if (identity is null) return string.Empty;
        var parts = new List<string> { $"源身份：{identity.SourceIdentity}" };
        if (!string.IsNullOrWhiteSpace(identity.TargetRoleArn))
            parts.Add($"目标 Role：{identity.TargetRoleArn}");
        if (identity.Source == CredentialSourceKind.AwsAssumeRole)
            parts.Add($"External ID：{(identity.ExternalIdConfigured ? "已配置" : "未配置")}");
        if (identity.SessionExpiresAtUtc is { } expiration)
            parts.Add($"会话到期：{expiration.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        if (identity.UserLoginMayBeRequired)
            parts.Add("需要用户触发 SSO 登录或刷新");
        return string.Join("；", parts);
    }
}
