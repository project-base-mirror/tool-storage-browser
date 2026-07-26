using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class FolderSyncJobDialog : Form
{
    private sealed record ProfileChoice(ConnectionProfile Profile)
    {
        public override string ToString() => Profile.Name;
    }

    private readonly TextBox _name = new();
    private readonly TextBox _local = new();
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _bucket = new();
    private readonly TextBox _prefix = new();
    private readonly ComboBox _direction = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _newFiles = new() { Text = "同步新增文件", Checked = true, AutoSize = true };
    private readonly CheckBox _changedFiles = new() { Text = "同步已更改文件", Checked = true, AutoSize = true };
    private readonly CheckBox _deletions = new() { Text = "将源端删除传播到目标端", AutoSize = true };
    private readonly CheckBox _hashes = new() { Text = "可用时比较文件 MD5/ETag（较慢）", AutoSize = true };
    private readonly TextBox _exclusions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 100 };
    private readonly Button _save = new() { Text = "保存任务", DialogResult = DialogResult.OK, Size = new Size(100, 32) };
    private readonly Button _cancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(88, 32) };
    private readonly Guid _id;

    public FolderSyncJob Job { get; private set; }

    public FolderSyncJobDialog(
        IReadOnlyList<ConnectionProfile> profiles,
        FolderSyncJob? job = null,
        ConnectionProfile? initialProfile = null,
        string? initialBucket = null,
        string? initialPrefix = null)
    {
        Job = job ?? new FolderSyncJob();
        _id = Job.Id;
        Text = job is null ? "添加文件夹同步任务" : "编辑文件夹同步任务";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 650);
        MinimumSize = new Size(650, 560);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _profile.Items.AddRange(profiles.Select(profile => (object)new ProfileChoice(profile)).ToArray());
        _direction.Items.AddRange(["本地 → S3（上传镜像）", "S3 → 本地（下载镜像）"]);
        BuildLayout();
        LoadJob(job, initialProfile, initialBucket, initialPrefix);
        _save.Click += (_, _) => SaveJob();
    }

    private void BuildLayout()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16),
            ColumnCount = 3
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var row = 0;
        AddField(table, ref row, "任务名称：", _name);

        _local.Dock = DockStyle.Fill;
        AddField(table, ref row, "本地文件夹：", _local, new Button { Text = "浏览...", AutoSize = true });
        var browse = (Button)table.GetControlFromPosition(2, row - 1)!;
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择同步使用的本地文件夹" };
            if (Directory.Exists(_local.Text)) dialog.InitialDirectory = _local.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) _local.Text = dialog.SelectedPath;
        };

        AddField(table, ref row, "连接：", _profile);
        AddField(table, ref row, "Bucket：", _bucket);
        AddField(table, ref row, "S3 前缀：", _prefix);
        AddHint(table, ref row, "前缀可留空；示例 backups/site/。同步仅在指定 Bucket 与前缀内操作。");
        AddField(table, ref row, "同步方向：", _direction);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        options.Controls.AddRange([_newFiles, _changedFiles, _deletions, _hashes]);
        AddField(table, ref row, "行为：", options);
        AddHint(table, ref row, "这是单向镜像。启用删除传播时，源端不存在的文件会从目标端删除，执行前会再次确认。");

        AddField(table, ref row, "排除规则：", _exclusions);
        AddHint(table, ref row, "每行一个规则；* 匹配单层，** 匹配多层。示例：*.tmp、bin/**、**/.git/**。");

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(3, 12, 3, 3)
        };
        buttons.Controls.AddRange([_cancel, _save]);
        table.Controls.Add(buttons, 0, row);
        table.SetColumnSpan(buttons, 3);
        Controls.Add(table);
        AcceptButton = _save;
        CancelButton = _cancel;
    }

    private static void AddField(TableLayoutPanel table, ref int row, string text, Control field, Control? trailing = null)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 3)
        }, 0, row);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(field, 1, row);
        if (trailing is not null) table.Controls.Add(trailing, 2, row);
        row++;
    }

    private static void AddHint(TableLayoutPanel table, ref int row, string text)
    {
        var hint = new Label
        {
            Text = text,
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(6, 0, 3, 8)
        };
        table.Controls.Add(hint, 0, row);
        table.SetColumnSpan(hint, 3);
        row++;
    }

    private void LoadJob(
        FolderSyncJob? job,
        ConnectionProfile? initialProfile,
        string? initialBucket,
        string? initialPrefix)
    {
        var source = job ?? new FolderSyncJob
        {
            Name = "新同步任务",
            LocalDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ProfileId = initialProfile?.Id ?? Guid.Empty,
            ProfileName = initialProfile?.Name ?? string.Empty,
            Bucket = initialBucket ?? string.Empty,
            Prefix = initialPrefix ?? string.Empty
        };
        _name.Text = source.Name;
        _local.Text = source.LocalDirectory;
        _bucket.Text = source.Bucket;
        _prefix.Text = source.Prefix;
        _direction.SelectedIndex = source.Direction == FolderSyncDirection.Upload ? 0 : 1;
        _newFiles.Checked = source.IncludeNewFiles;
        _changedFiles.Checked = source.IncludeChangedFiles;
        _deletions.Checked = source.PropagateDeletions;
        _hashes.Checked = source.CompareHashesWhenAvailable;
        _exclusions.Lines = source.ExclusionPatterns.ToArray();

        var profileId = source.ProfileId != Guid.Empty ? source.ProfileId : initialProfile?.Id;
        for (var index = 0; index < _profile.Items.Count; index++)
        {
            if (_profile.Items[index] is ProfileChoice choice && choice.Profile.Id == profileId)
            {
                _profile.SelectedIndex = index;
                break;
            }
        }
        if (_profile.SelectedIndex < 0 && _profile.Items.Count > 0) _profile.SelectedIndex = 0;
    }

    private void SaveJob()
    {
        try
        {
            if (_profile.SelectedItem is not ProfileChoice selected)
                throw new InvalidOperationException("请先创建并选择对象存储连接。");
            if (!_newFiles.Checked && !_changedFiles.Checked && !_deletions.Checked)
                throw new InvalidOperationException("任务至少需要包含新增、更改或删除中的一类操作。");

            var local = Path.IsPathFullyQualified(_local.Text.Trim())
                ? Path.GetFullPath(_local.Text.Trim())
                : throw new InvalidOperationException("本地文件夹必须使用绝对路径。");
            var candidate = new FolderSyncJob
            {
                Id = _id,
                Name = _name.Text.Trim(),
                LocalDirectory = local,
                ProfileId = selected.Profile.Id,
                ProfileName = selected.Profile.Name,
                Bucket = _bucket.Text.Trim(),
                Prefix = S3Path.NormalizePrefix(_prefix.Text),
                Direction = _direction.SelectedIndex == 1 ? FolderSyncDirection.Download : FolderSyncDirection.Upload,
                IncludeNewFiles = _newFiles.Checked,
                IncludeChangedFiles = _changedFiles.Checked,
                PropagateDeletions = _deletions.Checked,
                CompareHashesWhenAvailable = _hashes.Checked,
                ExclusionPatterns = _exclusions.Lines
                    .Select(pattern => pattern.Trim())
                    .Where(pattern => pattern.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                LastRunAt = Job.LastRunAt
            };
            candidate.Validate();
            Job = candidate;
        }
        catch (Exception exception)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "无法保存同步任务", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
