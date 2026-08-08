using System.Reflection;
using S3Explorer.Cli;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class ConnectionCommandsTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 4)]
    public async Task ConnectionTestReturnsStructuredSuccessAndFailure(bool success, int expectedExitCode)
    {
        var profile = new ConnectionProfile
        {
            Name = "test",
            ServiceType = S3ServiceType.Custom,
            Endpoint = "https://s3.example.com",
            Region = "us-east-1"
        };
        var storage = DispatchProxy.Create<IS3StorageService, ConnectionStorageProxy>();
        ((ConnectionStorageProxy)(object)storage).Result = new ConnectionTestResult(
            success,
            TimeSpan.FromMilliseconds(25),
            2,
            success ? "连接成功" : "连接失败",
            success ? 200 : 503,
            CredentialSource: "测试凭据");
        var args = CliArguments.Parse(["connection", "test", "--profile", "test"]);

        var result = await ConnectionCommands.RunTestAsync(
            "test",
            args,
            new SingleProfileStore(profile),
            storage,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.IsType<ConnectionTestResult>(result.Data);
        Assert.Contains(success ? "连接成功" : "连接失败", result.Text, StringComparison.Ordinal);
        if (success)
            Assert.Contains("凭据来源: 测试凭据", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionTestRejectsUnknownVerbBeforeStorageAccess()
    {
        var storage = DispatchProxy.Create<IS3StorageService, ConnectionStorageProxy>();
        var exception = await Assert.ThrowsAsync<CliUsageException>(() =>
            ConnectionCommands.RunTestAsync(
                "show",
                CliArguments.Parse(["connection", "show"]),
                new SingleProfileStore(new ConnectionProfile()),
                storage,
                TestContext.Current.CancellationToken));

        Assert.Contains("connection test", exception.Message, StringComparison.Ordinal);
    }

    public class ConnectionStorageProxy : DispatchProxy
    {
        public ConnectionTestResult Result { get; set; } =
            new(false, TimeSpan.Zero, 0, "not configured");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IS3StorageService.TestConnectionAsync)
                ? Task.FromResult(Result)
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class SingleProfileStore(ConnectionProfile profile) : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([profile]);

        public Task SaveAsync(
            IReadOnlyCollection<ConnectionProfile> profiles,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
