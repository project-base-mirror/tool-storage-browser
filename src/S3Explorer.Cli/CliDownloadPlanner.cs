using S3Explorer.Core;

namespace S3Explorer.Cli;

internal static class CliDownloadPlanner
{
    public static IReadOnlyList<TransferTaskRecord> Plan(
        string rootDirectory, string prefix, IEnumerable<S3ObjectEntry> entries)
    {
        var batchId = Guid.NewGuid();
        var tasks = entries.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
        {
            if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("下载对象超出所选前缀。");
            return new TransferTaskRecord
            {
                BatchId = batchId,
                Direction = TransferDirection.Download,
                ObjectKey = item.Key,
                LocalPath = LocalObjectPath.MapRelativeKey(rootDirectory, item.Key[prefix.Length..]),
                ConflictPolicy = ObjectConflictPolicy.AutoRename
            };
        }).ToArray();
        // Reserve all targets before starting parallel writes, independent of server page order.
        // Existing files/directories also reserve their names. Atomic no-overwrite commit
        // remains necessary for files created by another process after this snapshot.
        var existing = tasks.Where(task => File.Exists(task.LocalPath) || Directory.Exists(task.LocalPath))
            .Select(task => new TransferTaskRecord
            {
                Direction = TransferDirection.Download,
                LocalPath = task.LocalPath
            }).ToArray();
        TransferDestinationReservation.ReserveBatch(tasks, existing, batchId);
        foreach (var task in tasks)
        {
            LocalObjectPath.EnsureNoReparsePoints(rootDirectory, task.LocalPath);
            for (var parent = Path.GetDirectoryName(task.LocalPath); parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent))
                    throw new IOException($"下载目录与已有文件冲突，未开始下载：{parent}");
        }
        return tasks;
    }
}
