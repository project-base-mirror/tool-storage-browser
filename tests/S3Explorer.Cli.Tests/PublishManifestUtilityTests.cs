using System.Text;
using System.Text.Json;
using S3Explorer.Cli;
using S3Explorer.Contracts;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class PublishManifestUtilityTests
{
    [Fact]
    public void CreatePlanClassifiesNewModifiedAndUnchangedFiles()
    {
        var local = new[]
        {
            ManifestFile("config.bytes", 10, 'a'),
            ManifestFile("bundles/ui.bundle", 20, 'b'),
            ManifestFile("new.dat", 30, 'c')
        };
        var remote = new PublishManifest
        {
            Files =
            [
                ManifestFile("config.bytes", 10, 'a'),
                ManifestFile("bundles/ui.bundle", 19, 'b'),
                ManifestFile("removed.dat", 40, 'd')
            ]
        };

        var plan = PublishManifestUtility.CreatePlan(local, remote);

        Assert.Equal(1, plan.NewFiles);
        Assert.Equal(1, plan.ModifiedFiles);
        Assert.Equal(1, plan.UnchangedFiles);
        Assert.Equal(50, plan.UploadBytes);
        Assert.Equal(PublishChangeKind.Unchanged, Item(plan, "config.bytes").Change);
        Assert.Equal(PublishChangeKind.Modified, Item(plan, "bundles/ui.bundle").Change);
        Assert.Equal(PublishChangeKind.New, Item(plan, "new.dat").Change);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("content/../../secret.txt")]
    [InlineData("/absolute.txt")]
    public void ValidateManifestRejectsUnsafePaths(string path)
    {
        var manifest = new PublishManifest
        {
            Files = [ManifestFile(path, 1, 'a')]
        };

        Assert.Throws<InvalidDataException>(() =>
            PublishManifestUtility.ValidateManifest(manifest));
    }

    [Fact]
    public void ValidateManifestRejectsDuplicatePaths()
    {
        var manifest = new PublishManifest
        {
            Files =
            [
                ManifestFile("config.bytes", 1, 'a'),
                ManifestFile("config.bytes", 1, 'b')
            ]
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            PublishManifestUtility.ValidateManifest(manifest));

        Assert.Contains("重复路径", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsyncExcludesManifestAndComputesSha256()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "S3Explorer.Cli.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var filePath = Path.Combine(directory, "config.bytes");
            var manifestPath = Path.Combine(directory, "publish-manifest.json");
            await File.WriteAllTextAsync(
                filePath, "hello", Encoding.UTF8, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                manifestPath, "{}", Encoding.UTF8, TestContext.Current.CancellationToken);

            var files = await PublishManifestUtility.ScanAsync(
                directory,
                manifestPath,
                cancellationToken: TestContext.Current.CancellationToken);

            var file = Assert.Single(files);
            Assert.Equal("config.bytes", file.Entry.Path);
            Assert.Equal(new FileInfo(filePath).Length, file.Entry.Size);
            Assert.Equal(64, file.Entry.Sha256.Length);
            Assert.Equal(
                await PublishManifestUtility.ComputeSha256Async(
                    filePath, TestContext.Current.CancellationToken),
                file.Entry.Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OlderManifestWithoutAccessModeDefaultsToPreserve()
    {
        var manifest = JsonSerializer.Deserialize<PublishManifest>(
            "{\"schemaVersion\":1,\"files\":[]}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(manifest);
        Assert.Equal(PublishAccessMode.Preserve, manifest.AccessMode);
        PublishManifestUtility.ValidateManifest(manifest);
    }

    [Fact]
    public void CreatePlanTreatsHeaderOnlyChangeAsModified()
    {
        var local = ManifestFile("bundle.data", 10, 'a');
        local.Headers = new PublishObjectHeaders { CacheControl = "public,max-age=31536000,immutable" };
        var remote = ManifestFile("bundle.data", 10, 'a');
        remote.Headers = new PublishObjectHeaders { CacheControl = "no-cache" };

        var plan = PublishManifestUtility.CreatePlan(
            [local],
            new PublishManifest { SchemaVersion = 2, Files = [remote] });

        Assert.Equal(PublishChangeKind.Modified, Assert.Single(plan.Items).Change);
        Assert.Equal(10, plan.UploadBytes);
    }

    [Fact]
    public void HeaderRulesApplyDefaultsAndOrderedGlobOverlays()
    {
        var rules = new PublishHeaderRuleSet
        {
            Defaults = new PublishObjectHeaders
            {
                CacheControl = "public,max-age=300",
                Metadata = new Dictionary<string, string> { ["channel"] = "stable" }
            },
            Rules =
            [
                new PublishHeaderRule
                {
                    Pattern = "*.json",
                    Headers = new PublishObjectHeaders
                    {
                        ContentType = "application/json",
                        CacheControl = "no-cache"
                    }
                },
                new PublishHeaderRule
                {
                    Pattern = "config/**",
                    Headers = new PublishObjectHeaders
                    {
                        Tags = new Dictionary<string, string> { ["group"] = "configuration" }
                    }
                }
            ]
        };

        PublishHeaderRuleUtility.Validate(rules);
        var resolved = Assert.IsType<PublishObjectHeaders>(
            PublishHeaderRuleUtility.Resolve(rules, "config/runtime.json"));

        Assert.Equal("application/json", resolved.ContentType);
        Assert.Equal("no-cache", resolved.CacheControl);
        Assert.Equal("stable", resolved.Metadata!["channel"]);
        Assert.Equal("configuration", resolved.Tags!["group"]);
    }

    [Fact]
    public void UnsupportedFutureManifestSchemaIsRejected()
    {
        var manifest = new PublishManifest
        {
            SchemaVersion = PublishManifest.CurrentSchemaVersion + 1,
            Files = []
        };

        Assert.Throws<InvalidDataException>(() => PublishManifestUtility.ValidateManifest(manifest));
    }

    [Fact]
    public void CreateMirrorDeletePlanKeepsLocalFilesManifestAndDirectoryMarkers()
    {
        var plan = PublishManifestUtility.CreateMirrorDeletePlan(
            [ManifestFile("keep.bin", 10, 'a')],
            [
                RemoteObject("releases/game/remove.bin", 30),
                RemoteObject("releases/game/keep.bin", 10),
                RemoteObject("releases/game/publish-manifest.json", 100),
                RemoteObject("releases/game/folder/", 0, isDirectory: true),
                RemoteObject("releases/game/also-remove.bin", -1)
            ],
            "releases/game",
            "publish-manifest.json");

        Assert.Collection(
            plan,
            item =>
            {
                Assert.Equal("also-remove.bin", item.Path);
                Assert.Equal("releases/game/also-remove.bin", item.Key);
                Assert.Equal(0, item.Size);
            },
            item =>
            {
                Assert.Equal("remove.bin", item.Path);
                Assert.Equal("releases/game/remove.bin", item.Key);
                Assert.Equal(30, item.Size);
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("releases/../game")]
    [InlineData("releases//game")]
    public void CreateMirrorDeletePlanRejectsUnsafePrefix(string prefix)
    {
        Assert.Throws<InvalidDataException>(() =>
            PublishManifestUtility.CreateMirrorDeletePlan(
                [],
                [],
                prefix,
                "publish-manifest.json"));
    }

    [Fact]
    public void CreateMirrorDeletePlanRejectsObjectsOutsidePrefix()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            PublishManifestUtility.CreateMirrorDeletePlan(
                [],
                [RemoteObject("releases/other/file.bin", 1)],
                "releases/game",
                "publish-manifest.json"));

        Assert.Contains("超出镜像发布前缀", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMirrorDeletePlanRejectsNonCanonicalRemotePaths()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            PublishManifestUtility.CreateMirrorDeletePlan(
                [],
                [RemoteObject("releases/game/folder//file.bin", 1)],
                "releases/game",
                "publish-manifest.json"));

        Assert.Contains("路径", exception.Message, StringComparison.Ordinal);
    }

    private static PublishManifestFile ManifestFile(string path, long size, char hash) =>
        new()
        {
            Path = path,
            Size = size,
            Sha256 = new string(hash, 64)
        };

    private static PublishPlanItem Item(PublishPlan plan, string path) =>
        Assert.Single(plan.Items, item => item.Path == path);

    private static S3ObjectEntry RemoteObject(string key, long size, bool isDirectory = false) =>
        new(
            key,
            S3Path.DisplayName(key, isDirectory),
            size,
            isDirectory,
            DateTimeOffset.UtcNow,
            "STANDARD");
}
