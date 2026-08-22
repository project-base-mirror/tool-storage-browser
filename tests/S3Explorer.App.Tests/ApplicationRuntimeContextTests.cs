using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ApplicationRuntimeContextTests
{
    [Fact]
    public void DevelopmentRuntimeIsIsolatedFromInstalledApplication()
    {
        var roaming = Path.Combine(Path.GetTempPath(), "s3explorer-roaming");
        var local = Path.Combine(Path.GetTempPath(), "s3explorer-local");

        var development = ApplicationRuntimeContext.Resolve(
            new AutomationOptions(),
            developmentMode: true,
            roaming,
            local);
        var production = ApplicationRuntimeContext.Resolve(
            new AutomationOptions(),
            developmentMode: false,
            roaming,
            local);

        Assert.True(development.DevelopmentMode);
        Assert.NotEqual(production.InstanceKey, development.InstanceKey);
        Assert.NotEqual(production.DataRoot, development.DataRoot);
        Assert.Equal(ApplicationRuntimeContext.DevelopmentInstanceKey, development.InstanceKey);
        Assert.Equal(Path.Combine(roaming, ApplicationRuntimeContext.DevelopmentDataDirectoryName), development.DataRoot);
        Assert.Equal(Path.Combine(local, ApplicationRuntimeContext.DevelopmentDataDirectoryName), development.LocalDataRoot);
        Assert.Equal(SingleInstanceCoordinator.ApplicationInstanceKey, production.InstanceKey);
        Assert.Equal(Path.Combine(roaming, ApplicationRuntimeContext.ProductionDataDirectoryName), production.DataRoot);
        Assert.Equal(Path.Combine(local, ApplicationRuntimeContext.ProductionDataDirectoryName), production.LocalDataRoot);
    }

    [Fact]
    public void AutomationRuntimeKeepsItsExplicitIsolationContract()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "s3explorer-automation-runtime"));
        var options = AutomationOptions.Parse(
        [
            "--automation-state", Path.Combine(root, "state.json"),
            "--automation-data-dir", Path.Combine(root, "data"),
            "--automation-instance-key", "automation.test"
        ]);

        var runtime = ApplicationRuntimeContext.Resolve(options, developmentMode: true, root, root);

        Assert.False(runtime.DevelopmentMode);
        Assert.Equal("automation.test", runtime.InstanceKey);
        Assert.Equal(Path.Combine(root, "data"), runtime.DataRoot);
        Assert.Equal(runtime.DataRoot, runtime.LocalDataRoot);
    }
}
