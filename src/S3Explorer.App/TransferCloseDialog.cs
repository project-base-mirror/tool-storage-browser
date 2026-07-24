namespace S3Explorer.App;

internal enum TransferCloseAction { Wait, Pause, Cancel, Return }

internal sealed class TransferCloseDialog : Form
{
    public TransferCloseAction SelectedAction { get; private set; } = TransferCloseAction.Return;

    public TransferCloseDialog(int activeCount)
    {
        Text = "仍有传输任务";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 245);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = $"当前有 {activeCount} 个排队或运行中的传输任务。请选择关闭策略：",
            AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "等待完成：保持窗口打开直到当前任务结束。\n暂停并退出：保存队列，下次启动可继续。\n取消并退出：取消所有未完成任务。\n返回程序：不关闭窗口。",
            AutoSize = true, MaximumSize = new Size(520, 0)
        }, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        buttons.Controls.Add(CreateButton("返回程序", TransferCloseAction.Return));
        buttons.Controls.Add(CreateButton("取消并退出", TransferCloseAction.Cancel));
        buttons.Controls.Add(CreateButton("暂停并退出", TransferCloseAction.Pause));
        buttons.Controls.Add(CreateButton("等待完成", TransferCloseAction.Wait));
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
    }

    private Button CreateButton(string text, TransferCloseAction action)
    {
        var button = new Button { Text = text, Size = new Size(116, 34), Margin = new Padding(6) };
        button.Click += (_, _) =>
        {
            SelectedAction = action;
            DialogResult = action == TransferCloseAction.Return ? DialogResult.Cancel : DialogResult.OK;
            Close();
        };
        return button;
    }
}
