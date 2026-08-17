using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class DiscardCdnChangesDialog : Form
{
    public DiscardCdnChangesDialog()
    {
        Name = "DiscardCdnChangesDialog";
        Text = "放弃未保存的更改";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 210);
        MinimumSize = new Size(480, 200);
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = UiIcons.CreateApplicationIcon();

        var message = new Label
        {
            Name = "DiscardCdnChangesMessage",
            Text = "当前有未保存的 CDN 配置更改。关闭后，新增、编辑、复制和删除的更改都会丢失。\n\n确定放弃更改吗？",
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 28, 28, 12),
            AutoSize = false
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 8)
        };
        var no = new Button
        {
            Name = "KeepCdnChangesButton",
            Text = "否(&N)",
            DialogResult = DialogResult.No,
            MinimumSize = new Size(96, 36),
            AutoSize = true
        };
        var yes = new Button
        {
            Name = "DiscardCdnChangesButton",
            Text = "是(&Y)",
            DialogResult = DialogResult.Yes,
            MinimumSize = new Size(96, 36),
            AutoSize = true
        };
        buttons.Controls.AddRange([no, yes]);
        Controls.Add(message);
        Controls.Add(buttons);
        AcceptButton = no;
        CancelButton = no;
    }
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
    private readonly IReadOnlyList<CredentialProfile> _credentials;
    private readonly TextBox _name = new() { Name = "CdnProfileName" };
    private readonly ComboBox _provider = new()
    {
        Name = "CdnProfileProvider",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
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

    public CdnProfileEditorDialog(
        CdnProfile? profile,
        IReadOnlyList<CredentialProfile> credentials,
        bool copying = false)
    {
        Profile = profile ?? new CdnProfile();
        _id = Profile.Id;
        _credentials = credentials;
        Name = "CdnProfileEditorDialog";
        Text = profile is null ? "新增 CDN 配置" : copying ? "复制 CDN 配置" : "编辑 CDN 配置";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 680);
        MinimumSize = new Size(640, 580);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Icon = UiIcons.CreateApplicationIcon();

        _provider.Items.AddRange([
            new Choice<string>(CdnProfile.GenericHttpProviderId, "通用 HTTP"),
            new Choice<string>(CdnProfile.AlibabaCloudProviderId, "阿里云 CDN")
        ]);
        _warmupMode.Items.AddRange([
            new Choice<CdnWarmupMode>(CdnWarmupMode.Head, "HEAD（轻量，但部分 CDN 与 GET 行为不同）"),
            new Choice<CdnWarmupMode>(CdnWarmupMode.RangeGet, "Range GET（推荐）"),
            new Choice<CdnWarmupMode>(CdnWarmupMode.FullGet, "完整 GET")
        ]);
        _purgeMethod.Items.AddRange(["GET", "POST", "PUT", "PATCH", "DELETE"]);

        var fields = EditorLayout.Fields();
        EditorLayout.AddField(fields, "名称：", _name);
        EditorLayout.AddField(fields, "Provider：", _provider);
        EditorLayout.AddField(fields, "CDN 基础 URL：", _baseUrl);
        EditorLayout.AddField(fields, "备注：", _notes);
        EditorLayout.AddField(fields, "关联凭据：", _credential);
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
        _save.Text = "确定";
        _save.Click += (_, _) => Save();
        _provider.SelectedIndexChanged += (_, _) =>
        {
            PopulateCredentials(Selected(_provider, CdnProfile.GenericHttpProviderId), null);
            UpdateProviderFields();
        };
        _warmupMode.SelectedIndexChanged += (_, _) =>
            _rangeMiB.Enabled = Selected(_warmupMode, CdnWarmupMode.RangeGet) == CdnWarmupMode.RangeGet;
        AcceptButton = _save;
        CancelButton = _cancel;
        LoadProfile(Profile);
    }

    private void LoadProfile(CdnProfile profile)
    {
        _name.Text = profile.Name;
        SelectValue(_provider, profile.ProviderId);
        PopulateCredentials(profile.ProviderId, profile.CredentialId);
        _baseUrl.Text = profile.BaseUrl;
        _notes.Text = profile.Notes;
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
        UpdateProviderFields();
    }

    private void Save()
    {
        var providerId = Selected(_provider, CdnProfile.GenericHttpProviderId);
        var genericHttpProvider = string.Equals(
            providerId,
            CdnProfile.GenericHttpProviderId,
            StringComparison.OrdinalIgnoreCase);
        var candidate = new CdnProfile
        {
            Id = _id,
            Name = _name.Text.Trim(),
            Notes = _notes.Text.Trim(),
            ProviderId = providerId,
            BaseUrl = _baseUrl.Text.Trim(),
            CredentialId = Selected(_credential, (Guid?)null),
            WarmupMode = Selected(_warmupMode, CdnWarmupMode.RangeGet),
            WarmupRangeBytes = decimal.ToInt64(_rangeMiB.Value) * 1024L * 1024L,
            PurgeEndpointTemplate = genericHttpProvider ? _purgeEndpoint.Text.Trim() : string.Empty,
            PurgeHttpMethod = genericHttpProvider ? _purgeMethod.SelectedItem?.ToString() ?? "POST" : "POST",
            PurgeBodyTemplate = genericHttpProvider ? _purgeBody.Text : string.Empty,
            PurgeContentType = !genericHttpProvider || string.IsNullOrWhiteSpace(_purgeContentType.Text)
                ? "application/json"
                : _purgeContentType.Text.Trim(),
            TimeoutSeconds = decimal.ToInt32(_timeout.Value),
            FollowRedirects = _followRedirects.Checked,
            Enabled = _enabled.Checked,
            LastCertificateCheck = string.Equals(
                Profile.BaseUrl.TrimEnd('/'),
                _baseUrl.Text.Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase)
                ? Profile.LastCertificateCheck
                : null
        };
        var errors = CdnConfigurationValidator.Validate(new CdnConfiguration([candidate], []), _credentials);
        var selectedCredential = candidate.CredentialId is Guid credentialId
            ? _credentials.FirstOrDefault(value => value.Id == credentialId)
            : null;
        if (string.Equals(candidate.ProviderId, CdnProfile.AlibabaCloudProviderId, StringComparison.OrdinalIgnoreCase) &&
            selectedCredential is null)
            errors = errors.Append("阿里云 CDN 必须选择 Alibaba Cloud AccessKey 凭据。").ToArray();
        else if (selectedCredential is not null && !selectedCredential.IsCompatibleWith(candidate.ProviderId))
            errors = errors.Append("所选凭据与 CDN Provider 不兼容。").ToArray();
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

    private void PopulateCredentials(string providerId, Guid? selectedId)
    {
        _credential.Items.Clear();
        if (string.Equals(providerId, CdnProfile.GenericHttpProviderId, StringComparison.OrdinalIgnoreCase))
            _credential.Items.Add(new Choice<Guid?>(null, "(无需认证)"));
        foreach (var item in _credentials
                     .Where(value => value.IsCompatibleWith(providerId))
                     .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            _credential.Items.Add(new Choice<Guid?>(item.Id, $"{item.Name} · {item.Fingerprint}"));
        SelectValue(_credential, selectedId);
    }

    private void UpdateProviderFields()
    {
        var generic = string.Equals(
            Selected(_provider, CdnProfile.GenericHttpProviderId),
            CdnProfile.GenericHttpProviderId,
            StringComparison.OrdinalIgnoreCase);
        _purgeEndpoint.Enabled = generic;
        _purgeMethod.Enabled = generic;
        _purgeBody.Enabled = generic;
        _purgeContentType.Enabled = generic;
    }
}

internal sealed class CredentialEditorDialog : Form
{
    private sealed record Choice<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly Guid _id;
    private readonly TextBox _name = new() { Name = "CredentialName" };
    private readonly ComboBox _provider = new()
    {
        Name = "CredentialProvider",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox _type = new()
    {
        Name = "CdnCredentialType",
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _accessKeyId = new() { Name = "CredentialAccessKeyId" };
    private readonly TextBox _header = new() { Name = "CdnCredentialHeader" };
    private readonly TextBox _secret = new()
    {
        Name = "CredentialSecret",
        UseSystemPasswordChar = true
    };
    private readonly TextBox _sessionToken = new()
    {
        Name = "CredentialSessionToken",
        UseSystemPasswordChar = true
    };
    private readonly CheckBox _showSecret = new()
    {
        Name = "ShowCredentialSecret",
        Text = "显示秘密值",
        AutoSize = true
    };
    private readonly Button _save = EditorLayout.SaveButton("SaveCredentialButton");
    private readonly Button _cancel = EditorLayout.CancelButton("CancelCredentialButton");

    public CredentialProfile Credential { get; private set; }

    public CredentialEditorDialog(
        CredentialProfile? credential,
        CredentialProviderKind? initialProvider = null,
        CredentialKind? initialKind = null)
    {
        var initialProviderValue = initialProvider ?? CredentialProviderKind.GenericHttp;
        var kind = initialKind ?? (initialProviderValue == CredentialProviderKind.GenericHttp
            ? CredentialKind.BearerToken
            : CredentialKind.AccessKeyPair);
        Credential = credential ?? new CredentialProfile { Provider = initialProviderValue, Kind = kind };
        _id = Credential.Id;
        Name = "CredentialEditorDialog";
        Text = credential is null ? "新增统一凭据" : "编辑统一凭据";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 380);
        MinimumSize = new Size(560, 340);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Icon = UiIcons.CreateApplicationIcon();

        foreach (var provider in Enum.GetValues<CredentialProviderKind>())
            _provider.Items.Add(new Choice<CredentialProviderKind>(provider, ProviderText(provider)));

        var fields = EditorLayout.Fields();
        EditorLayout.AddField(fields, "名称：", _name);
        EditorLayout.AddField(fields, "提供方：", _provider);
        EditorLayout.AddField(fields, "凭据类型：", _type);
        EditorLayout.AddField(fields, "Access Key ID：", _accessKeyId);
        EditorLayout.AddField(fields, "Header 名称：", _header);
        EditorLayout.AddField(fields, "秘密值：", _secret);
        EditorLayout.AddField(fields, "Session Token：", _sessionToken);
        EditorLayout.AddWide(fields, _showSecret);
        EditorLayout.AddWide(fields, new Label
        {
            Text = "对象存储与 CDN 共用同一个类型化凭据目录；配置只保存凭据引用，统一配置载荷使用 Windows DPAPI CurrentUser 加密。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(520, 0)
        });
        Controls.Add(EditorLayout.Root(fields, _save, _cancel));
        _save.Text = "确定";

        _name.Text = Credential.Name;
        _accessKeyId.Text = Credential.AccessKeyId;
        _header.Text = Credential.HeaderName;
        _secret.Text = Credential.Secret;
        _sessionToken.Text = Credential.SessionToken;
        SelectChoice(_provider, Credential.Provider);
        PopulateKinds(Credential.Kind);
        _provider.SelectedIndexChanged += (_, _) => PopulateKinds(null);
        _type.SelectedIndexChanged += (_, _) => UpdateState();
        _showSecret.CheckedChanged += (_, _) =>
        {
            _secret.UseSystemPasswordChar = !_showSecret.Checked;
            _sessionToken.UseSystemPasswordChar = !_showSecret.Checked;
        };
        _save.Click += (_, _) => Save();
        AcceptButton = _save;
        CancelButton = _cancel;
        UpdateState();
    }

    private void UpdateState()
    {
        var type = _type.SelectedItem is Choice<CredentialKind> choice
            ? choice.Value
            : CredentialKind.BearerToken;
        _accessKeyId.Enabled = type == CredentialKind.AccessKeyPair;
        _header.Enabled = type == CredentialKind.CustomHeader;
        _sessionToken.Enabled = type == CredentialKind.AccessKeyPair;
        _secret.Enabled = true;
    }

    private void Save()
    {
        var type = _type.SelectedItem is Choice<CredentialKind> choice
            ? choice.Value
            : CredentialKind.BearerToken;
        var provider = _provider.SelectedItem is Choice<CredentialProviderKind> providerChoice
            ? providerChoice.Value
            : CredentialProviderKind.GenericHttp;
        var candidate = new CredentialProfile
        {
            Id = _id,
            Name = _name.Text.Trim(),
            Provider = provider,
            Kind = type,
            AccessKeyId = type == CredentialKind.AccessKeyPair ? _accessKeyId.Text.Trim() : string.Empty,
            HeaderName = type == CredentialKind.CustomHeader ? _header.Text.Trim() : string.Empty,
            Secret = _secret.Text,
            SessionToken = type == CredentialKind.AccessKeyPair ? _sessionToken.Text : string.Empty
        };
        try
        {
            candidate.Validate();
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "凭据无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Credential = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void PopulateKinds(CredentialKind? selected)
    {
        var provider = _provider.SelectedItem is Choice<CredentialProviderKind> choice
            ? choice.Value
            : CredentialProviderKind.GenericHttp;
        CredentialKind[] kinds = provider switch
        {
            CredentialProviderKind.GenericHttp =>
                [CredentialKind.BearerToken, CredentialKind.CustomHeader],
            CredentialProviderKind.AmazonWebServices =>
                [CredentialKind.AccessKeyPair, CredentialKind.SecretValue],
            _ => [CredentialKind.AccessKeyPair]
        };
        _type.Items.Clear();
        foreach (var kind in kinds)
            _type.Items.Add(new Choice<CredentialKind>(kind, KindText(kind)));
        SelectChoice(_type, selected is CredentialKind value && kinds.Contains(value) ? value : kinds[0]);
        UpdateState();
    }

    private static void SelectChoice<T>(ComboBox combo, T value)
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

    private static string KindText(CredentialKind kind) => kind switch
    {
        CredentialKind.AccessKeyPair => "Access Key / Secret Key",
        CredentialKind.BearerToken => "Bearer Token",
        CredentialKind.CustomHeader => "自定义 Header",
        CredentialKind.SecretValue => "秘密值",
        _ => kind.ToString()
    };

    private static string ProviderText(CredentialProviderKind provider) => provider switch
    {
        CredentialProviderKind.S3Compatible => "S3 Compatible",
        CredentialProviderKind.AmazonWebServices => "Amazon Web Services",
        CredentialProviderKind.AlibabaCloud => "Alibaba Cloud",
        CredentialProviderKind.TencentCloud => "Tencent Cloud",
        CredentialProviderKind.Cloudflare => "Cloudflare",
        CredentialProviderKind.Backblaze => "Backblaze",
        CredentialProviderKind.GoogleCloud => "Google Cloud",
        CredentialProviderKind.Supabase => "Supabase",
        CredentialProviderKind.GenericHttp => "通用 HTTP",
        _ => provider.ToString()
    };
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
        string? initialBucket,
        bool copying = false)
    {
        Binding = binding ?? new CdnBinding();
        _id = Binding.Id;
        Name = "CdnBindingEditorDialog";
        Text = binding is null ? "新增 CDN 关联" : copying ? "复制 CDN 关联" : "编辑 CDN 关联";
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
        _save.Text = "确定";
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
