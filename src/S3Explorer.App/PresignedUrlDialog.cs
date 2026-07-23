using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class PresignedUrlDialog : Form
{
    private readonly NumericUpDown _amount = new() { Minimum = 1, Maximum = 10080, Value = 1 };
    private readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _url = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _expires = new() { AutoSize = true };
    private readonly Func<TimeSpan, string> _generator;

    public PresignedUrlDialog(string location, Func<TimeSpan, string> generator)
    {
        _generator = generator;
        Text = "生成预签名 URL";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(730, 360);
        MinimumSize = new Size(650, 330);
        ShowInTaskbar = false;
        _unit.Items.AddRange(["分钟", "小时", "天"]);
        _unit.SelectedIndex = 1;

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "对象：", AutoSize = true }, 0, 0);
        table.Controls.Add(new TextBox { Text = location, ReadOnly = true, Dock = DockStyle.Fill }, 1, 0);

        var lifetime = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        lifetime.Controls.AddRange([_amount, _unit]);
        table.Controls.Add(new Label { Text = "有效期：", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        table.Controls.Add(lifetime, 1, 1);

        table.Controls.Add(new Label { Text = "URL：", AutoSize = true }, 0, 2);
        _url.Dock = DockStyle.Fill;
        table.Controls.Add(_url, 1, 2);
        table.Controls.Add(new Label { Text = "过期时间：", AutoSize = true }, 0, 3);
        table.Controls.Add(_expires, 1, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.OK, Width = 85 };
        var open = new Button { Text = "在浏览器打开", AutoSize = true };
        var copy = new Button { Text = "复制 URL", AutoSize = true };
        var generate = new Button { Text = "生成", Width = 85 };
        generate.Click += (_, _) => Generate();
        copy.Click += (_, _) => { if (_url.TextLength > 0) Clipboard.SetText(_url.Text); };
        open.Click += (_, _) =>
        {
            if (_url.TextLength > 0)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_url.Text) { UseShellExecute = true });
        };
        buttons.Controls.AddRange([close, open, copy, generate]);
        table.Controls.Add(buttons, 0, 4);
        table.SetColumnSpan(buttons, 2);
        Controls.Add(table);
        AcceptButton = generate;
        CancelButton = close;
    }

    private void Generate()
    {
        var amount = (double)_amount.Value;
        var lifetime = _unit.SelectedIndex switch
        {
            0 => TimeSpan.FromMinutes(amount),
            2 => TimeSpan.FromDays(amount),
            _ => TimeSpan.FromHours(amount)
        };
        try
        {
            _url.Text = _generator(lifetime);
            _expires.Text = DateTimeOffset.Now.Add(lifetime).LocalDateTime.ToString("G");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法生成 URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
