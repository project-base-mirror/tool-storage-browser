using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class FolderSyncTests
{
    [Fact]
    public void Upload_plan_includes_new_changed_and_optional_remote_deletion()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Job() with { PropagateDeletions = true };
        var local = new[]
        {
            File("new.txt", 1, now),
            File("changed.txt", 20, now),
            File("same.txt", 30, now)
        };
        var remote = new[]
        {
            File("changed.txt", 10, now.AddMinutes(-2)),
            File("same.txt", 30, now.AddMinutes(1)),
            File("old.txt", 4, now)
        };

        var plan = FolderSyncPlanner.Analyze(job, local, remote, now);

        Assert.Equal(FolderSyncAction.Upload, Item(plan, "new.txt").Action);
        Assert.Equal(FolderSyncAction.Upload, Item(plan, "changed.txt").Action);
        Assert.Equal(FolderSyncAction.None, Item(plan, "same.txt").Action);
        Assert.Equal(FolderSyncAction.DeleteRemote, Item(plan, "old.txt").Action);
        Assert.Equal(3, plan.ActionCount);
    }

    [Fact]
    public void Download_plan_reverses_source_and_destination()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Job() with { Direction = FolderSyncDirection.Download, PropagateDeletions = true };
        var local = new[] { File("local-only.txt", 1, now) };
        var remote = new[] { File("remote-only.txt", 2, now) };

        var plan = FolderSyncPlanner.Analyze(job, local, remote, now);

        Assert.Equal(FolderSyncAction.Download, Item(plan, "remote-only.txt").Action);
        Assert.Equal(FolderSyncAction.DeleteLocal, Item(plan, "local-only.txt").Action);
    }

    [Fact]
    public void Exclusions_support_single_and_cross_folder_wildcards()
    {
        Assert.True(FolderSyncGlobMatcher.IsMatch("bin/a.dll", ["bin/**"]));
        Assert.True(FolderSyncGlobMatcher.IsMatch("images/icon.png", ["**/*.png"]));
        Assert.True(FolderSyncGlobMatcher.IsMatch("root.tmp", ["*.tmp;*.bak"]));
        Assert.False(FolderSyncGlobMatcher.IsMatch("src/a.cs", ["bin/**;*.tmp"]));
    }

    [Fact]
    public void Hash_comparison_detects_same_size_content_change()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Job() with { CompareHashesWhenAvailable = true };

        var plan = FolderSyncPlanner.Analyze(
            job,
            [new FolderSyncFile("a.txt", 10, now, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
            [new FolderSyncFile("a.txt", 10, now.AddMinutes(1), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]);

        Assert.Equal(FolderSyncChange.Changed, plan.Items.Single().Change);
        Assert.Equal(FolderSyncAction.Upload, plan.Items.Single().Action);
    }

    [Fact]
    public async Task Store_round_trips_jobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonFolderSyncJobStore(Path.Combine(directory, "jobs.json"));
            var expected = Job() with { ExclusionPatterns = ["bin/**", "*.tmp"] };
            await store.SaveAsync([expected]);

            var actual = Assert.Single(await store.LoadAsync());
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.LocalDirectory, actual.LocalDirectory);
            Assert.Equal(expected.ProfileId, actual.ProfileId);
            Assert.Equal(expected.ExclusionPatterns, actual.ExclusionPatterns);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Store_rejects_unknown_schema_version()
    {
        var directory = Path.Combine(Path.GetTempPath(), "S3Explorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "jobs.json");
            await System.IO.File.WriteAllTextAsync(path, "{\"version\":2,\"jobs\":[]}");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new JsonFolderSyncJobStore(path).LoadAsync());

            Assert.Contains("不支持的同步任务存储版本", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static FolderSyncPlanItem Item(FolderSyncPlan plan, string path) =>
        plan.Items.Single(item => item.RelativePath == path);

    private static FolderSyncFile File(string path, long size, DateTimeOffset modified) => new(path, size, modified);

    private static FolderSyncJob Job() => new()
    {
        Name = "test",
        LocalDirectory = Path.GetFullPath(Path.GetTempPath()),
        ProfileId = Guid.NewGuid(),
        ProfileName = "profile",
        Bucket = "bucket"
    };
}
