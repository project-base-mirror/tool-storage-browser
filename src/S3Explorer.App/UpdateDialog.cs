namespace S3Explorer.App;

internal sealed class UpdateDialog : Form
{
    public Uri? SelectedUri { get; private set; }

    public UpdateDialog(Version currentVersion, GitHubReleaseInfo release)
    {
        Text = "发现 S3 Explorer 新版本";
        Icon = UiIcons.CreateApplicationIcon();
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 500);
        MinimumSize = new Size(600, 430);
        ShowInTaskbar = false;

        var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Color.FromArgb(245, 248, 252), Padding = new Padding(22, 17, 22, 14) };
        var icon = new PictureBox
        {
            Image = UiIcons.Create(UiIconKind.Refresh, 42),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Location = new Point(20, 22),
            Size = new Size(52, 52)
        };
        var title = new Label
        {
            Text = $"S3 Explorer {release.TagName} 可用",
            Font = new Font(Font, FontStyle.Bold),
            Location = new Point(88, 20),
            AutoSize = true
        };
        var summary = new Label
        {
            Text = $"当前版本 {ShortVersion(currentVersion)}  →  最新版本 {ShortVersion(release.Version)}" +
                   (release.PublishedAt is { } date ? $"    发布于 {date.ToLocalTime():yyyy-MM-dd}" : string.Empty),
            ForeColor = Color.FromArgb(90, 103, 120),
            Location = new Point(88, 52),
            AutoSize = true
        };
        var privacy = new Label
        {
            Text = "更新检查只读取公开 GitHub Release 信息；安装由你确认。",
            ForeColor = Color.FromArgb(90, 103, 120),
            Location = new Point(88, 74),
            AutoSize = true
        };
        header.Controls.AddRange([icon, title, summary, privacy]);

        var notesLabel = new Label { Text = "版本说明", Dock = DockStyle.Top, Height = 34, Padding = new Padding(18, 11, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        var notes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = release.Notes,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(18),
            Font = new Font("Segoe UI", 9.5f)
        };
        var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 12) };
        notesHost.Controls.Add(notes);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 8),
            BackColor = Color.FromArgb(248, 250, 252)
        };
        var download = new Button { Text = release.PreferredDownload is null ? "打开下载页面" : "下载推荐版本", Width = 124, Height = 32 };
        download.Click += (_, _) => SelectAndClose(release.PreferredDownload ?? release.ReleasePage);
        var openRelease = new Button { Text = "查看 Release", Width = 108, Height = 32 };
        openRelease.Click += (_, _) => SelectAndClose(release.ReleasePage);
        var later = new Button { Text = "稍后提醒", DialogResult = DialogResult.Cancel, Width = 96, Height = 32 };
        buttons.Controls.AddRange([download, openRelease, later]);

        Controls.Add(notesHost);
        Controls.Add(notesLabel);
        Controls.Add(header);
        Controls.Add(buttons);
        AcceptButton = download;
        CancelButton = later;
    }

    private void SelectAndClose(Uri uri)
    {
        SelectedUri = uri;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string ShortVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
