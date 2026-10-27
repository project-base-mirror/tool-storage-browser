using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Core.Tests;

public sealed class DownloadTemporaryOwnershipTests
{
    [Fact]
    public void ExistingSuffixNamedObjectIsNeverReusedAsTemporaryData()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "file");
            var unrelated = ResumableDownloadFile.TemporaryPath(target);
            File.WriteAllText(unrelated, "previous completed download or user file");
            var temporary = ResumableDownloadFile.ReserveTemporaryPath(target, null);
            Assert.NotEqual(unrelated, temporary);
            File.WriteAllText(temporary, "new");
            ResumableDownloadFile.Commit(temporary, target, 3, overwrite: false);
            Assert.Equal("previous completed download or user file", File.ReadAllText(unrelated));
            Assert.Equal("new", File.ReadAllText(target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CheckpointRetainsOwnedTemporaryAndNonOverwriteCommitPreservesDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "file");
            var temporary = ResumableDownloadFile.ReserveTemporaryPath(target, null);
            File.WriteAllText(temporary, "new");
            File.WriteAllText(target, "existing");
            Assert.Equal(temporary, ResumableDownloadFile.ReserveTemporaryPath(target, new DownloadCheckpoint(temporary, 3, 3)));
            Assert.Throws<IOException>(() => ResumableDownloadFile.Commit(temporary, target, 3, overwrite: false));
            Assert.Equal("existing", File.ReadAllText(target));
            Assert.Equal("new", File.ReadAllText(temporary));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
