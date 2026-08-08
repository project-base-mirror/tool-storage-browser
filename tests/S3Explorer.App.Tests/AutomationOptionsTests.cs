using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class AutomationOptionsTests
{
    [Fact]
    public void ParsesFixedAutomationArguments()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "s3explorer-automation-options"));
        var options = AutomationOptions.Parse(
        [
            "--automation-smoke",
            "--automation-state", Path.Combine(root, "state.json"),
            "--automation-report", Path.Combine(root, "report.json"),
            "--automation-screenshot", Path.Combine(root, "screenshot.png"),
            "--automation-data-dir", Path.Combine(root, "data"),
            "--automation-instance-key", "smoke_123.test"
        ]);

        Assert.True(options.Enabled);
        Assert.True(options.Smoke);
        Assert.Equal(Path.Combine(root, "state.json"), options.StatePath);
        Assert.Equal(Path.Combine(root, "report.json"), options.ReportPath);
        Assert.Equal(Path.Combine(root, "screenshot.png"), options.ScreenshotPath);
        Assert.Equal(Path.Combine(root, "data"), options.DataDirectory);
        Assert.Equal("smoke_123.test", options.InstanceKey);
    }

    [Fact]
    public void RejectsRelativeOutputPaths()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AutomationOptions.Parse(["--automation-state", "state.json"]));

        Assert.Contains("绝对路径", exception.Message);
    }

    [Fact]
    public void SmokeRequiresReportAndScreenshot()
    {
        var state = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "s3explorer-state.json"));

        var exception = Assert.Throws<ArgumentException>(() =>
            AutomationOptions.Parse(["--automation-smoke", "--automation-state", state]));

        Assert.Contains("--automation-report", exception.Message);
    }

    [Fact]
    public void RejectsUnknownArguments()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AutomationOptions.Parse(["--execute", "anything"]));

        Assert.Contains("不支持", exception.Message);
    }

    [Theory]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    public void RejectsUnsafeInstanceKeys(string value)
    {
        var state = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "s3explorer-state.json"));

        var exception = Assert.Throws<ArgumentException>(() =>
            AutomationOptions.Parse([
                "--automation-state", state,
                "--automation-instance-key", value
            ]));

        Assert.Contains("automation-instance-key", exception.Message);
    }
}
