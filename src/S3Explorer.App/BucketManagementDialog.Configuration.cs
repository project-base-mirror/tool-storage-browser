using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class BucketManagementDialog
{
    private readonly TabControl _corsViews = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _corsGrid = ConfigurationGrid();
    private readonly TextBox _corsJson = JsonTextBox();
    private readonly Label _corsReason = ReasonLabel();
    private readonly Button _corsReload = NamedButton("ReloadBucketCorsButton", "重新读取");
    private readonly Button _corsValidate = NamedButton("ValidateBucketCorsButton", "校验 / 格式化");
    private readonly Button _corsSave = NamedButton("SaveBucketCorsButton", "保存 CORS");
    private readonly Button _corsDelete = NamedButton("DeleteBucketCorsButton", "删除 CORS");
    private readonly Label _versioningReason = ReasonLabel();
    private readonly ComboBox _versioningMode = SelectionBox();
    private readonly Button _versioningReload = NamedButton("ReloadBucketVersioningButton", "重新读取");
    private readonly Button _versioningSave = NamedButton("SaveBucketVersioningButton", "应用版本状态");
    private readonly Label _encryptionReason = ReasonLabel();
    private readonly ComboBox _encryptionMode = SelectionBox();
    private readonly TextBox _kmsKeyId = new() { Width = 560 };
    private readonly Button _encryptionReload = NamedButton("ReloadBucketEncryptionButton", "重新读取");
    private readonly Button _encryptionSave = NamedButton("SaveBucketEncryptionButton", "保存默认加密");
    private readonly Button _encryptionDelete = NamedButton("DeleteBucketEncryptionButton", "删除默认加密");
    private readonly Label _tagsReason = ReasonLabel();
    private readonly DataGridView _tagsGrid = ConfigurationGrid();
    private readonly Button _tagsReload = NamedButton("ReloadBucketTagsButton", "重新读取");
    private readonly Button _tagsSave = NamedButton("SaveBucketTagsButton", "保存 Tags");
    private readonly Button _tagsDelete = NamedButton("DeleteBucketTagsButton", "删除 Tags");
    private BucketCorsConfiguration _currentCors = new([]);
    private BucketVersioningState _currentVersioning;
    private BucketEncryptionConfiguration _currentEncryption = new(BucketEncryptionMode.None);
    private IReadOnlyList<BucketTag> _currentTags = [];
    private bool _corsLoaded;
    private bool _versioningLoaded;
    private bool _encryptionLoaded;
    private bool _tagsLoaded;

    private TabPage BuildCorsTab()
    {
        _corsGrid.Columns.Add("Id", "规则 ID");
        _corsGrid.Columns.Add("Origins", "允许来源（逗号分隔）");
        _corsGrid.Columns.Add("Methods", "方法");
        _corsGrid.Columns.Add("AllowedHeaders", "允许 Header");
        _corsGrid.Columns.Add("ExposeHeaders", "暴露 Header");
        _corsGrid.Columns.Add("MaxAge", "Max Age（秒）");
        _corsGrid.Columns[0].Width = 105;
        _corsGrid.Columns[1].Width = 190;
        _corsGrid.Columns[2].Width = 120;
        _corsGrid.Columns[3].Width = 145;
        _corsGrid.Columns[4].Width = 145;
        _corsGrid.Columns[5].Width = 110;
        var form = new TabPage("表格") { Padding = new Padding(4) };
        form.Controls.Add(_corsGrid);
        var json = new TabPage("JSON") { Padding = new Padding(4) };
        json.Controls.Add(_corsJson);
        _corsViews.TabPages.AddRange([form, json]);
        _corsViews.SelectedIndexChanged += (_, _) => SynchronizeCorsEditor(showErrors: true);
        _corsReload.Click += async (_, _) => await LoadCorsAsync(force: true);
        _corsValidate.Click += (_, _) => ValidateAndFormatCors();
        _corsSave.Click += async (_, _) => await SaveCorsAsync();
        _corsDelete.Click += async (_, _) => await DeleteCorsAsync();

        var page = NewPage("CORS");
        var buttons = ButtonBar(_corsDelete, _corsSave, _corsValidate, _corsReload);
        page.Controls.Add(_corsViews);
        page.Controls.Add(_corsReason);
        page.Controls.Add(buttons);
        _corsReason.Dock = DockStyle.Top;
        buttons.Dock = DockStyle.Bottom;
        return page;
    }

    private TabPage BuildVersioningTab()
    {
        _versioningMode.Items.AddRange(["未启用", "已启用", "已暂停"]);
        _versioningMode.SelectedIndex = 0;
        _versioningReload.Click += async (_, _) => await LoadVersioningAsync(force: true);
        _versioningSave.Click += async (_, _) => await SaveVersioningAsync();
        var layout = VerticalLayout();
        layout.Controls.Add(new Label { Text = "Bucket 版本控制", AutoSize = true, Font = BoldFont() });
        layout.Controls.Add(_versioningReason);
        layout.Controls.Add(new Label
        {
            Text = "启用后，新写入会生成 Version ID；暂停只影响后续写入，不会删除已有版本。版本控制一旦启用，不能恢复为“从未启用”。",
            AutoSize = true, MaximumSize = new Size(800, 0), ForeColor = Color.DarkOrange
        });
        layout.Controls.Add(_versioningMode);
        layout.Controls.Add(ButtonBar(_versioningSave, _versioningReload));
        var page = NewPage("版本控制");
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildEncryptionTab()
    {
        _encryptionMode.Items.AddRange(["未配置", "SSE-S3（AES256）", "SSE-KMS"]);
        _encryptionMode.SelectedIndex = 0;
        _encryptionMode.SelectedIndexChanged += (_, _) => UpdateKmsControls();
        _encryptionReload.Click += async (_, _) => await LoadEncryptionAsync(force: true);
        _encryptionSave.Click += async (_, _) => await SaveEncryptionAsync();
        _encryptionDelete.Click += async (_, _) => await DeleteEncryptionAsync();
        var layout = VerticalLayout();
        layout.Controls.Add(new Label { Text = "Bucket 默认加密", AutoSize = true, Font = BoldFont() });
        layout.Controls.Add(_encryptionReason);
        layout.Controls.Add(_encryptionMode);
        layout.Controls.Add(new Label { Text = "KMS Key ID / ARN：", AutoSize = true, Margin = new Padding(0, 12, 0, 3) });
        layout.Controls.Add(_kmsKeyId);
        layout.Controls.Add(ButtonBar(_encryptionDelete, _encryptionSave, _encryptionReload));
        var page = NewPage("默认加密");
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildTagsTab()
    {
        _tagsGrid.Columns.Add("Key", "Key");
        _tagsGrid.Columns.Add("Value", "Value");
        _tagsGrid.Columns[0].Width = 280;
        _tagsGrid.Columns[1].Width = 460;
        _tagsReload.Click += async (_, _) => await LoadTagsAsync(force: true);
        _tagsSave.Click += async (_, _) => await SaveTagsAsync();
        _tagsDelete.Click += async (_, _) => await DeleteTagsAsync();
        var page = NewPage("Tags");
        var buttons = ButtonBar(_tagsDelete, _tagsSave, _tagsReload);
        page.Controls.Add(_tagsGrid);
        page.Controls.Add(_tagsReason);
        page.Controls.Add(buttons);
        _tagsReason.Dock = DockStyle.Top;
        buttons.Dock = DockStyle.Bottom;
        return page;
    }

    private async Task LoadSelectedConfigurationAsync()
    {
        if (_properties is null || _busy) return;
        switch ((BucketManagementPage)_tabs.SelectedIndex)
        {
            case BucketManagementPage.Cors: await LoadCorsAsync(); break;
            case BucketManagementPage.Versioning: await LoadVersioningAsync(); break;
            case BucketManagementPage.Encryption: await LoadEncryptionAsync(); break;
            case BucketManagementPage.Tags: await LoadTagsAsync(); break;
        }
    }

    private void ApplyConfigurationCapabilities(BucketCapabilities capabilities)
    {
        _corsReason.Text = capabilities.Cors.Reason;
        SetControls(capabilities.Cors.Supported,
            _corsViews, _corsReload, _corsValidate, _corsSave, _corsDelete);
        _versioningReason.Text = capabilities.Versioning.Reason;
        SetControls(capabilities.Versioning.Supported,
            _versioningMode, _versioningReload, _versioningSave);
        _encryptionReason.Text = $"{capabilities.Encryption.Reason}\r\nSSE-KMS：{capabilities.KmsEncryption.Reason}";
        SetControls(capabilities.Encryption.Supported,
            _encryptionMode, _encryptionReload, _encryptionSave, _encryptionDelete);
        _tagsReason.Text = $"{capabilities.Tagging.Reason}\r\n最多 50 个 Tag，Key 不可重复。成本分配标签需要在云厂商控制台另行激活。";
        SetControls(capabilities.Tagging.Supported,
            _tagsGrid, _tagsReload, _tagsSave, _tagsDelete);
        UpdateKmsControls();
    }

    private async Task LoadCorsAsync(bool force = false)
    {
        if (_corsLoaded && !force || _properties?.Capabilities.Cors.Supported != true) return;
        await ExecuteAsync("读取 Bucket CORS", async token =>
        {
            var loaded = await _storage.GetBucketCorsAsync(_profile, _bucket, token);
            _currentCors = loaded;
            ApplyCors(loaded);
            _corsLoaded = true;
        });
    }

    private void ApplyCors(BucketCorsConfiguration configuration)
    {
        _corsGrid.Rows.Clear();
        foreach (var rule in configuration.Rules)
            _corsGrid.Rows.Add(rule.Id ?? string.Empty, string.Join(", ", rule.AllowedOrigins),
                string.Join(", ", rule.AllowedMethods), string.Join(", ", rule.AllowedHeaders),
                string.Join(", ", rule.ExposeHeaders), rule.MaxAgeSeconds?.ToString() ?? string.Empty);
        _corsJson.Text = BucketCorsDocument.Serialize(configuration);
    }

    private BucketCorsConfiguration ReadCorsGrid()
    {
        var rules = _corsGrid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow)
            .Select(row => new BucketCorsRule(
                Cell(row, 0), SplitCell(row, 1), SplitCell(row, 2),
                SplitCell(row, 3), SplitCell(row, 4), ParseNullableInt(Cell(row, 5))))
            .ToArray();
        return BucketCorsDocument.Validate(new BucketCorsConfiguration(rules));
    }

    private bool SynchronizeCorsEditor(bool showErrors)
    {
        try
        {
            if (_corsViews.SelectedIndex == 1)
                _corsJson.Text = BucketCorsDocument.Serialize(ReadCorsGrid());
            else
                ApplyCors(BucketCorsDocument.Parse(_corsJson.Text));
            return true;
        }
        catch (Exception exception)
        {
            if (showErrors)
                ErrorDialog.ShowException(this, "CORS 配置无效", "校验 CORS", exception, _bucket);
            return false;
        }
    }

    private void ValidateAndFormatCors()
    {
        try
        {
            var value = _corsViews.SelectedIndex == 1
                ? BucketCorsDocument.Parse(_corsJson.Text)
                : ReadCorsGrid();
            ApplyCors(value);
            _corsViews.SelectedIndex = 1;
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "CORS 配置无效", "校验 CORS", exception, _bucket);
        }
    }

    private async Task SaveCorsAsync()
    {
        BucketCorsConfiguration value;
        try
        {
            value = _corsViews.SelectedIndex == 1
                ? BucketCorsDocument.Parse(_corsJson.Text)
                : ReadCorsGrid();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "CORS 配置无效", "校验 CORS", exception, _bucket);
            return;
        }
        if (!ConfirmChange("保存 CORS", $"规则数：{_currentCors.Rules.Count} → {value.Rules.Count}")) return;
        await ExecuteAsync("保存 Bucket CORS", async token =>
        {
            await _storage.PutBucketCorsAsync(_profile, _bucket, value, token);
            var loaded = await _storage.GetBucketCorsAsync(_profile, _bucket, token);
            _currentCors = loaded;
            ApplyCors(loaded);
        });
    }

    private async Task DeleteCorsAsync()
    {
        if (!ConfirmChange("删除 CORS", $"将删除当前 {_currentCors.Rules.Count} 条 CORS 规则。")) return;
        await ExecuteAsync("删除 Bucket CORS", async token =>
        {
            await _storage.DeleteBucketCorsAsync(_profile, _bucket, token);
            _currentCors = new BucketCorsConfiguration([]);
            ApplyCors(_currentCors);
        });
    }

    private async Task LoadVersioningAsync(bool force = false)
    {
        if (_versioningLoaded && !force || _properties?.Capabilities.Versioning.Supported != true) return;
        await ExecuteAsync("读取版本控制", async token =>
        {
            var loaded = await _storage.GetBucketVersioningAsync(_profile, _bucket, token);
            _currentVersioning = loaded;
            _versioningMode.SelectedIndex = (int)loaded;
            _versioningLoaded = true;
        });
    }

    private async Task SaveVersioningAsync()
    {
        var value = (BucketVersioningState)_versioningMode.SelectedIndex;
        if (value == BucketVersioningState.Disabled)
        {
            MessageBox.Show(this, "不能主动将版本控制恢复为“未启用”；已启用的 Bucket 只能暂停。", "版本控制", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var consequence = value == BucketVersioningState.Enabled
            ? "后续写入和普通删除将生成版本或 Delete Marker。"
            : "后续写入不再生成普通历史版本，但已有版本不会被删除。";
        if (!ConfirmChange("应用版本状态", $"{Display(_currentVersioning)} → {Display(value)}\r\n\r\n{consequence}")) return;
        await ExecuteAsync("保存版本控制", async token =>
        {
            await _storage.PutBucketVersioningAsync(_profile, _bucket, value, token);
            _currentVersioning = await _storage.GetBucketVersioningAsync(_profile, _bucket, token);
            _versioningMode.SelectedIndex = (int)_currentVersioning;
        });
    }

    private async Task LoadEncryptionAsync(bool force = false)
    {
        if (_encryptionLoaded && !force || _properties?.Capabilities.Encryption.Supported != true) return;
        await ExecuteAsync("读取默认加密", async token =>
        {
            var loaded = await _storage.GetBucketEncryptionAsync(_profile, _bucket, token);
            _currentEncryption = loaded;
            _encryptionMode.SelectedIndex = (int)loaded.Mode;
            _kmsKeyId.Text = loaded.KmsKeyId ?? string.Empty;
            _encryptionLoaded = true;
            UpdateKmsControls();
        });
    }

    private async Task SaveEncryptionAsync()
    {
        var value = new BucketEncryptionConfiguration(
            (BucketEncryptionMode)_encryptionMode.SelectedIndex, _kmsKeyId.Text.Trim());
        try { value.Validate(_properties?.Capabilities.KmsEncryption.Supported == true); }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "默认加密配置无效", "校验默认加密", exception, _bucket);
            return;
        }
        if (value.Mode == BucketEncryptionMode.None)
        {
            await DeleteEncryptionAsync();
            return;
        }
        if (!ConfirmChange("保存默认加密", $"{_currentEncryption.Summary} → {value.Summary}")) return;
        await ExecuteAsync("保存默认加密", async token =>
        {
            await _storage.PutBucketEncryptionAsync(_profile, _bucket, value, token);
            _currentEncryption = await _storage.GetBucketEncryptionAsync(_profile, _bucket, token);
            _encryptionMode.SelectedIndex = (int)_currentEncryption.Mode;
            _kmsKeyId.Text = _currentEncryption.KmsKeyId ?? string.Empty;
        });
    }

    private async Task DeleteEncryptionAsync()
    {
        if (!ConfirmChange("删除默认加密", $"{_currentEncryption.Summary} → 未配置")) return;
        await ExecuteAsync("删除默认加密", async token =>
        {
            await _storage.DeleteBucketEncryptionAsync(_profile, _bucket, token);
            _currentEncryption = new BucketEncryptionConfiguration(BucketEncryptionMode.None);
            _encryptionMode.SelectedIndex = 0;
            _kmsKeyId.Clear();
        });
    }

    private void UpdateKmsControls()
    {
        _kmsKeyId.Enabled = _properties?.Capabilities.Encryption.Supported == true &&
            _properties.Capabilities.KmsEncryption.Supported &&
            _encryptionMode.SelectedIndex == (int)BucketEncryptionMode.SseKms;
    }

    private async Task LoadTagsAsync(bool force = false)
    {
        if (_tagsLoaded && !force || _properties?.Capabilities.Tagging.Supported != true) return;
        await ExecuteAsync("读取 Bucket Tags", async token =>
        {
            var loaded = await _storage.GetBucketTagsAsync(_profile, _bucket, token);
            _currentTags = loaded;
            ApplyTags(loaded);
            _tagsLoaded = true;
        });
    }

    private void ApplyTags(IReadOnlyList<BucketTag> tags)
    {
        _tagsGrid.Rows.Clear();
        foreach (var tag in tags) _tagsGrid.Rows.Add(tag.Key, tag.Value);
    }

    private IReadOnlyList<BucketTag> ReadTags() => BucketTagValidator.Validate(
        _tagsGrid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow)
            .Select(row => new BucketTag(Cell(row, 0), Cell(row, 1))));

    private async Task SaveTagsAsync()
    {
        IReadOnlyList<BucketTag> value;
        try { value = ReadTags(); }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "Bucket Tags 无效", "校验 Bucket Tags", exception, _bucket);
            return;
        }
        if (value.Count == 0) { await DeleteTagsAsync(); return; }
        if (!ConfirmChange("保存 Bucket Tags", $"Tag 数：{_currentTags.Count} → {value.Count}")) return;
        await ExecuteAsync("保存 Bucket Tags", async token =>
        {
            await _storage.PutBucketTagsAsync(_profile, _bucket, value, token);
            _currentTags = await _storage.GetBucketTagsAsync(_profile, _bucket, token);
            ApplyTags(_currentTags);
        });
    }

    private async Task DeleteTagsAsync()
    {
        if (!ConfirmChange("删除 Bucket Tags", $"将删除当前 {_currentTags.Count} 个 Tag。")) return;
        await ExecuteAsync("删除 Bucket Tags", async token =>
        {
            await _storage.DeleteBucketTagsAsync(_profile, _bucket, token);
            _currentTags = [];
            ApplyTags(_currentTags);
        });
    }

    private bool ConfirmChange(string title, string difference) => MessageBox.Show(
        this, $"即将修改 Bucket “{_bucket}”：\r\n\r\n{difference}\r\n\r\n确定继续吗？",
        title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
        MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private static string Display(BucketVersioningState value) => value switch
    {
        BucketVersioningState.Disabled => "未启用",
        BucketVersioningState.Enabled => "已启用",
        BucketVersioningState.Suspended => "已暂停",
        _ => value.ToString()
    };

    private static string Cell(DataGridViewRow row, int index) =>
        Convert.ToString(row.Cells[index].Value)?.Trim() ?? string.Empty;

    private static IReadOnlyList<string> SplitCell(DataGridViewRow row, int index) =>
        Cell(row, index).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int? ParseNullableInt(string value) => string.IsNullOrWhiteSpace(value)
        ? null
        : int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Max Age “{value}”不是有效整数。");

    private static ComboBox SelectionBox() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 300
    };

    private static Button NamedButton(string name, string text)
    {
        var button = ActionButton(text);
        button.Name = name;
        return button;
    }

    private static DataGridView ConfigurationGrid() => new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false,
        AllowUserToAddRows = true, AllowUserToDeleteRows = true,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, BackgroundColor = SystemColors.Window
    };

    private static TextBox JsonTextBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, AcceptsTab = true,
        ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 10f)
    };

    private static FlowLayoutPanel VerticalLayout() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
        Padding = new Padding(16), AutoScroll = true, WrapContents = false
    };
}
