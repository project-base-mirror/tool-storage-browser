using S3Explorer.Cli;
using S3Explorer.Contracts;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class CliArgumentsTests
{
    [Fact]
    public void UnknownOptionIsRejected()
    {
        var exception = Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["upload", "--paralell", "4"]));

        Assert.Contains("未知选项", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueOptionWithoutValueIsRejected()
    {
        var exception = Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["upload", "--transfers"]));

        Assert.Contains("缺少值", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingletonOptionCannotBeRepeated()
    {
        var exception = Assert.Throws<CliUsageException>(() =>
            CliArguments.Parse(["upload", "--transfers", "2", "--transfers", "4"]));

        Assert.Contains("不能重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExclusionOptionCanBeRepeated()
    {
        var parsed = CliArguments.Parse([
            "sync", "add", "--exclude", "*.tmp", "--exclude", "cache/**"]);

        Assert.Equal(["*.tmp", "cache/**"], parsed.Values("exclude"));
    }

    [Fact]
    public void KnownOptionOnWrongCommandIsRejected()
    {
        var parsed = CliArguments.Parse(["version", "--bucket", "assets"]);

        var exception = Assert.Throws<CliUsageException>(() =>
            parsed.EnsureOnly(["output", "json"]));

        Assert.Contains("当前命令不支持", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, PublishAccessMode.Preserve)]
    [InlineData("preserve", PublishAccessMode.Preserve)]
    [InlineData("anonymous-read", PublishAccessMode.AnonymousRead)]
    [InlineData("public-read", PublishAccessMode.AnonymousRead)]
    [InlineData("private", PublishAccessMode.Private)]
    public void PublishAccessModeIsParsed(string? value, PublishAccessMode expected)
    {
        Assert.Equal(expected, AutomationCommands.ParseAccessMode(value));
    }

    [Fact]
    public void UnsupportedPublishAccessModeIsRejected()
    {
        var exception = Assert.Throws<CliUsageException>(() =>
            AutomationCommands.ParseAccessMode("bucket-public"));

        Assert.Contains("--access", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheTestAcceptsOnlyCdnProbeOptions()
    {
        var parsed = CliArguments.Parse(["cdn", "cache-test", "--profile", "edge", "--path", "file.bin"]);

        Program.ValidateCommandOptions("cdn", "cache-test", parsed);
    }
}
