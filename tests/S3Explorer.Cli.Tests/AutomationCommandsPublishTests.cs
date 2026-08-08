using System.Reflection;
using S3Explorer.Cli;
using S3Explorer.Contracts;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class AutomationCommandsPublishTests
{
    [Fact]
    public async Task MirrorPublishesManifestBeforeDeletingRemoteOnlyObjects()
    {
        await using var context = await PublishTestContext.CreateAsync();
        context.Storage.Objects["releases/game/stale.bin"] = [1, 2, 3];

        var command = await context.RunAsync("mirror");

        Assert.Equal(0, command.ExitCode);
        var result = Assert.IsType<PublishResult>(command.Data);
        Assert.True(result.Success);
        Assert.Equal(PublishDeleteMode.Mirror, result.DeleteMode);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(3, result.DeletedBytes);
        Assert.DoesNotContain("releases/game/stale.bin", context.Storage.Objects.Keys);
        Assert.Contains("releases/game/keep.bin", context.Storage.Objects.Keys);
        Assert.Contains("releases/game/publish-manifest.json", context.Storage.Objects.Keys);

        var deleteIndex = context.Storage.Operations.IndexOf("delete:releases/game/stale.bin");
        var manifestIndex = context.Storage.Operations.IndexOf("upload:releases/game/publish-manifest.json");
        Assert.True(manifestIndex >= 0 && deleteIndex > manifestIndex);
    }

    [Fact]
    public async Task MirrorDoesNotDeleteOrPublishManifestWhenUploadFails()
    {
        await using var context = await PublishTestContext.CreateAsync();
        context.Storage.Objects["releases/game/stale.bin"] = [1, 2, 3];
        context.Storage.FailUploadKey = "releases/game/keep.bin";

        var command = await context.RunAsync("mirror");

        Assert.Equal(4, command.ExitCode);
        var result = Assert.IsType<PublishResult>(command.Data);
        Assert.False(result.Success);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Contains("releases/game/stale.bin", context.Storage.Objects.Keys);
        Assert.DoesNotContain("releases/game/publish-manifest.json", context.Storage.Objects.Keys);
        Assert.DoesNotContain(context.Storage.Operations, value => value.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorDoesNotDeleteWhenManifestUploadFails()
    {
        await using var context = await PublishTestContext.CreateAsync();
        context.Storage.Objects["releases/game/stale.bin"] = [1, 2, 3];
        context.Storage.FailUploadKey = "releases/game/publish-manifest.json";

        var command = await context.RunAsync("mirror");

        Assert.Equal(4, command.ExitCode);
        var result = Assert.IsType<PublishResult>(command.Data);
        Assert.False(result.Success);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Contains("releases/game/stale.bin", context.Storage.Objects.Keys);
        Assert.DoesNotContain("releases/game/publish-manifest.json", context.Storage.Objects.Keys);
        Assert.DoesNotContain(context.Storage.Operations, value => value.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorKeepsPublishedManifestWhenCleanupDeleteFails()
    {
        await using var context = await PublishTestContext.CreateAsync();
        context.Storage.Objects["releases/game/stale.bin"] = [1, 2, 3];
        context.Storage.FailDelete = true;

        var command = await context.RunAsync("mirror");

        Assert.Equal(4, command.ExitCode);
        var result = Assert.IsType<PublishResult>(command.Data);
        Assert.False(result.Success);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Contains("releases/game/stale.bin", context.Storage.Objects.Keys);
        Assert.Contains("releases/game/publish-manifest.json", context.Storage.Objects.Keys);
    }

    [Fact]
    public async Task NoneModeDoesNotListOrDeleteRemoteObjects()
    {
        await using var context = await PublishTestContext.CreateAsync();
        context.Storage.Objects["releases/game/stale.bin"] = [1, 2, 3];

        var command = await context.RunAsync("none");

        Assert.Equal(0, command.ExitCode);
        var result = Assert.IsType<PublishResult>(command.Data);
        Assert.Equal(PublishDeleteMode.None, result.DeleteMode);
        Assert.Contains("releases/game/stale.bin", context.Storage.Objects.Keys);
        Assert.DoesNotContain(context.Storage.Operations, value => value.StartsWith("list:", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Storage.Operations, value => value.StartsWith("delete:", StringComparison.Ordinal));
    }

    private sealed class PublishTestContext : IAsyncDisposable
    {
        private PublishTestContext(string directory, FakeStorageProxy storage)
        {
            Directory = directory;
            Storage = storage;
        }

        public string Directory { get; }
        public FakeStorageProxy Storage { get; }

        public static async Task<PublishTestContext> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "S3Explorer.Cli.Tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "keep.bin"),
                [10, 20, 30, 40],
                TestContext.Current.CancellationToken);

            var service = DispatchProxy.Create<IS3StorageService, FakeStorageProxy>();
            return new PublishTestContext(directory, (FakeStorageProxy)(object)service);
        }

        public Task<AutomationCommandResult> RunAsync(string deleteMode)
        {
            var args = CliArguments.Parse([
                "publish",
                "--profile", "test",
                "--source", Directory,
                "--bucket", "assets",
                "--prefix", "releases/game",
                "--delete-mode", deleteMode,
                "--non-interactive"
            ]);
            return AutomationCommands.RunAsync(
                "publish",
                string.Empty,
                args,
                new TestProfileStore(),
                Storage.Service,
                new EmptyCdnConfigurationStore(),
                new EmptyCdnCredentialStore(),
                new UnusedCdnDeliveryService(),
                jsonOutput: true,
                TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
            return ValueTask.CompletedTask;
        }
    }

    public class FakeStorageProxy : DispatchProxy
    {
        public IS3StorageService Service => (IS3StorageService)(object)this;
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Operations { get; } = [];
        public string? FailUploadKey { get; set; }
        public bool FailDelete { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            return targetMethod.Name switch
            {
                nameof(IS3StorageService.ObjectExistsAsync) =>
                    Task.FromResult(Objects.ContainsKey((string)args[2]!)),
                nameof(IS3StorageService.ListObjectsAsync) => ListObjectsAsync(args),
                nameof(IS3StorageService.UploadFileAsync) => UploadFileAsync(args),
                nameof(IS3StorageService.DownloadFileAsync) => DownloadFileAsync(args),
                nameof(IS3StorageService.PutObjectAclAsync) => Task.CompletedTask,
                nameof(IS3StorageService.DeleteObjectsAsync) => DeleteObjectsAsync(args),
                _ => throw new NotSupportedException($"Test storage does not implement {targetMethod.Name}.")
            };
        }

        private Task<PagedObjectResult> ListObjectsAsync(object?[] args)
        {
            var prefix = (string)args[2]!;
            Operations.Add("list:" + prefix);
            var items = Objects
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => new S3ObjectEntry(
                    pair.Key,
                    S3Path.DisplayName(pair.Key, false),
                    pair.Value.LongLength,
                    false,
                    DateTimeOffset.UtcNow,
                    "STANDARD"))
                .ToArray();
            return Task.FromResult(new PagedObjectResult(items, null, false));
        }

        private async Task UploadFileAsync(object?[] args)
        {
            var key = (string)args[2]!;
            Operations.Add("upload:" + key);
            if (string.Equals(key, FailUploadKey, StringComparison.Ordinal))
                throw new InvalidOperationException("simulated upload failure");
            Objects[key] = await File.ReadAllBytesAsync(
                (string)args[3]!,
                (CancellationToken)args[^1]!);
        }

        private async Task DownloadFileAsync(object?[] args)
        {
            var key = (string)args[2]!;
            Operations.Add("download:" + key);
            await File.WriteAllBytesAsync(
                (string)args[3]!,
                Objects[key],
                (CancellationToken)args[^1]!);
        }

        private Task DeleteObjectsAsync(object?[] args)
        {
            var keys = (IReadOnlyCollection<string>)args[2]!;
            if (FailDelete)
                return Task.FromException(new InvalidOperationException("simulated delete failure"));
            foreach (var key in keys)
            {
                Operations.Add("delete:" + key);
                Objects.Remove(key);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TestProfileStore : IProfileStore
    {
        private static readonly ConnectionProfile Profile = new()
        {
            Name = "test",
            ServiceType = S3ServiceType.Custom,
            Endpoint = "https://s3.example.com",
            Region = "us-east-1",
            DefaultBucket = "assets"
        };

        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>([Profile]);

        public Task SaveAsync(
            IReadOnlyCollection<ConnectionProfile> profiles,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyCdnConfigurationStore : ICdnConfigurationStore
    {
        public Task<CdnConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CdnConfiguration.Empty);

        public Task SaveAsync(CdnConfiguration configuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyCdnCredentialStore : ICdnCredentialStore
    {
        public Task<IReadOnlyList<CdnCredential>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CdnCredential>>([]);

        public Task SaveAsync(
            IReadOnlyCollection<CdnCredential> credentials,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedCdnDeliveryService : ICdnDeliveryService
    {
        public Task<CdnProbeResult> ProbeAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            long sampleBytes,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CdnOperationResult> WarmupAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CdnOperationResult> PurgeAsync(
            CdnProfile profile,
            CdnCredential? credential,
            Uri url,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
