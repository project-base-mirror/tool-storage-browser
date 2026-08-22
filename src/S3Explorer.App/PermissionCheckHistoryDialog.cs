using System.Text;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class PermissionCheckHistoryDialog : Form
{
    private readonly PermissionCheckHistoryStore _store;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private IReadOnlyList<PermissionCheckHistoryEntry> _entries = Array.Empty<PermissionCheckHistoryEntry>();

    public PermissionCheckHistoryDialog(PermissionCheckHistoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Name = nameof(PermissionCheckHistoryDialog);
        Text = "权限检查记录";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 560);
        MinimumSize = new Size(760, 420);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        _grid.Name = "PermissionCheckHistoryGrid";
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        AddColumn("Credential", "凭据", 180);
        AddColumn("Scope", "目标", 280);
        AddColumn("Mode", "类型", 90);
        AddColumn("CheckedAt", "检查时间", 150);
        AddColumn("Summary", "结果", 220);
        _grid.CellDoubleClick += (_, _) => ShowSelectedDetails();

        var detail = new Button { Name = "ViewPermissionCheckDetailsButton", Text = "查看详情", AutoSize = true };
        detail.Click += (_, _) => ShowSelectedDetails();
        var delete = new Button { Name = "DeletePermissionCheckHistoryButton", Text = "删除选中", AutoSize = true };
        delete.Click += async (_, _) => await RunUiActionAsync("删除权限检查记录", DeleteSelectedAsync);
        var clear = new Button { Name = "ClearPermissionCheckHistoryButton", Text = "清空记录", AutoSize = true };
        clear.Click += async (_, _) => await RunUiActionAsync("清空权限检查记录", ClearAsync);
        var close = new Button { Name = "ClosePermissionCheckHistoryButton", Text = "关闭", DialogResult = DialogResult.Cancel, AutoSize = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.Add(close);
        actions.Controls.Add(clear);
        actions.Controls.Add(delete);
        actions.Controls.Add(detail);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(actions, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "每个凭据与目标范围只保留最近一次结果；记录不包含密钥或令牌。", AutoSize = true, ForeColor = SystemColors.GrayText }, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        AcceptButton = close;
        CancelButton = close;
        Shown += async (_, _) => await RunUiActionAsync("加载权限检查记录", () => RefreshAsync());
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _entries = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _grid.Rows.Clear();
        foreach (var entry in _entries)
        {
            var row = _grid.Rows.Add(
                entry.CredentialName,
                string.IsNullOrWhiteSpace(entry.TargetScope) ? "（未指定）" : entry.TargetScope,
                entry.MutationProbe ? "写入探针" : "只读检查",
                entry.CheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                FormatSummary(entry));
            _grid.Rows[row].Tag = entry;
        }
        _status.Text = $"共 {_entries.Count} 条记录";
    }

    private void AddColumn(string name, string header, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });

    private PermissionCheckHistoryEntry? SelectedEntry() =>
        _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as PermissionCheckHistoryEntry;

    private void ShowSelectedDetails()
    {
        var entry = SelectedEntry();
        if (entry is null) return;
        var text = new StringBuilder()
            .AppendLine($"凭据：{entry.CredentialName} · {entry.Provider} / {entry.Kind} · {entry.Fingerprint}")
            .AppendLine($"目标：{entry.TargetScope}")
            .AppendLine($"类型：{(entry.MutationProbe ? "写入探针" : "只读检查")}")
            .AppendLine($"时间：{entry.CheckedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}")
            .AppendLine()
            .ToString();
        foreach (var check in entry.Result.Checks)
            text += $"[{check.State}] {check.Subject}/{check.Name} {check.Message}\r\n";
        using var dialog = new Form { Text = "权限检查详情", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(760, 480), Icon = UiIcons.CreateApplicationIcon() };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = text };
        dialog.Controls.Add(box);
        dialog.ShowDialog(this);
    }

    private async Task DeleteSelectedAsync()
    {
        var entry = SelectedEntry();
        if (entry is null) return;
        if (MessageBox.Show(
                this,
                $"确定删除凭据“{entry.CredentialName}”在目标“{entry.TargetScope}”的最近检查结果吗？",
                "删除权限检查记录",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        await _store.DeleteAsync(entry.CredentialId, entry.TargetScope).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RunUiActionAsync(string operation, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ErrorDialog.ShowException(this, operation + "失败", operation, exception);
        }
    }

    private async Task ClearAsync()
    {
        if (_entries.Count == 0) return;
        if (MessageBox.Show(this, "确定清空所有权限检查记录吗？", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        await _store.ClearAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private static string FormatSummary(PermissionCheckHistoryEntry entry) =>
        $"通过 {entry.PassedCount} · 拒绝 {entry.DeniedCount} · 无法确定 {entry.IndeterminateCount} · 不支持 {entry.UnsupportedCount}";
}
