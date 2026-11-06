using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    [Theory]
    [InlineData(false, ObjectConflictPolicy.Overwrite)]
    [InlineData(false, ObjectConflictPolicy.Skip)]
    [InlineData(false, ObjectConflictPolicy.AutoRename)]
    [InlineData(true, ObjectConflictPolicy.Overwrite)]
    [InlineData(true, ObjectConflictPolicy.Skip)]
    [InlineData(true, ObjectConflictPolicy.AutoRename)]
    public async Task ActualConflictOffersRequestedChoice(bool download, ObjectConflictPolicy choice)
    {
        await WithTransferConflictFormAsync(async (form, fake, profile, root) =>
        {
            var path = Path.Combine(root, "index.html");
            await File.WriteAllTextAsync(path, "existing");
            fake.Exists = (_, _, key) => Task.FromResult(key == "lp/index.html");
            var prompts = 0;
            form.TransferConflictPolicySelector = _ => { prompts++; return choice; };
            var result = download
                ? form.ResolveDownloadDestination(path, ObjectConflictPolicy.Ask, new(StringComparer.OrdinalIgnoreCase))
                : await form.ResolveUploadDestinationAsync(profile, "bucket", "lp/index.html", ObjectConflictPolicy.Ask, new(StringComparer.Ordinal));
            Assert.Equal(1, prompts);
            if (choice == ObjectConflictPolicy.Skip) Assert.Null(result);
            else
            {
                Assert.NotNull(result);
                Assert.Equal(choice, result.Policy);
                if (choice == ObjectConflictPolicy.AutoRename) Assert.NotEqual(download ? path : "lp/index.html", result.Path);
                else Assert.Equal(download ? LocalObjectPath.ToExtendedLengthPath(path) : "lp/index.html", result.Path);
            }
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearDestinationDoesNotPromptEvenWithFailedTask(bool download)
    {
        await WithTransferConflictFormAsync(async (form, fake, profile, root) =>
        {
            fake.Exists = (_, _, _) => Task.FromResult(false);
            form.TransferConflictPolicySelector = _ => throw new InvalidOperationException("Unexpected chooser");
            var result = download
                ? form.ResolveDownloadDestination(Path.Combine(root, "index.html"), ObjectConflictPolicy.Ask, new(StringComparer.OrdinalIgnoreCase))
                : await form.ResolveUploadDestinationAsync(profile, "bucket", "lp/index.html", ObjectConflictPolicy.Ask, new(StringComparer.Ordinal));
            Assert.NotNull(result);
            Assert.Equal(ObjectConflictPolicy.Skip, result.Policy); // Protect a destination created after discovery.
        }, failedDownload: download);
    }

    [Fact]
    public async Task CancellingActualConflictStopsResolution()
    {
        await WithTransferConflictFormAsync(async (form, fake, profile, root) =>
        {
            fake.Exists = (_, _, _) => Task.FromResult(true);
            form.TransferConflictPolicySelector = _ => null;
            await Assert.ThrowsAsync<OperationCanceledException>(() => form.ResolveUploadDestinationAsync(
                profile, "bucket", "lp/index.html", ObjectConflictPolicy.Ask, new(StringComparer.Ordinal)));
            var path = Path.Combine(root, "index.html");
            await File.WriteAllTextAsync(path, "existing");
            Assert.Throws<OperationCanceledException>(() => form.ResolveDownloadDestination(path, ObjectConflictPolicy.Ask, new(StringComparer.OrdinalIgnoreCase)));
        });
    }

    [Fact]
    public async Task ExplicitOverwriteDoesNotPrompt()
    {
        await WithTransferConflictFormAsync(async (form, fake, profile, root) =>
        {
            fake.Exists = (_, _, _) => throw new InvalidOperationException("Overwrite does not need preflight");
            form.TransferConflictPolicySelector = _ => throw new InvalidOperationException("Unexpected chooser");
            var result = await form.ResolveUploadDestinationAsync(profile, "bucket", "lp/index.html", ObjectConflictPolicy.Overwrite, new(StringComparer.Ordinal));
            Assert.Equal(ObjectConflictPolicy.Overwrite, result!.Policy);
            var path = Path.Combine(root, "index.html");
            await File.WriteAllTextAsync(path, "existing");
            Assert.Equal(ObjectConflictPolicy.Overwrite, form.ResolveDownloadDestination(path, ObjectConflictPolicy.Overwrite, new(StringComparer.OrdinalIgnoreCase))!.Policy);
        });
    }

    [Theory]
    [InlineData(ObjectConflictPolicy.Skip)]
    [InlineData(ObjectConflictPolicy.AutoRename)]
    public async Task ActiveUploadReservationOffersSafeChoices(ObjectConflictPolicy choice)
    {
        await WithTransferConflictFormAsync(async (form, fake, profile, root) =>
        {
            fake.Exists = (_, _, _) => Task.FromResult(false);
            var prompts = 0;
            form.TransferConflictPolicySelector = _ => { prompts++; return choice; };
            var result = await form.ResolveUploadDestinationAsync(profile, "bucket", "lp/index.html",
                ObjectConflictPolicy.Ask, new(StringComparer.Ordinal) { "lp/index.html" });
            Assert.Equal(1, prompts);
            if (choice == ObjectConflictPolicy.Skip) Assert.Null(result);
            else Assert.NotEqual("lp/index.html", result!.Path);
        });
    }

    [Fact]
    public async Task DownloadMappingCollisionAutomaticallyKeepsBoth()
    {
        await WithTransferConflictFormAsync((form, fake, profile, root) =>
        {
            var path = Path.Combine(root, "index.html");
            form.TransferConflictPolicySelector = _ => throw new InvalidOperationException("Unexpected chooser");
            var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { TransferDestinationReservation.NormalizeLocalPath(path) };
            var result = form.ResolveDownloadDestination(path, ObjectConflictPolicy.Ask, allocated);
            Assert.NotNull(result);
            Assert.Equal(ObjectConflictPolicy.AutoRename, result.Policy);
            Assert.NotEqual(LocalObjectPath.ToExtendedLengthPath(path), result.Path);
            return Task.CompletedTask;
        });
    }

    private static Task WithTransferConflictFormAsync(
        Func<MainForm, FileClipboardStorageProxy, ConnectionProfile, string, Task> test, bool? failedDownload = null) =>
        RunMessagePumpAsync(async _ =>
        {
            var root = Path.Combine(Path.GetTempPath(), "s3-conflicts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var profile = new ConnectionProfile { Name = "test", Endpoint = "https://storage.example.test" };
            var storage = DispatchProxy.Create<IS3StorageService, FileClipboardStorageProxy>();
            var store = new JsonTransferTaskStore(Path.Combine(root, "tasks.json"));
            if (failedDownload is { } download)
                await store.SaveAsync(new TransferStoreSnapshot { Tasks = [new TransferTaskRecord
                {
                    ProfileId = profile.Id, ProfileName = profile.Name, Bucket = "bucket", ObjectKey = "lp/index.html",
                    LocalPath = Path.Combine(root, "index.html"), State = TransferTaskState.Failed,
                    Direction = download ? TransferDirection.Download : TransferDirection.Upload
                }] });
            var queue = new PersistentTransferQueue(store, new NeverRunExecutor());
            try
            {
                await queue.InitializeAsync();
                using var form = CreateForm(queue, new TransferRuntimeConfiguration(), new SimpleFileLogger(Path.Combine(root, "logs")), new SingleProfileStore(profile), storage: storage);
                SetField(form, "_currentProfile", profile);
                SetField(form, "_currentBucket", "bucket");
                await test(form, (FileClipboardStorageProxy)(object)storage, profile, root);
            }
            finally { await queue.DisposeAsync(); Directory.Delete(root, true); }
        });
}
