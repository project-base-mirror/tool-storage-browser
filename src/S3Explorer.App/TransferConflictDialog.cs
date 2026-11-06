using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class TransferConflictDialog : Form
{
    private readonly ComboBox _policy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private sealed record Choice(string Text, ObjectConflictPolicy Policy)
    {
        public override string ToString() => Text;
    }

    public TransferConflictDialog(string title, ObjectConflictPolicy defaultPolicy, bool queued = false)
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
            new Choice("自动重命名，保留两份", ObjectConflictPolicy.AutoRename)
        ]);
        if (queued) _policy.Items.RemoveAt(0);
        _policy.SelectedItem = _policy.Items.Cast<Choice>().FirstOrDefault(choice => choice.Policy == defaultPolicy)
            ?? _policy.Items.Cast<Choice>().First(choice => choice.Policy == ObjectConflictPolicy.Skip);
        layout.Controls.Add(_policy);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(450, 0), Padding = new Padding(0, 8, 0, 8),
            Text = "仅在目标冲突时询问，所选策略用于当前目标。占用中的队列目标不能覆盖；批量下载中名称映射冲突会自动保留两份。兼容存储会在写入前再次检查目标。" });
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
