using Amazon.S3;

namespace S3Explorer.App;

internal sealed class ErrorDialog : Form
{
    private ErrorDialog(string title, string operation, string location, Exception exception)
    {
        Text = "操作失败";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 410);
        MinimumSize = new Size(560, 360);
        ShowInTaskbar = false;

        var type = exception is AmazonS3Exception s3 ? s3.ErrorCode : exception.GetType().Name;
        var status = exception is AmazonS3Exception aws ? $"{(int)aws.StatusCode} {aws.StatusCode}" : "—";
        var requestId = exception is AmazonS3Exception request ? request.RequestId : "—";
        var suggestion = Suggest(exception);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = 8
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, 0, "操作：", operation);
        AddRow(table, 1, "位置：", location);
        AddRow(table, 2, "错误类型：", type ?? title);
        AddRow(table, 3, "HTTP 状态：", status);
        AddRow(table, 4, "Request ID：", requestId ?? "—");
        AddRow(table, 5, "消息：", exception.Message);
        AddRow(table, 6, "建议：", suggestion);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.OK, Width = 85 };
        var copy = new Button { Text = "复制详细信息", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(
            $"操作: {operation}\r\n位置: {location}\r\n类型: {type}\r\nHTTP: {status}\r\nRequestId: {requestId}\r\n消息: {exception.Message}\r\n建议: {suggestion}");
        buttons.Controls.AddRange([close, copy]);
        table.Controls.Add(buttons, 0, 7);
        table.SetColumnSpan(buttons, 2);
        Controls.Add(table);
        AcceptButton = close;
        CancelButton = close;
    }

    public static void ShowException(IWin32Window? owner, string title, string operation, Exception exception, string location = "")
    {
        using var dialog = new ErrorDialog(title, operation, location, exception);
        dialog.ShowDialog(owner);
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, string value)
    {
        table.RowStyles.Add(new RowStyle(row == 5 || row == 6 ? SizeType.Percent : SizeType.AutoSize, row == 5 || row == 6 ? 50 : 0));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) }, 0, row);
        table.Controls.Add(new TextBox { Text = value, ReadOnly = true, BorderStyle = BorderStyle.None, Multiline = true, Dock = DockStyle.Fill, BackColor = SystemColors.Control, Margin = new Padding(3, 6, 3, 3) }, 1, row);
    }

    private static string Suggest(Exception exception)
    {
        if (exception is OperationCanceledException) return "操作已取消。";
        if (exception is AmazonS3Exception s3)
        {
            return s3.ErrorCode switch
            {
                "AccessDenied" => "检查当前凭据是否拥有对应的 S3 权限，以及 Bucket Policy 和 Public Access Block。",
                "InvalidAccessKeyId" => "检查 Access Key 是否正确且仍然有效。",
                "SignatureDoesNotMatch" => "检查 Secret Key、Region、Endpoint、系统时间和地址风格。",
                "NoSuchBucket" => "Bucket 不存在，或当前 Endpoint / Region 不正确。",
                "NoSuchKey" => "对象已不存在，请刷新当前目录。",
                "BucketNotEmpty" => "先删除对象、历史版本和未完成分片上传。",
                "SlowDown" => "服务端要求降低请求速率，稍后重试。",
                "RequestTimeTooSkewed" => "同步 Windows 系统时间后重试。",
                _ => "检查 Endpoint、Region、凭据、网络连接和服务端日志。"
            };
        }
        if (exception is IOException) return "检查本地磁盘空间、文件占用和目录写入权限。";
        return "查看日志获取完整信息，并检查网络与当前配置。";
    }
}
