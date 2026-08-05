using S3Explorer.Updater;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class UpdateRunnerTests
{
    [Fact]
    public void ParsesStrictAbsoluteUpdateArguments()
    {
        var root = TemporaryDirectory();
        try
        {
            var msi = Path.Combine(root, "update.msi");
            var app = Path.Combine(root, "S3Explorer.exe");
            File.WriteAllBytes(msi, [1, 2, 3]);
            File.WriteAllBytes(app, [4, 5, 6]);

            var result = UpdateArguments.Parse([
                "--parent-pid", "42",
                "--parent-start-time-utc", "2026-08-05T01:02:03Z",
                "--msi", msi,
                "--sha256", new string('a', 64),
                "--application", app,
                "--state", Path.Combine(root, "state.json"),
                "--log", Path.Combine(root, "install.log"),
                "--target-version", "0.7.2"
            ]);

            Assert.Equal(42, result.ParentPid);
            Assert.Equal("0.7.2", result.TargetVersion);
            Assert.Equal(msi, result.MsiPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("--parent-pid")]
    [InlineData("--sha256")]
    [InlineData("--target-version")]
    public void RejectsMissingRequiredArguments(string omitted)
    {
        var root = TemporaryDirectory();
        try
        {
            var msi = Path.Combine(root, "update.msi");
            var app = Path.Combine(root, "S3Explorer.exe");
            File.WriteAllText(msi, "msi");
            File.WriteAllText(app, "app");
            var pairs = new Dictionary<string, string>
            {
                ["--parent-pid"] = "42",
                ["--parent-start-time-utc"] = "2026-08-05T01:02:03Z",
                ["--msi"] = msi,
                ["--sha256"] = new string('a', 64),
                ["--application"] = app,
                ["--state"] = Path.Combine(root, "state.json"),
                ["--log"] = Path.Combine(root, "install.log"),
                ["--target-version"] = "0.7.2"
            };
            pairs.Remove(omitted);

            Assert.Throws<ArgumentException>(() =>
                UpdateArguments.Parse(pairs.SelectMany(pair => new[] { pair.Key, pair.Value }).ToArray()));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResultStateIsWrittenAtomically()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "state.json");
            UpdateRunner.WriteResult(path, new UpdateResult(
                1,
                "completed",
                "0.7.2",
                0,
                "ok",
                Path.Combine(root, "install.log"),
                DateTimeOffset.Parse("2026-08-05T01:02:03Z")));

            var payload = File.ReadAllText(path);
            Assert.Contains("\"status\": \"completed\"", payload, StringComparison.Ordinal);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RunnerRejectsChangedPackageBeforeLaunchingWindowsInstaller()
    {
        var root = TemporaryDirectory();
        try
        {
            var msi = Path.Combine(root, "update.msi");
            var app = Path.Combine(root, "S3Explorer.exe");
            var state = Path.Combine(root, "state.json");
            File.WriteAllText(msi, "changed-package");
            File.WriteAllText(app, "not-an-executable");
            var options = new UpdateArguments(
                int.MaxValue,
                DateTime.UtcNow,
                msi,
                new string('0', 64),
                app,
                state,
                Path.Combine(root, "install.log"),
                "0.7.2");

            var exitCode = UpdateRunner.Run(options);

            Assert.Equal(1, exitCode);
            Assert.Contains("SHA-256", File.ReadAllText(state), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "S3Explorer.Updater.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
