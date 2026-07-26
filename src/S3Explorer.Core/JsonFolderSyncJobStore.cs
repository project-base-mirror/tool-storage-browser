using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3Explorer.Core;

public interface IFolderSyncJobStore
{
    Task<IReadOnlyList<FolderSyncJob>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyCollection<FolderSyncJob> jobs, CancellationToken cancellationToken = default);
}

public sealed class JsonFolderSyncJobStore : IFolderSyncJobStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;

    public JsonFolderSyncJobStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "sync-jobs.json");
    }

    public async Task<IReadOnlyList<FolderSyncJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return Array.Empty<FolderSyncJob>();
        await using var stream = File.OpenRead(_path);
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, Options, cancellationToken).ConfigureAwait(false)
            ?? new Document();
        if (document.Version != 1)
            throw new InvalidOperationException($"不支持的同步任务存储版本：{document.Version}");
        ValidateJobs(document.Jobs);
        return document.Jobs;
    }

    public async Task SaveAsync(IReadOnlyCollection<FolderSyncJob> jobs, CancellationToken cancellationToken = default)
    {
        ValidateJobs(jobs);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, new Document { Jobs = jobs.ToList() }, Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _path, true);
    }

    private static void ValidateJobs(IEnumerable<FolderSyncJob> jobs)
    {
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            job.Validate();
            if (!ids.Add(job.Id)) throw new InvalidOperationException($"同步任务 ID 重复：{job.Id}");
            if (!names.Add(job.Name.Trim())) throw new InvalidOperationException($"同步任务名称重复：{job.Name}");
        }
    }

    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<FolderSyncJob> Jobs { get; set; } = [];
    }
}
