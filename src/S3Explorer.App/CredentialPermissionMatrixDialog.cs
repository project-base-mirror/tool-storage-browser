using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class CredentialPermissionMatrixDialog : Form
{
    private readonly IReadOnlyList<CredentialProfile> _credentials;
    private readonly PermissionCheckHistoryStore _historyStore;
    private readonly Func<CredentialProfile, IWin32Window, CancellationToken, Task> _runCheck;
    private readonly Func<CredentialProfile, IWin32Window, CancellationToken, Task> _runProbe;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _check = new()
    {
        Name = "CheckSelectedCredentialPermissionsButton",
        Text = "立即检查 OSS/CDN 控制面",
        AutoSize = true,
        MinimumSize = new Size(148, 34)
    };
    private readonly Button _probe = new()
    {
        Name = "ProbeSelectedCredentialPermissionsButton",
        Text = "存储探针...",
        AutoSize = true,
        MinimumSize = new Size(112, 34)
    };
    private CancellationTokenSource? _operationCancellation;

    public CredentialPermissionMatrixDialog(
        IReadOnlyList<CredentialProfile> credentials,
        PermissionCheckHistoryStore historyStore,
        Func<CredentialProfile, IWin32Window, CancellationToken, Task> runCheck,
        Func<CredentialProfile, IWin32Window, CancellationToken, Task> runProbe)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _runCheck = runCheck ?? throw new ArgumentNullException(nameof(runCheck));
        _runProbe = runProbe ?? throw new ArgumentNullException(nameof(runProbe));

        Name = nameof(CredentialPermissionMatrixDialog);
        Text = "凭据权限";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1120, 600);
        MinimumSize = new Size(900, 460);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        ConfigureGrid();

        _check.Click += async (_, _) => await RunSelectedAsync(_runCheck, "正在检查所选凭据...");
        _probe.Click += async (_, _) => await RunSelectedAsync(
            _runProbe,
            "正在执行真实写入、删除探针，请等待远端临时对象完成清理...");

        var history = new Button
        {
            Name = "ViewCredentialPermissionHistoryButton",
            Text = "检查记录...",
            AutoSize = true,
            MinimumSize = new Size(112, 34)
        };
        history.Click += async (_, _) => await ShowHistoryAsync();
        var close = new Button
        {
            Name = "CloseCredentialPermissionMatrixButton",
            Text = "关闭",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(90, 34)
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.Add(close);
        actions.Controls.Add(history);
        actions.Controls.Add(_probe);
        actions.Controls.Add(_check);

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

        var description = new Label
        {
            AutoSize = true,
            Text = "每行是一项统一凭据。√ 通过  ·  × 明确拒绝  ·  ? 无法确定  ·  — 未记录或不适用。多个关联目标按最严格结果汇总。",
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        };
        var safety = new Label
        {
            AutoSize = true,
            Text = "凭据中心只检查对象存储与 CDN 控制面权限。CDN 内容认证属于各 CDN 配置，由“CDN 配置中心 → 检查选中 CDN”验证。刷新/预热不会自动提交真实任务。",
            ForeColor = Color.DarkOrange,
            Margin = new Padding(0, 8, 0, 0)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(safety, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);

        CancelButton = close;
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, args) =>
        {
            if (_operationCancellation is null)
                return;
            _operationCancellation.Cancel();
            _status.Text = "正在取消当前检查，请稍候...";
            args.Cancel = true;
        };
        UpdateActions();
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var selectedId = SelectedCredential?.Id;
        var entries = await _historyStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        var rows = CredentialPermissionMatrixBuilder.Build(_credentials, entries);

        _grid.Rows.Clear();
        foreach (var row in rows.OrderBy(value => value.Credential.Name, StringComparer.OrdinalIgnoreCase))
        {
            var rowIndex = _grid.Rows.Add(
                row.Credential.Name,
                row.ListBucket.DisplaySymbol(),
                row.HeadObject.DisplaySymbol(),
                row.GetObject.DisplaySymbol(),
                row.PutObject.DisplaySymbol(),
                row.DeleteObject.DisplaySymbol(),
                row.PutObjectAcl.DisplaySymbol(),
                row.CdnControlQuery.DisplaySymbol(),
                row.RefreshOrPush.DisplaySymbol(),
                row.LastCheckedAtUtc == DateTimeOffset.MinValue
                    ? "从未检查"
                    : row.LastCheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            var gridRow = _grid.Rows[rowIndex];
            gridRow.Tag = row.Credential;
            ApplyCellStyle(gridRow.Cells[1], row.ListBucket);
            ApplyCellStyle(gridRow.Cells[2], row.HeadObject);
            ApplyCellStyle(gridRow.Cells[3], row.GetObject);
            ApplyCellStyle(gridRow.Cells[4], row.PutObject);
            ApplyCellStyle(gridRow.Cells[5], row.DeleteObject);
            ApplyCellStyle(gridRow.Cells[6], row.PutObjectAcl);
            ApplyCellStyle(gridRow.Cells[7], row.CdnControlQuery);
            ApplyCellStyle(gridRow.Cells[8], row.RefreshOrPush);
            if (selectedId == row.Credential.Id)
                gridRow.Selected = true;
        }

        if (_grid.SelectedRows.Count == 0 && _grid.Rows.Count > 0)
            _grid.Rows[0].Selected = true;
        _status.Text = rows.Count == 0
            ? "凭据中心中还没有凭据。"
            : $"共 {rows.Count} 项凭据；选择一行即可检查或执行探针。";
        UpdateActions();
    }

    private void ConfigureGrid()
    {
        _grid.Name = "CredentialPermissionMatrixGrid";
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Columns.Add(TextColumn("Credential", "凭据", 190));
        _grid.Columns.Add(StateColumn("ListBucket", "列举"));
        _grid.Columns.Add(StateColumn("HeadObject", "属性"));
        _grid.Columns.Add(StateColumn("GetObject", "下载"));
        _grid.Columns.Add(StateColumn("PutObject", "上传"));
        _grid.Columns.Add(StateColumn("DeleteObject", "删除"));
        _grid.Columns.Add(StateColumn("PutObjectAcl", "ACL"));
        _grid.Columns.Add(StateColumn("CdnControlQuery", "CDN 控制面查询", 118));
        _grid.Columns.Add(StateColumn("RefreshOrPush", "CDN 刷新/预热*", 118));
        if (_grid.Columns["CdnControlQuery"] is { } cdnQueryColumn)
            cdnQueryColumn.HeaderCell.ToolTipText =
                "阿里云执行 DescribeUserDomains；通用 HTTP 仅确认控制端点配置，不提交真实刷新请求。";
        if (_grid.Columns["RefreshOrPush"] is { } refreshColumn)
            refreshColumn.HeaderCell.ToolTipText =
                "刷新/预热会产生真实控制面任务，当前无副作用检查不会自动提交，因此通常显示 ?。";
        _grid.Columns.Add(TextColumn("LastChecked", "最近检查", 160));
        _grid.SelectionChanged += (_, _) => UpdateActions();
    }

    private async Task RunSelectedAsync(
        Func<CredentialProfile, IWin32Window, CancellationToken, Task> action,
        string status)
    {
        if (_operationCancellation is not null)
        {
            _operationCancellation.Cancel();
            return;
        }

        var credential = SelectedCredential;
        if (credential is null)
            return;

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        _status.Text = status;
        UseWaitCursor = true;
        UpdateActions();
        try
        {
            await action(credential, this, cancellation.Token).ConfigureAwait(true);
            if (!IsDisposed && !Disposing)
                await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
                _operationCancellation = null;
            if (!IsDisposed && !Disposing)
            {
                UseWaitCursor = false;
                UpdateActions();
            }
        }
    }

    private async Task ShowHistoryAsync()
    {
        using var history = new PermissionCheckHistoryDialog(_historyStore);
        history.ShowDialog(this);
        await RefreshAsync().ConfigureAwait(true);
    }

    private CredentialProfile? SelectedCredential =>
        _grid.SelectedRows.Count == 0
            ? null
            : _grid.SelectedRows[0].Tag as CredentialProfile;

    private void UpdateActions()
    {
        var busy = _operationCancellation is not null;
        var selected = SelectedCredential is not null;
        _grid.Enabled = !busy;
        _check.Enabled = busy || selected;
        _check.Text = busy ? "取消检查" : "立即检查 OSS/CDN 控制面";
        _probe.Enabled = !busy && selected;
    }

    private static DataGridViewTextBoxColumn TextColumn(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable
    };

    private static DataGridViewTextBoxColumn StateColumn(string name, string header, int width = 70) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        DefaultCellStyle = new DataGridViewCellStyle
        {
            Alignment = DataGridViewContentAlignment.MiddleCenter,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size + 2, FontStyle.Bold)
        }
    };

    private static void ApplyCellStyle(DataGridViewCell cell, PermissionMatrixCellState state)
    {
        cell.ToolTipText = state switch
        {
            PermissionMatrixCellState.Passed => "所有已记录关联目标均通过",
            PermissionMatrixCellState.Denied => "至少一个关联目标明确拒绝",
            PermissionMatrixCellState.Indeterminate => "无法确定、跳过或 Provider 不支持无副作用验证",
            _ => "尚无该权限的检查记录，或该权限不适用"
        };
        cell.Style.ForeColor = state switch
        {
            PermissionMatrixCellState.Passed => Color.ForestGreen,
            PermissionMatrixCellState.Denied => Color.Firebrick,
            PermissionMatrixCellState.Indeterminate => Color.DarkOrange,
            _ => SystemColors.GrayText
        };
    }
}
