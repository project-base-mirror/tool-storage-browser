using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class UpdateDownloadDialog : Form
{
    private readonly UpdateDownloadService _service;
    private readonly GitHubReleaseInfo _release;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 48, Padding = new Padding(16, 16, 16, 0) };
    private readonly Label _detail = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(16, 4, 16, 0), ForeColor = Color.FromArgb(90, 103, 120) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 24, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
    private readonly Button _cancel = new() { Text = "取消", Width = 90, Height = 30 };
    private bool _finished;

    public VerifiedUpdatePackage? Package { get; private set; }
    public Exception? Failure { get; private set; }

    public UpdateDownloadDialog(UpdateDownloadService service, GitHubReleaseInfo release)
    {
        _service = service;
        _release = release;
        Text = $"下载 S3 Explorer {release.TagName}";
        Icon = UiIcons.CreateApplicationIcon();
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 178);
        MinimumSize = new Size(520, 178);
        MaximumSize = new Size(900, 220);
        ShowInTaskbar = false;
        ControlBox = false;

        var progressHost = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(16, 8, 16, 8) };
        progressHost.Controls.Add(_progress);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        _cancel.Click += (_, _) => CancelDownload();
        buttons.Controls.Add(_cancel);
        Controls.Add(buttons);
        Controls.Add(progressHost);
        Controls.Add(_detail);
        Controls.Add(_status);

        Shown += async (_, _) => await DownloadAsync();
        FormClosing += (_, args) =>
        {
            if (_finished) return;
            args.Cancel = true;
            CancelDownload();
        };
    }

    private async Task DownloadAsync()
    {
        var progress = new Progress<UpdateDownloadProgress>(UpdateProgress);
        try
        {
            Package = await _service.DownloadAsync(_release, progress, _cancellation.Token);
            _finished = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _finished = true;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception exception)
        {
            Failure = exception;
            _finished = true;
            DialogResult = DialogResult.Abort;
            Close();
        }
    }

    private void UpdateProgress(UpdateDownloadProgress value)
    {
        if (_finished || IsDisposed) return;
        _status.Text = value.Stage;
        if (value.TotalBytes is > 0)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Value = (int)Math.Clamp(value.BytesReceived * 100 / value.TotalBytes.Value, 0, 100);
            _detail.Text = $"{FileSizeFormatter.Format(value.BytesReceived)} / {FileSizeFormatter.Format(value.TotalBytes.Value)}";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 30;
            _detail.Text = value.BytesReceived > 0 ? FileSizeFormatter.Format(value.BytesReceived) : string.Empty;
        }
    }

    private void CancelDownload()
    {
        if (_cancellation.IsCancellationRequested) return;
        _cancel.Enabled = false;
        _cancel.Text = "正在取消...";
        _status.Text = "正在取消更新下载...";
        _cancellation.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cancellation.Dispose();
        base.Dispose(disposing);
    }
}
