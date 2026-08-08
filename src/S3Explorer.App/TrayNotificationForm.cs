namespace S3Explorer.App;

internal sealed class TrayNotificationForm : Form
{
    private const int WindowMargin = 12;
    private const int ExtendedStyleToolWindow = 0x00000080;
    private const int ExtendedStyleNoActivate = 0x08000000;
    private readonly System.Windows.Forms.Timer _closeTimer;
    private readonly Action _activated;
    private readonly Image _statusImage;
    private readonly Font _titleFont;

    public TrayNotificationForm(
        string title,
        string message,
        bool warning,
        Action activated,
        TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(activated);

        _activated = activated;
        _statusImage = (warning ? SystemIcons.Warning : SystemIcons.Information).ToBitmap();
        _titleFont = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold);
        Name = "TrayTransferNotification";
        Text = title;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SystemColors.Window;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ClientSize = new Size(360, 96);
        Padding = new Padding(1);

        var icon = new PictureBox
        {
            Name = "TrayNotificationIcon",
            Image = _statusImage,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill,
            Margin = new Padding(12)
        };
        var titleLabel = new Label
        {
            Name = "TrayNotificationTitle",
            Text = title,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = _titleFont,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0, 10, 12, 0)
        };
        var messageLabel = new Label
        {
            Name = "TrayNotificationMessage",
            Text = message,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 3, 12, 10)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);
        layout.Controls.Add(titleLabel, 1, 0);
        layout.Controls.Add(messageLabel, 1, 1);
        Controls.Add(layout);

        foreach (Control control in new Control[] { this, layout, icon, titleLabel, messageLabel })
        {
            control.Cursor = Cursors.Hand;
            control.Click += (_, _) => ActivateApplication();
        }

        var effectiveDuration = duration ?? TimeSpan.FromSeconds(5);
        _closeTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp((int)effectiveDuration.TotalMilliseconds, 1000, 30000)
        };
        _closeTimer.Tick += (_, _) => Close();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ExtendedStyleToolWindow | ExtendedStyleNoActivate;
            return parameters;
        }
    }

    internal static Point CalculateLocation(Rectangle workingArea, Size windowSize) =>
        new(
            Math.Max(workingArea.Left, workingArea.Right - windowSize.Width - WindowMargin),
            Math.Max(workingArea.Top, workingArea.Bottom - windowSize.Height - WindowMargin));

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = CalculateLocation(workingArea, Size);
        _closeTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.ActiveBorder, ButtonBorderStyle.Solid);
    }

    private void ActivateApplication()
    {
        _closeTimer.Stop();
        Close();
        _activated();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Dispose();
            _statusImage.Dispose();
            _titleFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
