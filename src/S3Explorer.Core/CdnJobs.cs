namespace S3Explorer.Core;

public enum CdnJobAction
{
    Warmup,
    PurgeUrl,
    PurgeThenWarmup
}

public enum CdnJobState
{
    Pending,
    Running,
    WaitingProvider,
    Completed,
    Failed,
    Cancelled
}

public enum CdnProviderOperationState
{
    Completed,
    Accepted,
    Failed
}

public sealed record CdnJobRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string IdempotencyKey { get; init; } = string.Empty;
    public Guid CdnProfileId { get; init; }
    public Guid? BindingId { get; init; }
    public Guid? TransferTaskId { get; init; }
    public CdnJobAction Action { get; init; }
    public CdnJobState State { get; init; } = CdnJobState.Pending;
    public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();
    public int AttemptCount { get; init; }
    public int MaxAttempts { get; init; } = 4;
    public int RetryBaseDelaySeconds { get; init; } = 2;
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string ProviderTaskId { get; init; } = string.Empty;
    public string LastMessage { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;
    public int? LastStatusCode { get; init; }
    public long BytesRead { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public bool IsTerminal => State is CdnJobState.Completed or CdnJobState.Cancelled;

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("CDN 任务 ID 不能为空。", nameof(Id));
        if (string.IsNullOrWhiteSpace(IdempotencyKey) || IdempotencyKey.Length > 1000)
            throw new ArgumentException("CDN 任务幂等键必须为 1–1000 个字符。", nameof(IdempotencyKey));
        if (CdnProfileId == Guid.Empty)
            throw new ArgumentException("CDN 任务必须指定 CDN 配置。", nameof(CdnProfileId));
        if (!Enum.IsDefined(Action)) throw new ArgumentOutOfRangeException(nameof(Action));
        if (!Enum.IsDefined(State)) throw new ArgumentOutOfRangeException(nameof(State));
        if (Urls.Count is < 1 or > 10_000)
            throw new ArgumentException("CDN 任务必须包含 1–10000 个 URL。", nameof(Urls));
        foreach (var value in Urls)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"CDN 任务 URL 无效：{value}", nameof(Urls));
        }
        if (AttemptCount < 0 || MaxAttempts is < 1 or > 100 || AttemptCount > MaxAttempts)
            throw new ArgumentOutOfRangeException(nameof(AttemptCount));
        if (RetryBaseDelaySeconds is < 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelaySeconds));
        if (State == CdnJobState.WaitingProvider && string.IsNullOrWhiteSpace(ProviderTaskId))
            throw new ArgumentException("等待 Provider 的任务必须包含 Provider 任务 ID。");
    }
}

public sealed record CdnJobStoreSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset AutomationStartedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<CdnJobRecord> Jobs { get; init; } = Array.Empty<CdnJobRecord>();

    public void Validate()
    {
        if (SchemaVersion != 1)
            throw new InvalidOperationException($"不支持的 CDN 任务存储版本：{SchemaVersion}");
        if (AutomationStartedAt == default)
            throw new InvalidOperationException("CDN 自动化起始时间无效。");
        foreach (var job in Jobs) job.Validate();
        if (Jobs.Select(job => job.Id).Distinct().Count() != Jobs.Count)
            throw new InvalidOperationException("CDN 任务 ID 重复。");
        if (Jobs.Select(job => job.IdempotencyKey).Distinct(StringComparer.Ordinal).Count() != Jobs.Count)
            throw new InvalidOperationException("CDN 任务幂等键重复。");
    }
}

public interface ICdnJobStore
{
    Task<CdnJobStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CdnJobStoreSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed record CdnProviderRequest(
    CdnJobAction Action,
    CdnProfile Profile,
    CdnCredential? Credential,
    IReadOnlyList<Uri> Urls,
    string ProviderTaskId = "");

public sealed record CdnProviderResult(
    CdnProviderOperationState State,
    string Message,
    bool Retryable = false,
    string ProviderTaskId = "",
    int? StatusCode = null,
    string ResponseSnippet = "",
    long BytesRead = 0);

public interface ICdnProvider
{
    string ProviderId { get; }
    CdnCapabilities Capabilities { get; }
    Task<CdnProviderResult> SubmitAsync(CdnProviderRequest request, CancellationToken cancellationToken);
    Task<CdnProviderResult> QueryAsync(CdnProviderRequest request, CancellationToken cancellationToken);
}

public interface ICdnJobExecutor
{
    Task<CdnProviderResult> ExecuteAsync(CdnJobRecord job, CancellationToken cancellationToken);
}

public sealed class CdnJobQueueChangedEventArgs(CdnJobStoreSnapshot snapshot, Guid? changedJobId = null) : EventArgs
{
    public CdnJobStoreSnapshot Snapshot { get; } = snapshot;
    public Guid? ChangedJobId { get; } = changedJobId;
}

public sealed class PersistentCdnJobQueue : IAsyncDisposable
{
    private sealed class RuntimeJob
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public Task? Execution { get; set; }
    }

