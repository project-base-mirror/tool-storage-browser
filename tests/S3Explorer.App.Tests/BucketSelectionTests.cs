using S3Explorer.App;
using S3Explorer.Core;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class BucketSelectionTests
{
    [Fact]
    public async Task Cache_does_not_store_secrets_and_roundtrips_successful_discovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"s3explorer-buckets-{Guid.NewGuid():N}.json");
        try
        {
            var profile = Profile() with { AccessKey = "SECRET-AK", SecretKey = "SECRET-SK" };
            await new BucketDiscoveryCache(path).RecordSuccessfulDiscoveryAsync(
                profile, ["zeta", "alpha", "alpha"], cancellationToken: cancellationToken);
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            Assert.Contains("\"schema\": 1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-AK", text);
            Assert.DoesNotContain("SECRET-SK", text);
            var snapshot = await new BucketDiscoveryCache(path).GetAsync(profile, cancellationToken);
            Assert.Equal(["alpha", "zeta"], snapshot!.Buckets);
        }
        finally { DeleteCacheFiles(path); }
    }

    [Fact]
    public async Task Changing_connection_signature_invalidates_cache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new BucketDiscoveryCache();
        var profile = Profile();
        await cache.RecordSuccessfulDiscoveryAsync(profile, ["old"], cancellationToken: cancellationToken);
        Assert.NotNull(await cache.GetAsync(profile, cancellationToken));
        Assert.Null(await cache.GetAsync(profile with { Region = "eu-west-1" }, cancellationToken));
        Assert.Null(await cache.GetAsync(profile with { AccessKey = "rotated-key" }, cancellationToken));
    }

    [Fact]
    public void Merge_is_deterministic_and_deduplicates_known_and_remote_names()
    {
        var names = BucketPicker.MergeBucketNames([" zeta", "alpha"], ["beta", "alpha", ""]);
        Assert.Equal(["alpha", "beta", "zeta"], names);
    }

    [Fact]
    public async Task Remote_failure_leaves_cache_available_and_manual_input_legal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cache = new BucketDiscoveryCache();
        var profile = Profile();
        await cache.RecordSuccessfulDiscoveryAsync(profile, ["cached-bucket"], cancellationToken: cancellationToken);
        var snapshot = await cache.GetAsync(profile, cancellationToken);
        Assert.Contains("cached-bucket", snapshot!.Buckets);
        var merged = BucketPicker.MergeBucketNames(profile.KnownBuckets, snapshot.Buckets, ["manual-bucket"]);
        Assert.Contains("manual-bucket", merged);
        Assert.Contains("cached-bucket", merged);
    }

    [Fact]
    public void Picker_is_editable_and_merges_known_cached_and_remote_buckets()
    {
        RunSta(() =>
        {
            var profile = Profile() with { DefaultBucket = "known-bucket" };
            var cache = new BucketDiscoveryCache();
            cache.RecordSuccessfulDiscoveryAsync(profile, ["cached-bucket"]).GetAwaiter().GetResult();
            using var picker = new BucketPicker(
                cache,
                static (_, _) => Task.FromResult<IReadOnlyList<string>>(["remote-bucket"]));

            picker.BucketText = "manual-bucket";
            picker.RefreshAsync(profile).GetAwaiter().GetResult();

            Assert.Equal(ComboBoxStyle.DropDown, picker.Input.DropDownStyle);
            Assert.Equal("manual-bucket", picker.BucketText);
            Assert.Equal(
                ["cached-bucket", "known-bucket", "remote-bucket"],
                picker.Input.Items.Cast<string>());
            Assert.Contains("已连接并刷新", picker.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Picker_keeps_cache_and_manual_entry_when_remote_discovery_fails()
    {
        RunSta(() =>
        {
            var profile = Profile();
            var cache = new BucketDiscoveryCache();
            cache.RecordSuccessfulDiscoveryAsync(profile, ["cached-bucket"]).GetAwaiter().GetResult();
            using var picker = new BucketPicker(
                cache,
                static (_, _) => throw new InvalidOperationException("secretkey=SHOULD-NOT-LEAK"));

            picker.BucketText = "manual-bucket";
            picker.RefreshAsync(profile).GetAwaiter().GetResult();

            Assert.Equal("manual-bucket", picker.BucketText);
            Assert.Contains("cached-bucket", picker.Input.Items.Cast<string>());
            Assert.Contains("可手动输入", picker.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("SHOULD-NOT-LEAK", picker.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Existing_bucket_inputs_use_the_same_editable_picker()
    {
        RunSta(() =>
        {
            var storageProfile = Profile();
            var cdnProfile = new CdnProfile
            {
                Name = "cdn",
                BaseUrl = "https://cdn.example.test"
            };
            using var probe = new StoragePermissionProbeDialog([storageProfile]);
            using var sync = new FolderSyncJobDialog(null!, [storageProfile]);
            using var binding = new CdnBindingEditorDialog(
                null, [storageProfile], [cdnProfile], storageProfile, null);
            using var transfer = new ObjectTransferDialog(false, "source", string.Empty, 1);

            AssertEditablePicker(probe, "StoragePermissionProbeBucket");
            AssertEditablePicker(sync, "FolderSyncBucket");
            AssertEditablePicker(binding, "CdnBindingBucket");
            AssertEditablePicker(transfer, "ObjectTransferDestinationBucket");
        });
    }

    [Fact]
    public void Stale_remote_result_cannot_replace_the_new_profiles_bucket_choices()
    {
        RunSta(() =>
        {
            var firstProfile = Profile() with { Name = "first" };
            var secondProfile = Profile() with
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Name = "second"
            };
            var firstResult = new TaskCompletionSource<IReadOnlyList<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondResult = new TaskCompletionSource<IReadOnlyList<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstLoaderCalls = 0;
            using var picker = new BucketPicker(
                new BucketDiscoveryCache(),
                (profile, _) =>
                {
                    if (profile.Id != firstProfile.Id) return secondResult.Task;
                    Interlocked.Increment(ref firstLoaderCalls);
                    return firstResult.Task;
                });

            var firstRefresh = picker.RefreshAsync(firstProfile);
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref firstLoaderCalls) == 1,
                TimeSpan.FromSeconds(2)));
            var secondRefresh = picker.RefreshAsync(secondProfile);
            secondResult.SetResult(["second-bucket"]);
            WaitWithMessagePump(secondRefresh);
            firstResult.SetResult(["stale-first-bucket"]);
            WaitWithMessagePump(firstRefresh);

            Assert.Equal(secondProfile.Id, picker.SelectedProfile?.Id);
            Assert.Contains("second-bucket", picker.Input.Items.Cast<string>());
            Assert.DoesNotContain("stale-first-bucket", picker.Input.Items.Cast<string>());
        });
    }

    private static ConnectionProfile Profile() => ConnectionProfile.CreatePreset(S3ServiceType.AmazonS3) with
    {
        Name = "test",
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Endpoint = "https://s3.example.test",
        CredentialId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

    private static void DeleteCacheFiles(string path)
    {
        foreach (var file in new[] { path, path + ".bak", path + ".tmp" })
            if (File.Exists(file)) File.Delete(file);
    }

    private static void AssertEditablePicker(Control root, string name)
    {
        var picker = Assert.IsType<BucketPicker>(Assert.Single(root.Controls.Find(name, true)));
        Assert.Equal(ComboBoxStyle.DropDown, picker.Input.DropDownStyle);
        picker.BucketText = "manual-bucket";
        Assert.Equal("manual-bucket", picker.BucketText);
    }

    private static void WaitWithMessagePump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "异步 Bucket 刷新未在测试超时内完成。");
        task.GetAwaiter().GetResult();
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
