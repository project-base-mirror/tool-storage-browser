using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class SyncTransferTaskTests
{
    [Fact]
    public void Local_delete_task_requires_safe_local_target()
    {
        var task = BaseTask() with { Direction = TransferDirection.DeleteLocal };
        Assert.Throws<ArgumentException>(() => task.Validate());

        task = task with { LocalPath = Path.Combine(Path.GetTempPath(), "file.txt") };
        task.Validate();
    }

    [Fact]
    public void Remote_delete_task_does_not_require_local_path()
    {
        var task = BaseTask() with { Direction = TransferDirection.DeleteRemote };
        task.Validate();
    }

    private static TransferTaskRecord BaseTask() => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileName = "profile",
        Bucket = "bucket",
        ObjectKey = "path/file.txt",
        TotalBytes = 0
    };
}
