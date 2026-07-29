using System.Text.Json;
using System.Text.Json.Serialization;

namespace S3Explorer.Core;

public interface IFolderSyncJobStore
{
    Task<IReadOnlyList<FolderSyncJob>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyCollection<FolderSyncJob> jobs, CancellationToken cancellationToken = default);
}

public sealed class JsonFolderSyncJobStore : IFolderSyncJobStore, IRecoveryAwareStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly DurableJsonFile _file;

    public JsonFolderSyncJobStore(string? path = null)
    {
        _file = new DurableJsonFile(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S3Explorer",
            "sync-jobs.json"));
    }

    public JsonStoreRecoveryInfo? LastRecovery => _file.LastRecovery;

    public async Task<IReadOnlyList<FolderSyncJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var document = await _file.LoadAsync(
            static () => new Document(),
            Options,
            ValidateDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.Jobs;
    }

    public async Task SaveAsync(IReadOnlyCollection<FolderSyncJob> jobs, CancellationToken cancellationToken = default)
    {
        await _file.SaveAsync(
            new Document { Jobs = jobs.ToList() },
            Options,
            ValidateDocument,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateDocument(Document document)
    {
        if (document.Version != 1)
            throw new InvalidOperationException($"不支持的同步任务存储版本：{document.Version}");
        if (document.Jobs is null || document.Jobs.Any(job => job is null))
            throw new InvalidDataException("同步任务存储包含空集合或空记录。");
        ValidateJobs(document.Jobs);
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
