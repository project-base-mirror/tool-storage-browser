using System.Text;

namespace S3Explorer.App;

internal sealed class LogViewerDialog : Form
{
    private const int MaxDisplayedBytes = 4 * 1024 * 1024;

    private readonly SimpleFileLogger _logger;
    private readonly TextBox _path = new()
    {
        Name = "LogViewerPath",
        ReadOnly = true,
        Dock = DockStyle.Fill
    };
    private readonly RichTextBox _content = new()
    {
        Name = "LogViewerContent",
        Dock = DockStyle.Fill,
        ReadOnly = true,
        DetectUrls = false,
        WordWrap = false,
        ScrollBars = RichTextBoxScrollBars.Both,
        BackColor = SystemColors.Window
    };
    private readonly Label _status = new()
    {
        Name = "LogViewerStatus",
        Text = "正在读取日志…",
        AutoSize = true,
        Anchor = AnchorStyles.Left
    };
    private readonly Button _refresh = new()
    {
        Name = "RefreshLogButton",
        Text = "刷新",
        AutoSize = true,
        MinimumSize = new Size(88, 36)
    };
    private readonly Button _copy = new()
    {
        Name = "CopyLogButton",
        Text = "复制内容",
        AutoSize = true,
        MinimumSize = new Size(104, 36),
        Enabled = false
    };
    private readonly Button _scrollToEnd = new()
    {
        Name = "ScrollLogEndButton",
        Text = "跳到末尾",
        AutoSize = true,
        MinimumSize = new Size(104, 36),
        Enabled = false
    };
    private readonly Button _close = new()
    {
        Name = "CloseLogViewerButton",
        Text = "关闭",
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
        MinimumSize = new Size(88, 36)
    };
    private CancellationTokenSource? _refreshCancellation;
    private bool _shownOnce;

    public LogViewerDialog(SimpleFileLogger logger)
    {
        _logger = logger;

        Name = "LogViewerDialog";
        Text = "查看日志";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 620);
        MinimumSize = new Size(700, 480);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        _path.Text = logger.CurrentLogPath;
        _content.Font = new Font("Consolas", 9.5F);

        _refresh.Click += async (_, _) => await RefreshLogAsync();
        _copy.Click += (_, _) => CopyContent();
        _scrollToEnd.Click += (_, _) => ScrollToEnd();
        Shown += async (_, _) =>
        {
            if (_shownOnce) return;
            _shownOnce = true;
            await RefreshLogAsync();
        };
        FormClosed += (_, _) =>
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
        };
        CancelButton = _close;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var introduction = new Label
        {
            Text = "应用日志直接显示在此窗口中。刷新可读取最新内容，不会调用外部文本工具。",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        };

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.Controls.Add(new Label
        {
            Text = "日志文件：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 6, 0)
        }, 0, 0);
        pathRow.Controls.Add(_path, 1, 0);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_status, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty
        };
        actions.Controls.AddRange([_refresh, _copy, _scrollToEnd, _close]);
        footer.Controls.Add(actions, 1, 0);

        root.Controls.Add(introduction, 0, 0);
        root.Controls.Add(pathRow, 0, 1);
        root.Controls.Add(_content, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
    }

    private async Task RefreshLogAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;
        var path = _logger.CurrentLogPath;

        _path.Text = path;
        _refresh.Enabled = false;
        _status.Text = "正在读取日志…";

        try
        {
            var snapshot = await LogFileReader.ReadAsync(path, MaxDisplayedBytes, cancellationToken);
            if (cancellationToken.IsCancellationRequested || IsDisposed) return;

            if (!snapshot.Exists)
            {
                _content.Text = "尚未生成今日日志。应用产生运行信息后，点击“刷新”即可查看。";
                _status.Text = "日志文件尚不存在";
                _copy.Enabled = false;
                _scrollToEnd.Enabled = false;
                return;
            }

            var truncationNotice = snapshot.IsTruncated
                ? $"—— 日志较大，仅显示末尾 {FormatSize(MaxDisplayedBytes)} ——{Environment.NewLine}"
                : string.Empty;
            _content.Text = truncationNotice + snapshot.Content;
            _copy.Enabled = _content.TextLength > 0;
            _scrollToEnd.Enabled = _content.TextLength > 0;
            _status.Text = $"已加载 {FormatSize(snapshot.Length)} · 最后修改 {snapshot.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            ScrollToEnd();
        }
        catch (OperationCanceledException)
        {
            // A newer refresh or closing the dialog superseded this read.
        }
        catch (Exception exception)
        {
            if (IsDisposed) return;
            _content.Text = $"读取日志失败。{Environment.NewLine}{Environment.NewLine}{exception.Message}";
            _status.Text = "读取失败";
            _copy.Enabled = true;
            _scrollToEnd.Enabled = false;
        }
        finally
        {
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                _refresh.Enabled = true;
        }
    }

    private void CopyContent()
    {
        var text = _content.SelectionLength > 0 ? _content.SelectedText : _content.Text;
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void ScrollToEnd()
    {
        _content.SelectionStart = _content.TextLength;
        _content.SelectionLength = 0;
        _content.ScrollToCaret();
        _content.Focus();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KiB";
        return $"{bytes / (1024d * 1024d):0.0} MiB";
    }
}

internal sealed record LogFileSnapshot(
    bool Exists,
    string Content,
    long Length,
    DateTime LastWriteTimeUtc,
    bool IsTruncated);

internal static class LogFileReader
{
    public static async Task<LogFileSnapshot> ReadAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var length = stream.Length;
            var start = Math.Max(0, length - maxBytes);
            var startsAtLineBoundary = true;
            if (start > 0)
            {
                stream.Position = start - 1;
                startsAtLineBoundary = stream.ReadByte() == '\n';
                stream.Position = start;
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024,
                leaveOpen: false);
            if (start > 0 && !startsAtLineBoundary)
                _ = await reader.ReadLineAsync(cancellationToken);
            var content = await reader.ReadToEndAsync(cancellationToken);

            return new LogFileSnapshot(
                true,
                content,
                length,
                File.GetLastWriteTimeUtc(path),
                start > 0);
        }
        catch (FileNotFoundException)
        {
            return new LogFileSnapshot(false, string.Empty, 0, DateTime.MinValue, false);
        }
        catch (DirectoryNotFoundException)
        {
            return new LogFileSnapshot(false, string.Empty, 0, DateTime.MinValue, false);
        }
    }
}
