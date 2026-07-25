using System.Diagnostics;
using System.Text.Json;

namespace S3Explorer.App;

internal sealed class AutomationSession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AutomationOptions _options;
    private bool _failed;

    public AutomationSession(AutomationOptions options)
    {
        _options = options;
        WriteState("starting", null, null);
    }

    public void Ready(MainForm form)
    {
        try
        {
            var report = form.BuildAutomationReport();
            if (!string.IsNullOrWhiteSpace(_options.ScreenshotPath))
                form.CaptureAutomationScreenshot(_options.ScreenshotPath);
            if (!string.IsNullOrWhiteSpace(_options.ReportPath))
                WriteAtomic(_options.ReportPath, report);

            WriteState("ready", form, report);
            if (_options.Smoke)
            {
                Environment.ExitCode = report.Passed ? 0 : 1;
                form.BeginInvoke(new Action(form.Close));
            }
        }
        catch (Exception exception)
        {
            Fail(form, exception);
        }
    }

    public void Fail(Form? form, Exception exception)
    {
        _failed = true;
        Environment.ExitCode = 1;
        WriteState("failed", form, null, exception);
        if (form is { IsDisposed: false, IsHandleCreated: true })
            form.BeginInvoke(new Action(form.Close));
    }

    public void MarkStopped(Form form)
    {
        if (!_failed && !_options.Smoke)
            WriteState("stopped", form, null);
    }

    private void WriteState(string status, Form? form, AutomationReport? report, Exception? exception = null)
    {
        using var process = Process.GetCurrentProcess();
        var state = new AutomationState(
            status,
            Environment.ProcessId,
            process.StartTime.ToUniversalTime(),
            Environment.ProcessPath ?? string.Empty,
            Application.ProductVersion,
            form?.Text ?? string.Empty,
            form?.IsHandleCreated == true ? form.Handle.ToInt64() : 0,
            report?.Passed ?? false,
            _options.ReportPath,
            _options.ScreenshotPath,
            exception is null ? string.Empty : $"{exception.GetType().Name}: {exception.Message}");
        WriteAtomic(_options.StatePath, state);
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("自动化输出路径必须包含目录。");

        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, true);
    }
}
