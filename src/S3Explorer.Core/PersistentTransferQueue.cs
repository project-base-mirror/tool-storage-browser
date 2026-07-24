namespace S3Explorer.Core;

public interface ITransferTaskExecutor
{
    Task ExecuteAsync(
        TransferTaskRecord task,
        IProgress<TransferProgress> progress,
        CancellationToken cancellationToken);
}

public sealed class TransferQueueChangedEventArgs(
    TransferStoreSnapshot snapshot,
    Guid? changedTaskId = null) : EventArgs
{
    public TransferStoreSnapshot Snapshot { get; } = snapshot;
    public Guid? ChangedTaskId { get; } = changedTaskId;
}

public sealed class TransferTaskProgressEventArgs(Guid taskId, TransferProgress progress) : EventArgs
{
    public Guid TaskId { get; } = taskId;
    public TransferProgress Progress { get; } = progress;
}

public sealed class PersistentTransferQueue : IAsyncDisposable
{
    private sealed class RuntimeTransfer
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public TransferTaskState RequestedStop { get; set; } = TransferTaskState.Cancelled;
    }

    private readonly ITransferTaskStore _store;
    private readonly ITransferTaskExecutor _executor;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<Guid, RuntimeTransfer> _running = [];
    private TransferStoreSnapshot _snapshot = new();
    private int _maxConcurrency;
    private bool _initialized;
    private bool _disposed;

    public PersistentTransferQueue(
        ITransferTaskStore store,
        ITransferTaskExecutor executor,
        int maxConcurrency = 4,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _executor = executor;
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 32);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler<TransferQueueChangedEventArgs>? Changed;
    public event EventHandler<TransferTaskProgressEventArgs>? ProgressChanged;

    public TransferStoreSnapshot Snapshot => _snapshot;
    public int ActiveCount => _snapshot.Tasks.Count(IsActive);
    public int RunningCount => _snapshot.Tasks.Count(task => task.State == TransferTaskState.Running);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_initialized)
                return;

            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var recovered = TransferTaskStateMachine.RecoverInterrupted(loaded, _clock());
            if (loaded.Tasks.Any(task => task.State == TransferTaskState.Running))
                await _store.SaveAsync(recovered, cancellationToken).ConfigureAwait(false);

            _snapshot = recovered;
            _initialized = true;
        }
        finally
        {
            _mutex.Release();
        }

        Publish();
        await PumpAsync().ConfigureAwait(false);
    }

    public async Task SetConcurrencyAsync(int value, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            _maxConcurrency = Math.Clamp(value, 1, 32);
        }
        finally
        {
            _mutex.Release();
        }

        await PumpAsync().ConfigureAwait(false);
    }

    public async Task EnqueueAsync(TransferTaskRecord task, CancellationToken cancellationToken = default)
    {
        task = task with
        {
            State = TransferTaskState.Queued,
            AttemptCount = 0,
            Failure = null,
            CreatedAt = task.CreatedAt == default ? _clock() : task.CreatedAt,
            UpdatedAt = _clock(),
            StartedAt = null,
            CompletedAt = null
        };
        task.Validate();

        await MutateAsync(
            snapshot =>
            {
                if (snapshot.Tasks.Any(item => item.Id == task.Id))
                    throw new InvalidOperationException($"传输任务 ID 已存在：{task.Id}");
                return snapshot with { Tasks = snapshot.Tasks.Append(task).ToArray() };
            },
            task.Id,
            cancellationToken).ConfigureAwait(false);

        await PumpAsync().ConfigureAwait(false);
    }

    public Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        StopOrTransitionAsync(taskId, TransferTaskState.Paused, cancellationToken);

    public Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        StopOrTransitionAsync(taskId, TransferTaskState.Cancelled, cancellationToken);

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(
            snapshot => ReplaceTask(snapshot, taskId, task =>
            {
                if (task.State == TransferTaskState.Queued)
                    return task;
                if (task.State is not (
                    TransferTaskState.Paused or
                    TransferTaskState.Interrupted or
                    TransferTaskState.Failed or
                    TransferTaskState.RetryPending))
                    throw new InvalidOperationException($"任务状态 {task.State} 不允许继续。");

                return TransferTaskStateMachine.Transition(task, TransferTaskState.Queued, _clock());
            }),
            taskId,
            cancellationToken).ConfigureAwait(false);

        await PumpAsync().ConfigureAwait(false);
    }

    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(
            snapshot => ReplaceTask(snapshot, taskId, task =>
            {
                if (task.State != TransferTaskState.Failed)
                    throw new InvalidOperationException("只有失败任务可以重试。");
                var retry = TransferTaskStateMachine.Transition(
                    task, TransferTaskState.RetryPending, _clock());
                return retry with { AttemptCount = 0, Failure = null, CompletedAt = null };
            }),
            taskId,
            cancellationToken).ConfigureAwait(false);

        await PumpAsync().ConfigureAwait(false);
    }

    public async Task RetryAllFailedAsync(CancellationToken cancellationToken = default)
    {
        await MutateAsync(
            snapshot => snapshot with
            {
                Tasks = snapshot.Tasks.Select(task =>
                {
                    if (task.State != TransferTaskState.Failed)
                        return task;
                    var retry = TransferTaskStateMachine.Transition(
                        task, TransferTaskState.RetryPending, _clock());
                    return retry with { AttemptCount = 0, Failure = null, CompletedAt = null };
                }).ToArray()
            },
            null,
            cancellationToken).ConfigureAwait(false);

        await PumpAsync().ConfigureAwait(false);
    }

    public async Task PauseAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            foreach (var runtime in _running.Values)
            {
                runtime.RequestedStop = TransferTaskState.Paused;
                runtime.Cancellation.Cancel();
            }

            var next = _snapshot with
            {
                Tasks = _snapshot.Tasks.Select(task =>
                    task.State is TransferTaskState.Queued or TransferTaskState.RetryPending
                        ? TransferTaskStateMachine.Transition(task, TransferTaskState.Paused, _clock())
                        : task).ToArray()
            };
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish();
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            foreach (var runtime in _running.Values)
            {
                runtime.RequestedStop = TransferTaskState.Cancelled;
                runtime.Cancellation.Cancel();
            }

            var next = _snapshot with
            {
                Tasks = _snapshot.Tasks.Select(task =>
                    task.State is TransferTaskState.Queued or
                        TransferTaskState.Paused or
                        TransferTaskState.RetryPending or
                        TransferTaskState.Interrupted or
                        TransferTaskState.Failed or
                        TransferTaskState.CleanupPending
                        ? TransferTaskStateMachine.Transition(task, TransferTaskState.Cancelled, _clock())
                        : task).ToArray()
            };
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish();
    }

    public Task RemoveCompletedAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(
            snapshot => snapshot with
            {
                Tasks = snapshot.Tasks
                    .Where(task => task.State is not (TransferTaskState.Completed or TransferTaskState.Cancelled))
                    .ToArray()
            },
            null,
            cancellationToken);

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (ActiveCount > 0)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopOrTransitionAsync(
        Guid taskId,
        TransferTaskState target,
        CancellationToken cancellationToken)
    {
        var needsPublish = false;
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (_running.TryGetValue(taskId, out var runtime))
            {
                runtime.RequestedStop = target;
                runtime.Cancellation.Cancel();
                return;
            }

            var task = FindTask(_snapshot, taskId);
            if (task.State == target)
                return;
            if (!TransferTaskStateMachine.CanTransition(task.State, target))
                throw new InvalidOperationException($"任务状态 {task.State} 不允许切换到 {target}。");

            var next = ReplaceTask(_snapshot, taskId, current =>
                TransferTaskStateMachine.Transition(current, target, _clock()));
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
            needsPublish = true;
        }
        finally
        {
            _mutex.Release();
        }

        if (needsPublish)
            Publish(taskId);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            TransferTaskRecord? task = null;
            RuntimeTransfer? runtime = null;

            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_initialized || _disposed || _running.Count >= _maxConcurrency)
                    return;

                var candidate = _snapshot.Tasks.FirstOrDefault(item =>
                    item.State is TransferTaskState.Queued or TransferTaskState.RetryPending);
                if (candidate is null)
                    return;

                var runningTask = TransferTaskStateMachine.Transition(
                    candidate, TransferTaskState.Running, _clock());
                var next = ReplaceTask(_snapshot, candidate.Id, _ => runningTask);
                await CommitLockedAsync(next, CancellationToken.None).ConfigureAwait(false);
                runtime = new RuntimeTransfer { Cancellation = new CancellationTokenSource() };
                _running.Add(candidate.Id, runtime);
                task = runningTask;
            }
            finally
            {
                _mutex.Release();
            }

            Publish(task.Id);
            _ = ExecuteAsync(task, runtime);
        }
    }

    private async Task ExecuteAsync(TransferTaskRecord task, RuntimeTransfer runtime)
    {
        var progress = new ImmediateProgress<TransferProgress>(value =>
            ProgressChanged?.Invoke(this, new TransferTaskProgressEventArgs(task.Id, value)));

        try
        {
            await _executor.ExecuteAsync(task, progress, runtime.Cancellation.Token).ConfigureAwait(false);
            await CompleteRunningAsync(task.Id, TransferTaskState.Completed, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            await CompleteRunningAsync(task.Id, runtime.RequestedStop, null).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CompleteRunningAsync(
                task.Id,
                TransferTaskState.Failed,
                TransferFailureClassifier.Classify(exception)).ConfigureAwait(false);
        }
        finally
        {
            runtime.Cancellation.Dispose();
        }
    }

    private async Task CompleteRunningAsync(
        Guid taskId,
        TransferTaskState state,
        TransferFailureInfo? failure)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _running.Remove(taskId);
            var next = ReplaceTask(_snapshot, taskId, task =>
                TransferTaskStateMachine.Transition(task, state, _clock(), failure));
            await CommitLockedAsync(next, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(taskId);
        await PumpAsync().ConfigureAwait(false);
    }

    private async Task MutateAsync(
        Func<TransferStoreSnapshot, TransferStoreSnapshot> mutation,
        Guid? changedTaskId,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var next = mutation(_snapshot);
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Publish(changedTaskId);
    }

    private async Task CommitLockedAsync(
        TransferStoreSnapshot next,
        CancellationToken cancellationToken)
    {
        next.Validate();
        await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        _snapshot = next;
    }

    private void Publish(Guid? changedTaskId = null) =>
        Changed?.Invoke(this, new TransferQueueChangedEventArgs(_snapshot, changedTaskId));

    private static TransferStoreSnapshot ReplaceTask(
        TransferStoreSnapshot snapshot,
        Guid taskId,
        Func<TransferTaskRecord, TransferTaskRecord> update)
    {
        var found = false;
        var tasks = snapshot.Tasks.Select(task =>
        {
            if (task.Id != taskId)
                return task;
            found = true;
            return update(task);
        }).ToArray();
        if (!found)
            throw new KeyNotFoundException($"找不到传输任务：{taskId}");
        return snapshot with { Tasks = tasks };
    }

    private static TransferTaskRecord FindTask(TransferStoreSnapshot snapshot, Guid taskId) =>
        snapshot.Tasks.FirstOrDefault(task => task.Id == taskId)
        ?? throw new KeyNotFoundException($"找不到传输任务：{taskId}");

    private static bool IsActive(TransferTaskRecord task) =>
        task.State is TransferTaskState.Queued or
            TransferTaskState.Running or
            TransferTaskState.RetryPending;

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized)
            throw new InvalidOperationException("传输队列尚未初始化。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await PauseAllAsync().ConfigureAwait(false);
        await WaitForIdleAsync().ConfigureAwait(false);
        _disposed = true;
        _mutex.Dispose();
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public static class TransferFailureClassifier
{
    public static TransferFailureInfo Classify(Exception exception)
    {
        var category = exception switch
        {
            TimeoutException => TransferFailureCategory.Timeout,
            UnauthorizedAccessException => TransferFailureCategory.LocalIo,
            FileNotFoundException or DirectoryNotFoundException => TransferFailureCategory.NotFound,
            IOException => TransferFailureCategory.LocalIo,
            ArgumentException => TransferFailureCategory.Validation,
            OperationCanceledException => TransferFailureCategory.Cancelled,
            _ => TransferFailureCategory.Unknown
        };

        var retryable = category is TransferFailureCategory.Network or
            TransferFailureCategory.Timeout or
            TransferFailureCategory.Service or
            TransferFailureCategory.Unknown;
        return new TransferFailureInfo(
            SensitiveDataRedactor.Redact(exception.Message),
            category,
            Retryable: retryable);
    }
}
