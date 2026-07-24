using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class ConnectionDialog : Form
{
    private readonly IS3StorageService _storage;
    private readonly TextBox _name = new();
    private readonly ComboBox _service = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _endpoint = new();
    private readonly ComboBox _region = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _signatureRegion = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _accessKey = new();
    private readonly TextBox _secretKey = new() { UseSystemPasswordChar = true };
    private readonly TextBox _sessionToken = new() { UseSystemPasswordChar = true };
    private readonly RadioButton _auto = new() { Text = "自动", Checked = true, AutoSize = true };
    private readonly RadioButton _virtual = new() { Text = "Virtual Hosted Style", AutoSize = true };
    private readonly RadioButton _path = new() { Text = "Path Style", AutoSize = true };
    private readonly CheckBox _https = new() { Text = "使用 HTTPS", Checked = true, AutoSize = true };
    private readonly CheckBox _ignoreCert = new() { Text = "忽略证书错误（不安全）", AutoSize = true, ForeColor = Color.DarkRed };
    private readonly TextBox _hostHeader = new();
    private readonly CheckBox _followRedirects = new() { Text = "处理临时重定向", Checked = true, AutoSize = true };
    private readonly CheckBox _multiDelete = new() { Text = "启用 Multi-Object Delete", Checked = true, AutoSize = true };
    private readonly CheckBox _multipartCopy = new() { Text = "启用 Multipart Copy", Checked = true, AutoSize = true };
    private readonly ComboBox _storageClass = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _timeout = new() { Minimum = 5, Maximum = 3600, Value = 100 };
    private readonly Button _test = new() { Text = "测试连接", AutoSize = true };
    private readonly Label _result = new() { AutoSize = true };
    private readonly Button _save = new() { Text = "保存", DialogResult = DialogResult.OK, Width = 85 };
    private readonly Button _cancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, Width = 85 };
    private readonly Guid _id;

    public ConnectionProfile Profile { get; private set; }

    public ConnectionDialog(IS3StorageService storage, ConnectionProfile? profile = null)
    {
        _storage = storage;
        Profile = profile ?? ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3);
        _id = Profile.Id;

        Text = profile is null ? "新建 S3 连接" : "编辑 S3 连接";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 730);
        MinimumSize = new Size(680, 650);
        ShowInTaskbar = false;

        _service.Items.AddRange(Enum.GetValues<S3ServiceType>().Cast<object>().ToArray());
        _storageClass.Items.AddRange(["STANDARD", "STANDARD_IA", "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER", "DEEP_ARCHIVE"]);
        _region.Items.AddRange(["us-east-1", "us-west-1", "us-west-2", "eu-west-1", "eu-central-1", "ap-southeast-1", "ap-northeast-1", "auto"]);
        _signatureRegion.Items.AddRange(["us-east-1", "us-west-1", "us-west-2", "eu-west-1", "eu-central-1", "ap-southeast-1", "ap-northeast-1", "auto"]);
        BuildLayout();
        LoadProfile(Profile);

        _service.SelectedIndexChanged += (_, _) =>
        {
            if (!Visible || _service.SelectedItem is not S3ServiceType type)
                return;
            var preset = ConnectionProfile.CreatePreset(type);
            _endpoint.Text = preset.Endpoint;
            _region.Text = preset.Region;
            _signatureRegion.Text = preset.SignatureRegion;
            _https.Checked = preset.UseHttps;
            _path.Checked = preset.AddressingStyle == AddressingStyle.PathStyle;
            _auto.Checked = preset.AddressingStyle == AddressingStyle.Auto;
        };
        _test.Click += async (_, _) => await TestConnectionAsync();
        _save.Click += (_, args) =>
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
        };
    }

    private void BuildLayout()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            AutoScroll = true
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        AddField(table, ref row, "连接名称：", _name);
        AddField(table, ref row, "服务类型：", _service);
        AddField(table, ref row, "Endpoint：", _endpoint);
        AddField(table, ref row, "Region：", _region);
        AddField(table, ref row, "签名 Region：", _signatureRegion);
        AddField(table, ref row, "Access Key：", _accessKey);

        var secretPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 28 };
        secretPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        secretPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        secretPanel.Controls.Add(_secretKey, 0, 0);
        _secretKey.Dock = DockStyle.Fill;
        var show = new CheckBox { Text = "显示", AutoSize = true };
        show.CheckedChanged += (_, _) => _secretKey.UseSystemPasswordChar = !show.Checked;
        secretPanel.Controls.Add(show, 1, 0);
        AddField(table, ref row, "Secret Key：", secretPanel);

        AddField(table, ref row, "Session Token：", _sessionToken);
        var style = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        style.Controls.AddRange([_auto, _virtual, _path]);
        AddField(table, ref row, "地址风格：", style);
        AddField(table, ref row, "自定义 Host Header：", _hostHeader);

        var security = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        security.Controls.AddRange([_https, _ignoreCert]);
        AddField(table, ref row, "连接选项：", security);
        var compatibility = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        compatibility.Controls.AddRange([_followRedirects, _multiDelete, _multipartCopy]);
        AddField(table, ref row, "兼容性开关：", compatibility);
        AddField(table, ref row, "默认存储类型：", _storageClass);
        AddField(table, ref row, "请求超时（秒）：", _timeout);

        var testPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        testPanel.Controls.AddRange([_test, _result]);
        table.Controls.Add(testPanel, 0, row);
        table.SetColumnSpan(testPanel, 2);
        row++;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.AddRange([_cancel, _save]);
        table.Controls.Add(buttons, 0, row);
        table.SetColumnSpan(buttons, 2);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(table);
        AcceptButton = _save;
        CancelButton = _cancel;
    }

    private static void AddField(TableLayoutPanel table, ref int row, string label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(control, 1, row);
        row++;
    }

    private void LoadProfile(ConnectionProfile profile)
    {
        _name.Text = profile.Name;
        _service.SelectedItem = profile.ServiceType;
        _endpoint.Text = profile.Endpoint;
        _region.Text = profile.Region;
        _signatureRegion.Text = profile.SignatureRegion;
        _accessKey.Text = profile.AccessKey;
        _secretKey.Text = profile.SecretKey;
        _sessionToken.Text = profile.SessionToken;
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
        _timeout.Value = Math.Clamp(profile.RequestTimeoutSeconds, 5, 3600);
    }

    private ConnectionProfile ReadProfile() => new()
    {
        Id = _id,
        Name = _name.Text.Trim(),
        ServiceType = (S3ServiceType)(_service.SelectedItem ?? S3ServiceType.Custom),
        Endpoint = _endpoint.Text.Trim(),
        Region = _region.Text.Trim(),
        SignatureRegion = _signatureRegion.Text.Trim(),
        AccessKey = _accessKey.Text.Trim(),
        SecretKey = _secretKey.Text,
        SessionToken = _sessionToken.Text,
        AddressingStyle = _path.Checked ? AddressingStyle.PathStyle : _virtual.Checked ? AddressingStyle.VirtualHosted : AddressingStyle.Auto,
        UseHttps = _https.Checked,
        IgnoreCertificateErrors = _ignoreCert.Checked,
        CustomHostHeader = _hostHeader.Text.Trim(),
        FollowTemporaryRedirects = _followRedirects.Checked,
        EnableMultiObjectDelete = _multiDelete.Checked,
        EnableMultipartCopy = _multipartCopy.Checked,
        DefaultStorageClass = _storageClass.Text,
        RequestTimeoutSeconds = (int)_timeout.Value
    };

    private async Task TestConnectionAsync()
    {
        try
        {
            var profile = ReadProfile();
            profile.Validate();
            if (!ConfirmInsecureTls(profile))
                return;
            _test.Enabled = false;
            _result.ForeColor = SystemColors.ControlText;
            _result.Text = "正在连接...";
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
        if (!profile.IgnoreCertificateErrors)
            return true;

        return MessageBox.Show(
            this,
            "TLS 证书验证旁路会使连接容易遭受中间人攻击。\n\n仅应在受控测试环境中启用。是否继续？",
            "不安全的 TLS 设置",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
