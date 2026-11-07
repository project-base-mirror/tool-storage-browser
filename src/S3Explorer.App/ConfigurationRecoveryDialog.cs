using System.Security.Cryptography;
using System.Text.Json;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Configuration;

namespace S3Explorer.App;

internal sealed class ConfigurationRecoveryDialog : Form
{
    private readonly string _dataRoot;
    private readonly Button _restore = new() { Name = "RestoreConfiguration", Text = "选择配置备份…", AutoSize = true };
    private readonly Button _reset = new() { Name = "ResetConfiguration", Text = "创建空配置…", AutoSize = true };
    private readonly Button _exit = new() { Name = "ExitRecovery", Text = "退出", AutoSize = true, DialogResult = DialogResult.Cancel };
    private bool _busy;

    public ExplorerConfigurationStore? RecoveredStore { get; private set; }

    public static bool CanOfferRecovery(string dataRoot, Exception failure)
    {
        if (failure is not (JsonException or InvalidDataException or InvalidOperationException or
            ArgumentException or FormatException or CryptographicException)) return false;
        var path = Path.Combine(dataRoot, "configuration.json");
        return !File.Exists(path) && !File.Exists(path + ".bak") && new DurableJsonFile(path).HasPersistedState;
    }

    public ConfigurationRecoveryDialog(string dataRoot, Exception error)
    {
        _dataRoot = dataRoot;
        Text = "恢复 S3 Explorer 配置";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 370);
        MinimumSize = new Size(600, 350);
        AutoScaleMode = AutoScaleMode.Font;
        ShowInTaskbar = true;
        MaximizeBox = false;
        MinimizeBox = false;
        CancelButton = _exit;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true, Dock = DockStyle.Fill,
            Text = "主配置和备份无法读取，原文件已保留，尚未创建空配置。\n\n" +
                   "可选择此 Windows 用户能够解密的 configuration.json 或 .bak 备份。\n" +
                   "也可明确创建空配置后重新添加连接或导入连接包；不会删除保留文件或远端数据。",
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        layout.Controls.Add(new TextBox
        {
            Name = "RecoveryDetails", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = $"保留位置：{dataRoot}\r\n文件名：configuration.json.corrupt-*\r\n\r\n{error.Message}"
        }, 0, 1);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0)
        };
        actions.Controls.AddRange([_exit, _reset, _restore]);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        _restore.Click += async (_, _) => await RestoreAsync();
        _reset.Click += async (_, _) => await ResetAsync();
        FormClosing += (_, args) => { if (_busy) args.Cancel = true; };
    }

    private async Task RestoreAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "选择统一加密配置或备份", InitialDirectory = _dataRoot,
            Filter = "统一配置或备份|*.json;*.bak|所有文件|*.*", CheckFileExists = true, Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await RecoverAsync(() => ConfigurationRecovery.RestoreFromFileAsync(_dataRoot, picker.FileName));
    }

    private async Task ResetAsync()
    {
        if (MessageBox.Show(this,
                "确定创建空配置并打开程序？现有连接和凭据将不会出现在新配置中。\n" +
                "损坏文件会继续保留，之后仍可恢复有效备份或导入连接包。远端对象不会改变。",
                "确认创建空配置", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        await RecoverAsync(() => ExplorerConfigurationStore.CreateOrReplaceAsync(_dataRoot, ExplorerConfiguration.Empty));
    }

    private async Task RecoverAsync(Func<Task<ExplorerConfigurationStore>> recover)
    {
        _busy = true;
        _restore.Enabled = _reset.Enabled = _exit.Enabled = false;
        try
        {
            RecoveredStore = await recover();
            _busy = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"恢复未完成，保留的原文件未被删除。\n\n{exception.Message}",
                "配置恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _restore.Enabled = _reset.Enabled = _exit.Enabled = true;
        }
    }
}
