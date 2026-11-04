namespace S3Explorer.Core;

public static class SafeLocalFileTraversal
{
    public static IEnumerable<string> EnumerateFiles(string rootDirectory, Action<string, Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(onError);
        var root = Path.GetFullPath(rootDirectory);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!visited.Add(directory)) continue;
            string[] entries;
            try
            {
                LocalObjectPath.EnsureNoReparsePoints(root, directory);
                entries = Directory.GetFileSystemEntries(directory);
                Array.Sort(entries, StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                onError(directory, exception);
                continue;
            }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    LocalObjectPath.EnsureContainedPath(root, entry);
                    attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        onError(entry, new IOException("已跳过符号链接或目录联接。"));
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    onError(entry, exception);
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }
}
