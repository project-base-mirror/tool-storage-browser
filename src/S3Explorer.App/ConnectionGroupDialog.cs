using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class ConnectionGroupDialog : Form
{
    private readonly TextBox _name = new() { Name = "ConnectionGroupNameTextBox", Dock = DockStyle.Fill };

    public string GroupName => _name.Text.Trim();

    public ConnectionGroupDialog(ConnectionGroup? group = null)
    {
        Text = group is null ? "新建连接分组" : "重命名连接分组";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 132);
        MinimumSize = new Size(380, 170);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "分组名称：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_name, 1, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Size = new Size(88, 30) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(88, 30) };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons, 0, 1);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        _name.Text = group?.Name ?? string.Empty;
        ok.Click += (_, _) => ValidateName();
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void ValidateName()
    {
        try
        {
            new ConnectionGroup { Name = GroupName }.Validate();
        }
        catch (Exception exception)
        {
            DialogResult = DialogResult.None;
            MessageBox.Show(this, exception.Message, "分组名称无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
