using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class CdnDownloadTestDialog : Form
{
    private readonly ICdnDeliveryService _deliveryService;
    private readonly CdnProfile _profile;
    private readonly CdnCredential? _credential;
    private readonly Uri _url;
    private readonly TextBox _urlBox = new()
    {
        Name = "CdnDownloadTestUrl",
        ReadOnly = true,
        Dock = DockStyle.Fill
    };
    private readonly NumericUpDown _sampleMiB = new()
    {
        Name = "CdnDownloadSampleMiB",
        Minimum = 1,
        Maximum = 1024,
        Value = 4,
        Width = 100
    };
    private readonly NumericUpDown _timeoutSeconds = new()
    {
        Name = "CdnDownloadTimeoutSeconds",
        Minimum = 1,
        Maximum = 3600,
        Value = 100,
        Width = 90
    };
    private readonly Label _status = new()
    {
        Name = "CdnDownloadTestStatus",
        AutoSize = true,
        Text = "尚未测试"
    };
    private readonly TableLayoutPanel _results = new()
    {
        Name = "CdnDownloadTestResults",
        Dock = DockStyle.Fill,
        AutoSize = true,
        ColumnCount = 2
    };
    private readonly TextBox _headers = new()
    {
        Name = "CdnDownloadResponseHeaders",
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false
    };
    private readonly Button _test = new()
    {
        Name = "RunCdnDownloadTestButton",
        Text = "开始测试",
        AutoSize = true,
        MinimumSize = new Size(108, 36)
    };
    private readonly Button _cancel = new()
    {
        Name = "CancelCdnDownloadTestButton",
        Text = "取消测试",
        AutoSize = true,
        MinimumSize = new Size(104, 36),
        Enabled = false
    };
    private readonly Button _close = new()
    {
        Name = "CloseCdnDownloadTestButton",
        Text = "关闭",
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
        MinimumSize = new Size(96, 36)
    };
    private readonly Dictionary<string, Label> _valueLabels = new(StringComparer.Ordinal);
    private CancellationTokenSource? _testCancellation;
    private bool _shownOnce;

    public CdnDownloadTestDialog(
        ICdnDeliveryService deliveryService,
        CdnProfile profile,
        CdnCredential? credential,
        Uri url)
    {
        _deliveryService = deliveryService;
        _profile = profile;
        _credential = credential;
        _url = url;

        Name = "CdnDownloadTestDialog";
        Text = $"CDN 下载测试 - {profile.Name}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 660);
        MinimumSize = new Size(700, 560);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        _urlBox.Text = url.AbsoluteUri;
        _timeoutSeconds.Value = Math.Clamp(
            profile.TimeoutSeconds,
            decimal.ToInt32(_timeoutSeconds.Minimum),
            decimal.ToInt32(_timeoutSeconds.Maximum));
        _test.Click += async (_, _) => await RunTestAsync();
        _cancel.Click += (_, _) => CancelTest();
        Shown += async (_, _) =>
        {
            if (_shownOnce) return;
            _shownOnce = true;
            await RunTestAsync();
        };
        FormClosing += (_, _) => _testCancellation?.Cancel();
        AcceptButton = _test;
        CancelButton = _close;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var urlRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        urlRow.Controls.Add(new Label { Text = "最终请求 URL：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        urlRow.Controls.Add(_urlBox, 1, 0);

        var optionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 7,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        optionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        optionRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        optionRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        optionRow.Controls.Add(new Label
        {
            Text = "Range GET 样本：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 3, 0)
        }, 0, 0);
        optionRow.Controls.Add(_sampleMiB, 1, 0);
        optionRow.Controls.Add(new Label
        {
            Text = "MiB",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 0, 0)
        }, 2, 0);
        optionRow.Controls.Add(new Label
        {
            Text = "测试超时：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 7, 3, 0)
        }, 4, 0);
        optionRow.Controls.Add(_timeoutSeconds, 5, 0);
        optionRow.Controls.Add(new Label
        {
            Text = "秒",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 0, 0)
        }, 6, 0);
        var optionHelp = new Label
        {
            Text = "服务端忽略 Range 时最多只读取设定样本大小；到达测试超时后会主动取消请求。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 5, 0, 0)
        };
        optionRow.Controls.Add(optionHelp, 0, 1);
        optionRow.SetColumnSpan(optionHelp, 7);

        _results.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var item in new[]
        {
            ("status", "HTTP 状态"),
            ("final", "最终 URL"),
            ("ttfb", "响应头耗时"),
            ("elapsed", "总耗时"),
            ("bytes", "读取字节"),
            ("speed", "平均吞吐"),
            ("type", "Content-Type"),
            ("length", "Content-Length"),
            ("cache", "缓存状态")
        })
        {
            var row = _results.RowCount++;
            _results.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _results.Controls.Add(new Label
            {
                Text = item.Item2 + "：",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(0, 4, 8, 4)
            }, 0, row);
            var value = new Label
            {
                Name = $"CdnDownloadResult_{item.Item1}",
                Text = "-",
                AutoSize = true,
                MaximumSize = new Size(610, 0),
                Margin = new Padding(0, 4, 0, 4)
            };
            _valueLabels[item.Item1] = value;
            _results.Controls.Add(value, 1, row);
        }

        var headersGroup = new GroupBox
        {
            Text = "响应 Header",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        headersGroup.Controls.Add(_headers);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Anchor = AnchorStyles.Left;
        _status.Margin = new Padding(0, 10, 12, 0);
        footer.Controls.Add(_status, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        actions.Controls.Add(_close);
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_test);
        footer.Controls.Add(actions, 1, 0);

        root.Controls.Add(urlRow, 0, 0);
        root.Controls.Add(optionRow, 0, 1);
        root.Controls.Add(new Label
        {
            Text = "测试使用真实 GET + Range 请求，记录响应头耗时、下载吞吐和常见 CDN 缓存 Header。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 2);
        root.Controls.Add(_results, 0, 3);
        root.Controls.Add(headersGroup, 0, 4);
        root.Controls.Add(footer, 0, 5);
        Controls.Add(root);
    }

    private async Task RunTestAsync()
    {
        if (_testCancellation is not null) return;

        var operationCancellation = new CancellationTokenSource();
        _testCancellation = operationCancellation;
        var timeoutSeconds = decimal.ToInt32(_timeoutSeconds.Value);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellation.Token);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        SetTesting(true);
        _status.Text = $"正在测试（最多 {timeoutSeconds} 秒）…";
        _status.ForeColor = SystemColors.ControlText;
        _headers.Clear();
        foreach (var key in _valueLabels.Keys)
            SetValue(key, "-");

        try
        {
            var sampleBytes = decimal.ToInt64(_sampleMiB.Value) * 1024L * 1024L;
            var result = await _deliveryService.ProbeAsync(
                _profile with { TimeoutSeconds = timeoutSeconds },
                _credential,
                _url,
                sampleBytes,
                requestCancellation.Token);

            SetValue("status", $"{result.StatusCode} {result.ReasonPhrase}");
            SetValue("final", result.FinalUrl.AbsoluteUri);
            SetValue("ttfb", $"{result.TimeToHeaders.TotalMilliseconds:F0} ms");
            SetValue("elapsed", $"{result.TotalElapsed.TotalMilliseconds:F0} ms");
            SetValue("bytes", FormatBytes(result.BytesRead));
            SetValue("speed", $"{FormatBytes((long)result.BytesPerSecond)}/s");
            SetValue("type", result.ContentType ?? "-");
            SetValue("length", result.ContentLength is long length ? FormatBytes(length) : "-");
            SetValue("cache", result.CacheStatus);
            _headers.Text = string.Join(
                Environment.NewLine,
                result.Headers.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(value => $"{value.Key}: {value.Value}"));
            _status.Text = result.Success ? "测试完成" : "请求已完成，但状态码表示失败";
            _status.ForeColor = result.Success ? Color.DarkGreen : Color.DarkRed;
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            if (!IsDisposed)
            {
                _status.Text = "测试已取消";
                _status.ForeColor = SystemColors.ControlText;
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                _status.Text = $"测试超时（{timeoutSeconds} 秒）";
                _status.ForeColor = Color.DarkRed;
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                _status.Text = "测试失败";
                _status.ForeColor = Color.DarkRed;
                ErrorDialog.ShowException(this, "CDN 下载测试失败", _profile.Name, exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_testCancellation, operationCancellation))
                _testCancellation = null;
            operationCancellation.Dispose();
            if (!IsDisposed)
                SetTesting(false);
        }
    }

    private void CancelTest()
    {
        if (_testCancellation is null || _testCancellation.IsCancellationRequested) return;
        _status.Text = "正在取消测试…";
        _status.ForeColor = SystemColors.ControlText;
        _cancel.Enabled = false;
        _testCancellation.Cancel();
    }

    private void SetTesting(bool testing)
    {
        _test.Enabled = !testing;
        _cancel.Enabled = testing;
        _sampleMiB.Enabled = !testing;
        _timeoutSeconds.Enabled = !testing;
    }

    private void SetValue(string key, string value) => _valueLabels[key].Text = value;

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:N2} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024d:N2} MiB";
        return $"{bytes / 1024d / 1024d / 1024d:N2} GiB";
    }
}
