using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class TransferConflictDialog : Form
{
    private readonly ComboBox _policy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private sealed record Choice(string Text, ObjectConflictPolicy Policy)
    {
        public override string ToString() => Text;
    }

    public TransferConflictDialog(string title, ObjectConflictPolicy defaultPolicy)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(490, 180);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _policy.Items.AddRange([
            new Choice("覆盖已有目标", ObjectConflictPolicy.Overwrite),
            new Choice("跳过已有目标", ObjectConflictPolicy.Skip),
            new Choice("自动重命名，保留两份", ObjectConflictPolicy.AutoRename),
            new Choice("逐项询问", ObjectConflictPolicy.Ask)
        ]);
        _policy.SelectedIndex = (int)defaultPolicy;
        layout.Controls.Add(_policy);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(450, 0), Padding = new Padding(0, 8, 0, 8),
            Text = "策略应用于本次文件及文件夹传输。占用中的队列目标不能覆盖；批量下载中名称映射冲突会自动保留两份。非覆盖上传需要服务端支持可靠条件写入。" });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public ObjectConflictPolicy Policy => ((Choice)_policy.SelectedItem!).Policy;
}