    private readonly ICdnJobStore _store;
    private readonly ICdnJobExecutor _executor;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<double> _jitter;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<Guid, RuntimeJob> _running = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CdnJobStoreSnapshot _snapshot = new();
    private Task? _worker;
    private int _maxConcurrency;
    private bool _initialized;
    private bool _disposed;

    public PersistentCdnJobQueue(
        ICdnJobStore store,
        ICdnJobExecutor executor,
        int maxConcurrency = 2,
        Func<DateTimeOffset>? clock = null,
        Func<double>? jitter = null)
    {
        _store = store;
        _executor = executor;
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    public event EventHandler<CdnJobQueueChangedEventArgs>? Changed;
    public CdnJobStoreSnapshot Snapshot => _snapshot;
    public int ActiveCount => _snapshot.Jobs.Count(job =>
        job.State is CdnJobState.Pending or CdnJobState.Running or CdnJobState.WaitingProvider);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_initialized) return;
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock();
            var jobs = loaded.Jobs.Select(job => job.State == CdnJobState.Running
                ? job with
                {
                    State = CdnJobState.Pending,
                    NextAttemptAt = now,
                    UpdatedAt = now,
                    LastMessage = "程序重新启动，任务已恢复等待执行。"
                }
                : job).ToArray();
            var recovered = loaded with { Jobs = jobs };
            recovered.Validate();
            if (!jobs.SequenceEqual(loaded.Jobs))
                await _store.SaveAsync(recovered, cancellationToken).ConfigureAwait(false);
            _snapshot = recovered;
            _initialized = true;
            _worker = Task.Run(WorkerLoopAsync);
        }
        finally
        {
            _mutex.Release();
        }
        Publish();
    }

    public async Task<CdnJobRecord> EnqueueAsync(
        CdnJobRecord job,
        CancellationToken cancellationToken = default)
    {
        var now = _clock();
        job = job with
        {
            State = CdnJobState.Pending,
            AttemptCount = 0,
            ProviderTaskId = string.Empty,
            LastMessage = string.Empty,
            LastError = string.Empty,
            LastStatusCode = null,
            BytesRead = 0,
            NextAttemptAt = null,
            CreatedAt = job.CreatedAt == default ? now : job.CreatedAt,
            UpdatedAt = now,
            StartedAt = null,
            CompletedAt = null
        };
        job.Validate();

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var existing = _snapshot.Jobs.FirstOrDefault(value =>
                string.Equals(value.IdempotencyKey, job.IdempotencyKey, StringComparison.Ordinal));
            if (existing is not null) return existing;
            if (_snapshot.Jobs.Any(value => value.Id == job.Id))
                throw new InvalidOperationException($"CDN 任务 ID 已存在：{job.Id}");
            await CommitLockedAsync(
                _snapshot with { Jobs = _snapshot.Jobs.Append(job).ToArray() },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish(job.Id);
        return job;
    }

    public Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        MutateAsync(jobId, job =>
        {
            if (job.State != CdnJobState.Failed)
                throw new InvalidOperationException("只有失败的 CDN 任务可以重试。");
            var now = _clock();
            return job with
            {
                State = CdnJobState.Pending,
                AttemptCount = 0,
                ProviderTaskId = string.Empty,
                LastError = string.Empty,
                LastMessage = "等待重试。",
                LastStatusCode = null,
                NextAttemptAt = now,
                StartedAt = null,
                CompletedAt = null,
                UpdatedAt = now
            };
        }, cancellationToken);

    public async Task RetryAllFailedAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var now = _clock();
            var jobs = _snapshot.Jobs.Select(job => job.State == CdnJobState.Failed
                ? job with
                {
                    State = CdnJobState.Pending,
                    AttemptCount = 0,
                    ProviderTaskId = string.Empty,
                    LastError = string.Empty,
                    LastMessage = "等待重试。",
                    LastStatusCode = null,
                    NextAttemptAt = now,
                    StartedAt = null,
                    CompletedAt = null,
                    UpdatedAt = now
                }
                : job).ToArray();
            await CommitLockedAsync(_snapshot with { Jobs = jobs }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish();
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? running = null;
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var current = FindJob(_snapshot, jobId);
            if (current.IsTerminal) return;
            if (_running.TryGetValue(jobId, out var runtime)) running = runtime.Cancellation;
            var now = _clock();
            await CommitLockedAsync(ReplaceJob(_snapshot, jobId, job => job with
            {
                State = CdnJobState.Cancelled,
                NextAttemptAt = null,
                LastMessage = "任务已取消。",
                LastError = string.Empty,
                UpdatedAt = now,
                CompletedAt = now
            }), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        running?.Cancel();
        Publish(jobId);
    }

    public async Task RemoveCompletedAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            await CommitLockedAsync(_snapshot with
            {
                Jobs = _snapshot.Jobs.Where(job =>
                    job.State is not (CdnJobState.Completed or CdnJobState.Cancelled)).ToArray()
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish();
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (ActiveCount > 0)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await StartReadyJobsAsync().ConfigureAwait(false);
                await Task.Delay(200, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task StartReadyJobsAsync()
    {
        List<(CdnJobRecord Job, RuntimeJob Runtime)> started = [];
        await _mutex.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_initialized || _disposed) return;
            var now = _clock();
            var available = Math.Max(0, _maxConcurrency - _running.Count);
            var candidates = _snapshot.Jobs
                .Where(job =>
                    job.State is CdnJobState.Pending or CdnJobState.WaitingProvider &&
                    (job.NextAttemptAt is null || job.NextAttemptAt <= now))
                .OrderBy(job => job.NextAttemptAt ?? job.CreatedAt)
                .ThenBy(job => job.CreatedAt)
                .Take(available)
                .ToArray();
            if (candidates.Length == 0) return;

            var jobs = _snapshot.Jobs.ToArray();
            foreach (var candidate in candidates)
            {
                var runningJob = candidate with
                {
                    State = CdnJobState.Running,
                    AttemptCount = Math.Min(candidate.MaxAttempts, candidate.AttemptCount + 1),
                    NextAttemptAt = null,
                    StartedAt = candidate.StartedAt ?? now,
                    UpdatedAt = now,
                    LastMessage = candidate.ProviderTaskId.Length > 0
                        ? "正在查询 Provider 任务状态。"
                        : "正在提交 CDN 操作。"
                };
                jobs[Array.FindIndex(jobs, value => value.Id == candidate.Id)] = runningJob;
                var runtime = new RuntimeJob
                {
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token)
                };
                _running.Add(candidate.Id, runtime);
                started.Add((runningJob, runtime));
            }
            await CommitLockedAsync(_snapshot with { Jobs = jobs }, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        if (started.Count > 0) Publish();
        foreach (var pair in started)
            pair.Runtime.Execution = Task.Run(() => ExecuteJobAsync(pair.Job, pair.Runtime));
    }

    private async Task ExecuteJobAsync(CdnJobRecord job, RuntimeJob runtime)
    {
        CdnProviderResult result;
        try
        {
            result = await _executor.ExecuteAsync(job, runtime.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            await CompleteExecutionAsync(job.Id, null, cancelled: true).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            result = new CdnProviderResult(
                CdnProviderOperationState.Failed,
                "CDN 任务执行异常。",
                Retryable: true,
                ResponseSnippet: SensitiveDataRedactor.Redact(exception.Message));
        }
        await CompleteExecutionAsync(job.Id, result, cancelled: false).ConfigureAwait(false);
    }

    private async Task CompleteExecutionAsync(Guid jobId, CdnProviderResult? result, bool cancelled)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running.Remove(jobId, out var runtime)) return;
            runtime.Cancellation.Dispose();
            var current = FindJob(_snapshot, jobId);
            if (current.State == CdnJobState.Cancelled) return;
            var now = _clock();
            var updated = cancelled && _disposed
                ? current with
                {
                    State = CdnJobState.Pending,
                    NextAttemptAt = now,
                    LastMessage = "程序退出，任务将在下次启动时继续。",
                    UpdatedAt = now
                }
                : ApplyResult(current, result!, now);
            await CommitLockedAsync(
                ReplaceJob(_snapshot, jobId, _ => updated),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish(jobId);
    }

    private CdnJobRecord ApplyResult(CdnJobRecord job, CdnProviderResult result, DateTimeOffset now)
    {
        var message = SensitiveDataRedactor.Redact(result.Message);
        var error = result.State == CdnProviderOperationState.Failed
            ? SensitiveDataRedactor.Redact(
                result.ResponseSnippet.Length > 0 ? result.ResponseSnippet : result.Message)
            : string.Empty;
        if (result.State == CdnProviderOperationState.Completed)
            return job with
            {
                State = CdnJobState.Completed,
                ProviderTaskId = result.ProviderTaskId,
                LastMessage = message,
                LastError = string.Empty,
                LastStatusCode = result.StatusCode,
                BytesRead = checked(job.BytesRead + Math.Max(0, result.BytesRead)),
                NextAttemptAt = null,
                UpdatedAt = now,
                CompletedAt = now
            };

        if (result.State == CdnProviderOperationState.Accepted)
        {
            if (string.IsNullOrWhiteSpace(result.ProviderTaskId))
                throw new InvalidOperationException("Provider 接受异步任务时必须返回任务 ID。");
            return job with
            {
                State = CdnJobState.WaitingProvider,
                ProviderTaskId = result.ProviderTaskId,
                LastMessage = message,
                LastError = string.Empty,
                LastStatusCode = result.StatusCode,
                BytesRead = checked(job.BytesRead + Math.Max(0, result.BytesRead)),
                NextAttemptAt = now.AddSeconds(5),
                UpdatedAt = now
            };
        }

        if (result.Retryable && job.AttemptCount < job.MaxAttempts)
        {
            var backoff = RetryBackoff.Calculate(job.RetryBaseDelaySeconds, job.AttemptCount);
            var jitterSeconds = Math.Min(30, backoff.TotalSeconds * 0.2 * Math.Clamp(_jitter(), 0, 1));
            return job with
            {
                State = CdnJobState.Pending,
                LastMessage = "操作失败，等待自动重试。",
                LastError = error,
                LastStatusCode = result.StatusCode,
                BytesRead = checked(job.BytesRead + Math.Max(0, result.BytesRead)),
                NextAttemptAt = now.Add(backoff).AddSeconds(jitterSeconds),
                UpdatedAt = now
            };
        }

        return job with
        {
            State = CdnJobState.Failed,
            LastMessage = message,
            LastError = error,
            LastStatusCode = result.StatusCode,
            BytesRead = checked(job.BytesRead + Math.Max(0, result.BytesRead)),
            NextAttemptAt = null,
            UpdatedAt = now,
            CompletedAt = now
        };
    }

    private async Task MutateAsync(
        Guid jobId,
        Func<CdnJobRecord, CdnJobRecord> mutation,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var next = ReplaceJob(_snapshot, jobId, mutation);
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish(jobId);
    }

    private async Task CommitLockedAsync(CdnJobStoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        snapshot.Validate();
        await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        _snapshot = snapshot;
    }

    private void Publish(Guid? jobId = null) =>
        Changed?.Invoke(this, new CdnJobQueueChangedEventArgs(_snapshot, jobId));

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("CDN 任务队列尚未初始化。");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PersistentCdnJobQueue));
    }

    private static CdnJobRecord FindJob(CdnJobStoreSnapshot snapshot, Guid id) =>
        snapshot.Jobs.FirstOrDefault(job => job.Id == id)
        ?? throw new KeyNotFoundException($"未找到 CDN 任务：{id}");

    private static CdnJobStoreSnapshot ReplaceJob(
        CdnJobStoreSnapshot snapshot,
        Guid id,
        Func<CdnJobRecord, CdnJobRecord> replacement) =>
        snapshot with
        {
            Jobs = snapshot.Jobs.Select(job => job.Id == id ? replacement(job) : job).ToArray()
        };

    public async ValueTask DisposeAsync()
    {
        List<Task> running;
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            foreach (var runtime in _running.Values) runtime.Cancellation.Cancel();
            running = _running.Values.Select(value => value.Execution).OfType<Task>().ToList();
        }
        finally
        {
            _mutex.Release();
        }

        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _mutex.Dispose();
    }
}
