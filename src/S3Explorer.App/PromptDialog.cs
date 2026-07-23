namespace S3Explorer.App;

internal static class PromptDialog
{
    public static string? Show(IWin32Window? owner, string title, string label, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(480, 145),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var caption = new Label { Text = label, AutoSize = true, Location = new Point(14, 16) };
        var textBox = new TextBox { Text = initial, Location = new Point(14, 43), Width = 450 };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(306, 94), Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(389, 94), Width = 75 };
        form.Controls.AddRange([caption, textBox, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { textBox.Focus(); textBox.SelectAll(); };
        return form.ShowDialog(owner) == DialogResult.OK ? textBox.Text : null;
    }
}
