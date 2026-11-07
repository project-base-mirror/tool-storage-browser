using S3Explorer.Updater;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class UpdaterMsiIntegrationTests
{
    public static bool HasArgumentProbe => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("S3EXPLORER_MSI_ARGUMENT_PROBE"));

    [Theory(Skip = "Requires the isolated Windows MSI argument probe fixture.", SkipUnless = nameof(HasArgumentProbe))]
    [Trait("Category", "UpdaterMsiIntegration")]
    [InlineData(@"C:\Program Files\S3 Explorer\S3Explorer.exe")]
    [InlineData(@"C:\My Apps\我的应用\S3Explorer.exe")]
    [InlineData(@"C:\S3Explorer.exe")]
    public void ProductionLauncherPassesExactPropertyToWindowsInstaller(string applicationPath)
    {
        var package = Environment.GetEnvironmentVariable("S3EXPLORER_MSI_ARGUMENT_PROBE")!;
        var logs = Environment.GetEnvironmentVariable("S3EXPLORER_MSI_ARGUMENT_LOGS")!;
        Assert.True(File.Exists(package));
        Directory.CreateDirectory(logs);
        var logPath = Path.Combine(logs, $"参数 probe {Guid.NewGuid():N}.log");
        // The probe fails BEFORE CostInitialize/InstallInitialize and writes no payload.
        // The test calls the exact production launcher, including elevation/serialization.
        using var installer = UpdateRunner.StartInstaller(package, logPath, applicationPath);
        if (!installer.WaitForExit(60000))
        {
            installer.Kill(entireProcessTree: true);
            throw new TimeoutException("MSI parser probe timed out.");
        }
        Assert.Equal(1603, installer.ExitCode);
        var log = File.ReadAllText(logPath);
        Assert.Contains($"S3EXPLORER_MSI_ARGUMENT_PROBE:{Path.GetDirectoryName(applicationPath)}:END", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Doing action: InstallInitialize", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Executing op: FileCopy", log, StringComparison.Ordinal);
    }
}
