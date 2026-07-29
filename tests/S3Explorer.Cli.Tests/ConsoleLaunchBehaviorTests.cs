using S3Explorer.Cli;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class ConsoleLaunchBehaviorTests
{
    [Fact]
    public void DirectExplorerLaunchPausesAfterShowingHelp()
    {
        Assert.True(ConsoleLaunchBehavior.ShouldPause(
            argumentCount: 0,
            isWindows: true,
            isUserInteractive: true,
            isInputRedirected: false,
            isOutputRedirected: false,
            attachedConsoleProcessCount: 1));
    }

    [Theory]
    [InlineData(1, true, true, false, false, 1)]
    [InlineData(0, false, true, false, false, 1)]
    [InlineData(0, true, false, false, false, 1)]
    [InlineData(0, true, true, true, false, 1)]
    [InlineData(0, true, true, false, true, 1)]
    [InlineData(0, true, true, false, false, 2)]
    public void ScriptAndTerminalLaunchesDoNotPause(
        int argumentCount,
        bool isWindows,
        bool isUserInteractive,
        bool isInputRedirected,
        bool isOutputRedirected,
        int attachedConsoleProcessCount)
    {
        Assert.False(ConsoleLaunchBehavior.ShouldPause(
            argumentCount,
            isWindows,
            isUserInteractive,
            isInputRedirected,
            isOutputRedirected,
            attachedConsoleProcessCount));
    }
}
