using System.Text;
using System.Text.Json;
using S3Explorer.Cli;
using S3Explorer.Contracts;
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
            await File.WriteAllTextAsync(filePath, "hello", Encoding.UTF8);
            await File.WriteAllTextAsync(manifestPath, "{}", Encoding.UTF8);

            var files = await PublishManifestUtility.ScanAsync(
                directory,
                manifestPath);

            var file = Assert.Single(files);
            Assert.Equal("config.bytes", file.Entry.Path);
            Assert.Equal(new FileInfo(filePath).Length, file.Entry.Size);
            Assert.Equal(64, file.Entry.Sha256.Length);
            Assert.Equal(
                await PublishManifestUtility.ComputeSha256Async(filePath),
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

    private static PublishManifestFile ManifestFile(string path, long size, char hash) =>
        new()
        {
            Path = path,
            Size = size,
            Sha256 = new string(hash, 64)
        };

    private static PublishPlanItem Item(PublishPlan plan, string path) =>
        Assert.Single(plan.Items, item => item.Path == path);
}
