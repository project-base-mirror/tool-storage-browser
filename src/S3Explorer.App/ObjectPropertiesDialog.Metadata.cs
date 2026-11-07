using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class ObjectPropertiesDialog
{
    private readonly TextBox _contentType = HeaderTextBox("ObjectContentType");
    private readonly TextBox _cacheControl = HeaderTextBox("ObjectCacheControl");
    private readonly TextBox _contentEncoding = HeaderTextBox("ObjectContentEncoding");
    private readonly TextBox _contentDisposition = HeaderTextBox("ObjectContentDisposition");
    private readonly TextBox _expiresUtc = HeaderTextBox("ObjectExpiresUtc");
    private readonly DataGridView _metadataGrid = KeyValueGrid("ObjectMetadataGrid", "Metadata Key");
    private readonly TextBox _metadataDestination = HeaderTextBox("ObjectMetadataCopyDestination");
    private readonly Button _metadataReplace = ActionButton("SaveObjectMetadataButton", "保存当前对象");
    private readonly Button _metadataSave = ActionButton("SaveObjectMetadataCopyButton", "另存为新对象...");
    private readonly Label _metadataReason = CapabilityLabel("ObjectMetadataCapabilityReason");
    private readonly DataGridView _tagsGrid = KeyValueGrid("ObjectTagsGrid", "Tag Key");
    private readonly Button _tagsReload = ActionButton("ReloadObjectTagsButton", "重新读取");
    private readonly Button _tagsSave = ActionButton("SaveObjectTagsButton", "保存 Tags");
    private readonly Button _tagsDelete = ActionButton("DeleteObjectTagsButton", "删除 Tags");
    private readonly Label _tagsReason = CapabilityLabel("ObjectTagsCapabilityReason");
    private IReadOnlyList<ObjectTag> _currentObjectTags = [];
    private bool _metadataBusy;
    private bool _tagsLoaded;

    private TabPage BuildMetadata(ObjectProperties value)
    {
        ApplyMetadata(value);
        var capability = _profile is null
            ? BucketFeatureSupport.No("当前调用未提供连接上下文，仅显示现有 Header 与 Metadata")
            : ObjectMetadataCopyPolicy.For(_profile.ServiceType, value);
        var rewrite = _profile is null ? capability : ObjectMetadataRewritePolicy.For(_profile.ServiceType, value);
        var editable = capability.Supported || rewrite.Supported;
        _metadataReplace.Enabled = rewrite.Supported && _storage is not null;
        _metadataReplace.Click += async (_, _) => await ReplaceMetadataAsync();
        _metadataReason.Text = rewrite.Reason;
        _metadataSave.Enabled = capability.Supported && _storage is not null;
        foreach (var textBox in new[] { _contentType, _cacheControl, _contentEncoding, _contentDisposition, _expiresUtc })
            textBox.ReadOnly = !editable;
        if (_profile?.ServiceType == S3ServiceType.SupabaseStorage)
        {
            _contentDisposition.ReadOnly = true;
            _expiresUtc.ReadOnly = true;
        }
        _metadataGrid.ReadOnly = !editable;
        _metadataGrid.AllowUserToAddRows = editable;
        _metadataGrid.AllowUserToDeleteRows = editable;
        _metadataDestination.Text = ObjectMetadataCopyPolicy.SuggestDestinationKey(value.Key);
        _metadataDestination.ReadOnly = !capability.Supported;
        _metadataReason.MaximumSize = new Size(520, 0);
        _metadataSave.Click += async (_, _) => await SaveMetadataAsync();

        var headers = new TableLayoutPanel
        {
            Name = "ObjectMetadataHeadersTable",
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(8)
        };
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddHeaderRow(headers, "Content-Type：", _contentType);
        AddHeaderRow(headers, "Cache-Control：", _cacheControl);
        AddHeaderRow(headers, "Content-Encoding：", _contentEncoding);
        AddHeaderRow(headers, "Content-Disposition：", _contentDisposition);
        AddHeaderRow(headers, "Expires UTC：", _expiresUtc);
        AddHeaderRow(headers, "另存目标 Key：", _metadataDestination);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 7)
        };
        actions.Controls.Add(_metadataSave);
        actions.Controls.Add(_metadataReplace);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(headers, 0, 0);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_metadataGrid, 0, 1);
        layout.Controls.Add(_metadataReason, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        var page = new TabPage("Metadata");
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildTags(ObjectProperties properties)
    {
        var capability = _profile is null
            ? BucketFeatureSupport.No("当前调用未提供连接上下文，不能读取对象 Tags")
            : S3ProviderCapabilityRegistry.For(_profile.ServiceType).Object.Tagging;
        _tagsReason.Text = capability.Reason;
        _tagsReload.Click += async (_, _) => await LoadObjectTagsAsync(force: true);
        _tagsSave.Click += async (_, _) => await SaveObjectTagsAsync();
        _tagsDelete.Click += async (_, _) => await DeleteObjectTagsAsync();
        foreach (var control in new Control[] { _tagsGrid, _tagsReload, _tagsSave, _tagsDelete })
            control.Enabled = capability.Supported && _storage is not null;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8, 7, 8, 7)
        };
        actions.Controls.Add(_tagsSave);
        actions.Controls.Add(_tagsDelete);
        actions.Controls.Add(_tagsReload);
        actions.Controls.Add(_tagsReason);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_tagsGrid, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        var page = new TabPage("Tags")
        {
            AccessibleDescription = $"s3://{properties.Bucket}/{properties.Key} 的对象 Tags"
        };
        page.Controls.Add(layout);
        return page;
    }

    private void ApplyMetadata(ObjectProperties value)
    {
        _contentType.Text = value.ContentType ?? string.Empty;
        _cacheControl.Text = value.CacheControl ?? string.Empty;
        _contentEncoding.Text = value.ContentEncoding ?? string.Empty;
        _contentDisposition.Text = value.ContentDisposition ?? string.Empty;
        _expiresUtc.Text = value.ExpiresUtc?.UtcDateTime.ToString("O") ?? value.ExpiresHeader ?? string.Empty;
        ApplyGrid(_metadataGrid, value.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
    }

    private async Task ReplaceMetadataAsync()
    {
        if (_storage is null || _profile is null || _objectProperties is null || _metadataBusy) return;
        ObjectWriteHeaders headers;
        try { headers = ReadMetadataEdits(); }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "Header / Metadata 无效", "校验对象 Metadata", exception, ObjectLocation());
            return;
        }
        if (MessageBox.Show(this, ObjectMetadataRewritePolicy.WarningFor(_profile.ServiceType) + "\r\n\r\n" + ObjectLocation(),
            "确认原地保存 Metadata", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var expected = _objectProperties;
        await ExecuteMetadataAsync("保存 Metadata", async token =>
        {
            await _storage.ReplaceObjectMetadataAsync(_profile, expected, headers, token);
            _objectProperties = await _storage.GetObjectPropertiesAsync(_profile, expected.Bucket, expected.Key, token);
            ApplyMetadata(_objectProperties);
            _metadataReason.Text = "已保存并重新读取当前对象。";
        });
    }

    private async Task SaveMetadataAsync()
    {
        if (_storage is null || _profile is null || _objectProperties is null || _metadataBusy) return;
        var capability = ObjectMetadataCopyPolicy.For(_profile.ServiceType, _objectProperties);
        if (!capability.Supported)
        {
            _metadataReason.Text = capability.Reason;
            return;
        }
        ObjectWriteHeaders headers;
        var destinationKey = _metadataDestination.Text;
        try
        {
            ObjectMetadataCopyPolicy.ValidateDestination(_objectProperties, destinationKey);
            headers = ReadMetadataEdits();
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "Header / Metadata 无效", "校验对象 Metadata", exception, ObjectLocation());
            return;
        }

        if (MessageBox.Show(
                this,
                $"将对话框中的 Header / Metadata 快照另存到同 Bucket 的新 Key。\r\n" +
                "源对象保持不变；已存在的当前目标不会被覆盖。复制可能产生请求费、存储费与新版本。\r\n" +
                "固定版本的内容与非编辑 Header 保留；Tags 使用复制执行时该版本的服务端值，不是打开窗口时的快照。\r\n" +
                "新对象受目标 Key 的 Bucket Policy / 生命周期规则约束。\r\n" +
                "仅支持 SSE-S3、禁用 ACL 且无 Object Lock 的 Bucket；无法验证时会停止。\r\n\r\n" +
                $"来源：{ObjectLocation()}\r\n固定版本：{_objectProperties.VersionId}\r\n目标：s3://{_objectProperties.Bucket}/{destinationKey}",
                "确认另存 Metadata 快照",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        var source = _objectProperties;
        await ExecuteMetadataAsync("另存 Metadata 快照", async token =>
        {
            await _storage.SaveObjectMetadataCopyAsync(_profile, source, destinationKey, headers, token);
            _metadataReason.Text = $"已另存为 s3://{source.Bucket}/{destinationKey}。源对象未改变；关闭后刷新列表可查看副本。";
        });
    }

    internal ObjectWriteHeaders ReadMetadataEdits()
    {
        // The user may press Save while the current cell is still in edit mode.
        // Read its committed value, not the cell's previous cached value.
        if (!_metadataGrid.EndEdit())
            throw new ArgumentException("请先完成当前 Metadata 单元格的编辑。");
        DateTimeOffset? expires = null;
        if (!string.IsNullOrWhiteSpace(_expiresUtc.Text))
        {
            if (!DateTimeOffset.TryParse(_expiresUtc.Text.Trim(), out var parsedExpires))
                throw new ArgumentException("Expires UTC 必须为空或有效的 ISO 8601 日期时间。");
            expires = parsedExpires.ToUniversalTime();
        }
        return new ObjectWriteHeaders(
            _contentType.Text, _cacheControl.Text, _contentEncoding.Text, _contentDisposition.Text,
            expires, ReadMetadataGrid()).ValidateForMetadataCopy();
    }

    private async Task LoadObjectTagsAsync(bool force = false)
    {
        if (_storage is null || _profile is null || _objectProperties is null || _metadataBusy || (!force && _tagsLoaded))
            return;
        await ExecuteMetadataAsync("读取对象 Tags", async token =>
        {
            _currentObjectTags = await _storage.GetObjectTagsAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, token);
            ApplyGrid(_tagsGrid, _currentObjectTags.Select(tag =>
                new KeyValuePair<string, string>(tag.Key, tag.Value)));
            _tagsLoaded = true;
        });
    }

    private async Task SaveObjectTagsAsync()
    {
        if (_storage is null || _profile is null || _objectProperties is null || _metadataBusy) return;
        IReadOnlyList<ObjectTag> tags;
        try
        {
            tags = ObjectTagValidator.Validate(ReadGrid(_tagsGrid)
                .Select(pair => new ObjectTag(pair.Key, pair.Value)));
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, "对象 Tags 无效", "校验对象 Tags", exception, ObjectLocation());
            return;
        }
        if (tags.Count == 0) { await DeleteObjectTagsAsync(); return; }
        if (MessageBox.Show(
                this,
                $"确定保存以下单个对象的 Tags 吗？\r\n\r\n{ObjectLocation()}\r\nTag 数：{_currentObjectTags.Count} → {tags.Count}",
                "确认对象 Tags",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await ExecuteMetadataAsync("保存对象 Tags", async token =>
        {
            await _storage.PutObjectTagsAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, tags, token);
            _currentObjectTags = await _storage.GetObjectTagsAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, token);
            ApplyGrid(_tagsGrid, _currentObjectTags.Select(tag =>
                new KeyValuePair<string, string>(tag.Key, tag.Value)));
            _tagsLoaded = true;
        });
    }

    private async Task DeleteObjectTagsAsync()
    {
        if (_storage is null || _profile is null || _objectProperties is null || _metadataBusy) return;
        if (MessageBox.Show(
                this,
                $"确定删除以下单个对象的全部 Tags 吗？\r\n\r\n{ObjectLocation()}",
                "确认删除对象 Tags",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await ExecuteMetadataAsync("删除对象 Tags", async token =>
        {
            await _storage.DeleteObjectTagsAsync(
                _profile, _objectProperties.Bucket, _objectProperties.Key,
                _objectProperties.VersionId, token);
            _currentObjectTags = [];
            _tagsLoaded = true;
            ApplyGrid(_tagsGrid, Array.Empty<KeyValuePair<string, string>>());
        });
    }

    private async Task ExecuteMetadataAsync(string operation, Func<CancellationToken, Task> action)
    {
        if (_metadataBusy) return;
        _metadataBusy = true;
        UseWaitCursor = true;
        try { await action(_objectLockCancellation.Token); }
        catch (OperationCanceledException) when (_objectLockCancellation.IsCancellationRequested) { }
        catch (ObjectMetadataCopyException exception)
        {
            // Cancellation after dispatch is an uncertain write outcome, not a
            // guaranteed rollback. A closed dialog must not open a new modal error.
            if (IsDisposed || Disposing || _objectLockCancellation.IsCancellationRequested) return;
            _metadataReason.Text = exception.Message;
            ErrorDialog.ShowException(this, $"{operation}未确认", operation, exception, ObjectLocation());
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, $"{operation}失败", operation, exception, ObjectLocation());
        }
        finally
        {
            UseWaitCursor = false;
            _metadataBusy = false;
        }
    }

    private IReadOnlyDictionary<string, string> ReadMetadataGrid() =>
        ObjectMetadataValidator.ValidatePreservingValues(ReadGrid(_metadataGrid, preserveValues: true)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<KeyValuePair<string, string>> ReadGrid(DataGridView grid, bool preserveValues = false) =>
        grid.Rows.Cast<DataGridViewRow>()
            .Where(row => !row.IsNewRow)
            .Select(row => new KeyValuePair<string, string>(
                row.Cells[0].Value?.ToString()?.Trim() ?? string.Empty,
                (preserveValues ? row.Cells[1].Value?.ToString() : row.Cells[1].Value?.ToString()?.Trim()) ?? string.Empty))
            .Where(pair => pair.Key.Length > 0 || pair.Value.Length > 0)
            .ToArray();

    private static void ApplyGrid(
        DataGridView grid,
        IEnumerable<KeyValuePair<string, string>> values)
    {
        grid.Rows.Clear();
        foreach (var pair in values) grid.Rows.Add(pair.Key, pair.Value);
    }

    private static void AddHeaderRow(TableLayoutPanel table, string label, Control value)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 8, 3)
        }, 0, row);
        table.Controls.Add(value, 1, row);
    }

    private static TextBox HeaderTextBox(string name) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        Margin = new Padding(3, 4, 3, 3)
    };

    private static DataGridView KeyValueGrid(string name, string keyHeader)
    {
        var grid = new DataGridView
        {
            Name = name,
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        grid.Columns.Add("Key", keyHeader);
        grid.Columns.Add("Value", "值");
        return grid;
    }

    private static Label CapabilityLabel(string name) => new()
    {
        Name = name,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 10, 8, 3)
    };
}
