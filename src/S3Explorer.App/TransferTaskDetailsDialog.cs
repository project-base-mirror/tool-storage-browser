using System.Text;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class TransferTaskDetailsDialog : Form
{
    private readonly RichTextBox _content = new()
    {
        Name = "TransferTaskDetailsContent",
        Dock = DockStyle.Fill,
        ReadOnly = true,
        WordWrap = false,
        DetectUrls = false,
        HideSelection = false,
        BackColor = SystemColors.Window
    };
    private readonly Button _copy = new()
    {
        Name = "CopyTransferTaskDetailsButton",
        Text = "复制全部",
        AutoSize = true,
        MinimumSize = new Size(96, 36)
    };
    private readonly Button _close = new()
    {
        Name = "CloseTransferTaskDetailsButton",
        Text = "关闭",
        DialogResult = DialogResult.Cancel,
        AutoSize = true,
        MinimumSize = new Size(88, 36)
    };

    public TransferTaskDetailsDialog(TransferTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);

        Name = "TransferTaskDetailsDialog";
        Text = "传输任务详细信息";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 560);
        MinimumSize = new Size(640, 460);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        footer.Controls.Add(_close);
        footer.Controls.Add(_copy);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };
        root.Controls.Add(_content);
        root.Controls.Add(footer);
        Controls.Add(root);

        _content.Font = new Font("Consolas", 9.5F);
        _content.Text = TransferTaskDetailsFormatter.Format(task);
        _copy.Click += (_, _) => Clipboard.SetText(_content.Text);
        CancelButton = _close;
    }
}

internal static class TransferTaskDetailsFormatter
{
    public static string Format(TransferTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var source = task.Direction switch
        {
            TransferDirection.Upload or TransferDirection.DeleteLocal => task.LocalPath,
            _ => S3Uri(task.ProfileName, task.Bucket, task.ObjectKey)
        };
        var target = task.Direction switch
        {
            TransferDirection.Upload => S3Uri(task.ProfileName, task.Bucket, task.ObjectKey),
            TransferDirection.Download => task.LocalPath,
            TransferDirection.Copy or TransferDirection.Move =>
                S3Uri(task.ProfileName, task.DestinationBucket, task.DestinationObjectKey),
            TransferDirection.DeleteRemote => "删除远端对象",
            TransferDirection.DeleteLocal => "删除本地文件",
            _ => string.Empty
        };

        var text = new StringBuilder()
            .AppendLine($"任务 ID：{task.Id}")
            .AppendLine($"批次 ID：{Value(task.BatchId)}")
            .AppendLine($"连接：{task.ProfileName} ({task.ProfileId})")
            .AppendLine($"方向：{DirectionText(task.Direction)}")
            .AppendLine($"状态：{StateText(task.State)}")
            .AppendLine($"来源：{source}")
            .AppendLine($"目标：{target}")
            .AppendLine($"Bucket：{task.Bucket}")
            .AppendLine($"对象 Key：{task.ObjectKey}")
            .AppendLine($"Version ID：{Value(task.VersionId)}")
            .AppendLine($"本地路径：{Value(task.LocalPath)}")
            .AppendLine($"总大小：{task.TotalBytes:N0} B")
            .AppendLine($"已传输：{task.TransferredBytes:N0} B")
            .AppendLine($"尝试次数：{task.AttemptCount:N0} / {task.MaxAttempts:N0}")
            .AppendLine($"创建时间：{LocalTime(task.CreatedAt)}")
            .AppendLine($"开始时间：{LocalTime(task.StartedAt)}")
            .AppendLine($"更新时间：{LocalTime(task.UpdatedAt)}")
            .AppendLine($"完成时间：{LocalTime(task.CompletedAt)}");

        if (task.Failure is { } failure)
        {
            text.AppendLine()
                .AppendLine("错误详情")
                .AppendLine($"类别：{failure.Category}")
                .AppendLine($"消息：{failure.SafeMessage}")
                .AppendLine($"HTTP 状态：{Value(failure.HttpStatusCode)}")
                .AppendLine($"服务代码：{Value(failure.ServiceCode)}")
                .AppendLine($"Request ID：{Value(failure.RequestId)}")
                .AppendLine($"可重试：{(failure.Retryable ? "是" : "否")}");
        }

        return text.ToString().TrimEnd();
    }

    private static string S3Uri(string profile, string bucket, string key) =>
        $"s3://{profile}/{bucket}/{key.TrimStart('/')}";

    private static string Value(object? value) =>
        value is null || string.IsNullOrWhiteSpace(value.ToString()) ? "—" : value.ToString()!;

    private static string LocalTime(DateTimeOffset? value) =>
        value is null ? "—" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

    private static string DirectionText(TransferDirection direction) => direction switch
    {
        TransferDirection.Upload => "上传",
        TransferDirection.Download => "下载",
        TransferDirection.Copy => "复制",
        TransferDirection.Move => "移动",
        TransferDirection.DeleteRemote => "删除远端",
        TransferDirection.DeleteLocal => "删除本地",
        _ => direction.ToString()
    };

    private static string StateText(TransferTaskState state) => state switch
    {
        TransferTaskState.Queued => "排队中",
        TransferTaskState.Running => "进行中",
        TransferTaskState.Paused => "已暂停",
        TransferTaskState.RetryPending => "等待重试",
        TransferTaskState.Interrupted => "已中断，可继续",
        TransferTaskState.Completed => "成功",
        TransferTaskState.Failed => "失败",
        TransferTaskState.Cancelled => "已取消",
        TransferTaskState.CleanupPending => "等待清理",
        _ => state.ToString()
    };
}
