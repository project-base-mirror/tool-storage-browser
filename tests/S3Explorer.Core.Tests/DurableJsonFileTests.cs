using System.Text.Json;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class DurableJsonFileTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    public async Task CorruptPrimaryIsPreservedAndLastGoodBackupIsRestored()
    {
        var root = TemporaryDirectory();
        var path = Path.Combine(root, "state.json");
        try
        {
            var file = new DurableJsonFile(path);
            await file.SaveAsync(new TestDocument { Value = "first" }, Options, Validate);
            await file.SaveAsync(new TestDocument { Value = "second" }, Options, Validate);
            await File.WriteAllTextAsync(path, "{truncated");

            var loaded = await file.LoadAsync(
                static () => new TestDocument(), Options, Validate);

            Assert.Equal("first", loaded.Value);
            Assert.True(file.LastRecovery?.RestoredFromBackup);
            Assert.False(file.LastRecovery?.UsedDefault);
            Assert.Single(Directory.EnumerateFiles(root, "state.json.corrupt-*-primary-*"));
            Assert.Equal("first", (await ReadDirectAsync(path)).Value);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CorruptFileWithoutBackupCanUseExplicitDefaultFallback()
    {
        var root = TemporaryDirectory();
        var path = Path.Combine(root, "state.json");
        await File.WriteAllTextAsync(path, "null");
        try
        {
            var file = new DurableJsonFile(path);

            var loaded = await file.LoadAsync(
                static () => new TestDocument { Value = "default" },
                Options,
                Validate,
                useDefaultWhenUnrecoverable: true);

            Assert.Equal("default", loaded.Value);
            Assert.True(file.LastRecovery?.UsedDefault);
            Assert.False(file.LastRecovery?.RestoredFromBackup);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.EnumerateFiles(root, "state.json.corrupt-*-primary-*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancelledSaveDoesNotReplaceExistingDocumentOrLeaveTemporaryFile()
    {
        var root = TemporaryDirectory();
        var path = Path.Combine(root, "state.json");
        try
        {
            var file = new DurableJsonFile(path);
            await file.SaveAsync(new TestDocument { Value = "stable" }, Options, Validate);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                file.SaveAsync(
                    new TestDocument { Value = "cancelled" },
                    Options,
                    Validate,
                    cancellation.Token));

            Assert.Equal("stable", (await ReadDirectAsync(path)).Value);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConcurrentStoreInstancesSerializeWritesForTheSamePath()
    {
        var root = TemporaryDirectory();
        var path = Path.Combine(root, "state.json");
        try
        {
            var saves = Enumerable.Range(0, 32).Select(value =>
                new DurableJsonFile(path).SaveAsync(
                    new TestDocument { Value = value.ToString() }, Options, Validate));

            await Task.WhenAll(saves);

            var loaded = await ReadDirectAsync(path);
            Assert.InRange(int.Parse(loaded.Value), 0, 31);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SuccessfulLoadRemovesStaleTemporaryFile()
    {
        var root = TemporaryDirectory();
        var path = Path.Combine(root, "state.json");
        try
        {
            var file = new DurableJsonFile(path);
            await file.SaveAsync(new TestDocument { Value = "stable" }, Options, Validate);
            await File.WriteAllTextAsync(path + ".tmp", "{stale");

            var loaded = await file.LoadAsync(static () => new TestDocument(), Options, Validate);

            Assert.Equal("stable", loaded.Value);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FileSystemFailureIsSurfacedInsteadOfReportedAsSaved()
    {
        var root = TemporaryDirectory();
        var parentFile = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(parentFile, "blocker");
        try
        {
            var file = new DurableJsonFile(Path.Combine(parentFile, "state.json"));

            await Assert.ThrowsAnyAsync<IOException>(() =>
                file.SaveAsync(new TestDocument { Value = "value" }, Options, Validate));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<TestDocument> ReadDirectAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TestDocument>(stream, Options)
            ?? throw new InvalidDataException("Expected a test document.");
    }

    private static void Validate(TestDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Value))
            throw new InvalidDataException("Value is required.");
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public sealed record TestDocument
    {
        public string Value { get; init; } = string.Empty;
    }
}
