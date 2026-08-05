using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class TrayResidencePolicyTests
{
    [Theory]
    [InlineData(true, false, false, CloseReason.UserClosing, true)]
    [InlineData(false, false, false, CloseReason.UserClosing, false)]
    [InlineData(true, true, false, CloseReason.UserClosing, false)]
    [InlineData(true, false, true, CloseReason.UserClosing, false)]
    [InlineData(true, false, false, CloseReason.WindowsShutDown, false)]
    public void ClosePolicyOnlyHidesAnInteractiveUserClose(
        bool enabled,
        bool automation,
        bool explicitExit,
        CloseReason reason,
        bool expected)
    {
        Assert.Equal(expected, TrayResidencePolicy.ShouldHideOnClose(
            enabled,
            automation,
            explicitExit,
            reason));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void MinimizePolicyDoesNotInterfereWithAutomationOrShutdown(
        bool enabled,
        bool automation,
        bool closing,
        bool expected)
    {
        Assert.Equal(expected, TrayResidencePolicy.ShouldHideOnMinimize(
            enabled,
            automation,
            closing));
    }
}
