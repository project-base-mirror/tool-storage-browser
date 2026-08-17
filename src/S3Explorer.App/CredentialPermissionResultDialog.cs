using System.Text;
using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class CredentialPermissionResultDialog : Form
{
    public CredentialPermissionResultDialog(CredentialProfile credential, PermissionCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(report);

        Name = nameof(CredentialPermissionResultDialog);
        Text = $"权限检查 - {credential.Name}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(840, 570);
        MinimumSize = new Size(680, 460);
        ShowInTaskbar = false;
        Icon = UiIcons.CreateApplicationIcon();
        AutoScaleMode = AutoScaleMode.Font;

        var summary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = $"凭据：{credential.Name} · {credential.Provider} / {credential.Kind} · {credential.Fingerprint}\n" +
                   $"检查 {report.TotalChecks:N0} 项：通过 {report.PassedCount:N0}，拒绝 {report.DeniedCount:N0}，" +
                   $"无法确定 {report.IndeterminateCount:N0}，不支持 {report.UnsupportedCount:N0}，跳过 {report.SkippedCount:N0}",
            Margin = new Padding(0, 0, 0, 10)
        };
        var explanation = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text = "本页只执行无副作用检查。Put/Delete/ACL 与 CDN 刷新写权限显示“无法确定”是预期结果；需要显式写入探针时请使用 CLI permission check --probe-write --yes。",
            MaximumSize = new Size(790, 0),
            Margin = new Padding(0, 0, 0, 10)
        };
        var details = new TextBox
        {
            Name = "CredentialPermissionDetailsTextBox",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Text = FormatReport(report)
        };
        var close = new Button
        {
            Name = "CloseCredentialPermissionResultButton",
            Text = "关闭",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(96, 36)
        };
        var copy = new Button
        {
            Name = "CopyCredentialPermissionResultButton",
            Text = "复制结果",
            AutoSize = true,
            MinimumSize = new Size(108, 36)
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(details.Text); }
            catch (Exception exception)
            {
                MessageBox.Show(this, SensitiveDataRedactor.Redact(exception.Message), "无法复制",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        actions.Controls.Add(close);
        actions.Controls.Add(copy);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(summary, 0, 0);
        layout.Controls.Add(explanation, 0, 1);
        layout.Controls.Add(details, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        AcceptButton = close;
        CancelButton = close;
    }

    private static string FormatReport(PermissionCheckReport report)
    {
        var text = new StringBuilder();
        foreach (var result in report.Results)
        {
            text.AppendLine($"[{result.TargetScope}]  {result.CheckedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            foreach (var check in result.Checks)
            {
                text.Append("  ").Append(StateText(check.State)).Append("  ")
                    .Append(check.Subject).Append('/').Append(check.Name);
                if (!check.Required) text.Append("（非阻断）");
                if (check.StatusCode is not null) text.Append("  HTTP ").Append(check.StatusCode.Value);
                if (!string.IsNullOrWhiteSpace(check.ProviderCode)) text.Append("  code=").Append(check.ProviderCode);
                if (!string.IsNullOrWhiteSpace(check.RequestId)) text.Append("  requestId=").Append(check.RequestId);
                text.AppendLine();
                if (!string.IsNullOrWhiteSpace(check.Message))
                    text.Append("      ").AppendLine(check.Message);
            }
            text.AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    private static string StateText(PermissionCheckState state) => state switch
    {
        PermissionCheckState.Passed => "[通过]",
        PermissionCheckState.Denied => "[拒绝]",
        PermissionCheckState.Unsupported => "[不支持]",
        PermissionCheckState.Skipped => "[跳过]",
        _ => "[无法确定]"
    };
}
