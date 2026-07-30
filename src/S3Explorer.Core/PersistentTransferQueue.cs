using System.Net;
using System.Net.Sockets;

namespace S3Explorer.Core;

public interface ITransferTaskExecutionContext
{
    TransferTaskRecord Task { get; }
    void ReportProgress(TransferProgress progress);
    Task UpdateCheckpointAsync(
        long transferredBytes,
        DownloadCheckpoint? downloadCheckpoint,
        MultipartUploadCheckpoint? multipartCheckpoint,
        CancellationToken cancellationToken = default);
    Task UpdateDestinationSnapshotAsync(
        bool destinationExistedBeforeTransfer,
        CancellationToken cancellationToken = default);
}

public interface ITransferTaskExecutor
{
    Task ExecuteAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken);

    Task AbortMultipartAsync(ITransferTaskExecutionContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class TransferQueueChangedEventArgs(TransferStoreSnapshot snapshot, Guid? changedTaskId = null) : EventArgs
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

    private sealed class ExecutionContext(PersistentTransferQueue owner, Guid taskId) : ITransferTaskExecutionContext
    {
        public TransferTaskRecord Task => FindTask(owner._snapshot, taskId);

        public void ReportProgress(TransferProgress progress) =>
            owner.ProgressChanged?.Invoke(owner, new TransferTaskProgressEventArgs(taskId, progress));

        public Task UpdateCheckpointAsync(
            long transferredBytes,
            DownloadCheckpoint? downloadCheckpoint,
            MultipartUploadCheckpoint? multipartCheckpoint,
            CancellationToken cancellationToken = default) =>
            owner.UpdateCheckpointAsync(
                taskId, transferredBytes, downloadCheckpoint, multipartCheckpoint, cancellationToken);

        public Task UpdateDestinationSnapshotAsync(
            bool destinationExistedBeforeTransfer,
            CancellationToken cancellationToken = default) =>
            owner.UpdateDestinationSnapshotAsync(
                taskId, destinationExistedBeforeTransfer, cancellationToken);
    }

    private readonly ITransferTaskStore _store;
    private readonly ITransferTaskExecutor _executor;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<Guid, RuntimeTransfer> _running = [];
    private readonly CancellationTokenSource _lifetime = new();
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
            if (_initialized) return;
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
            NextAttemptAt = null,
            CreatedAt = task.CreatedAt == default ? _clock() : task.CreatedAt,
            UpdatedAt = _clock(),
            StartedAt = null,
            CompletedAt = null
        };
        task.Validate();
        await MutateAsync(snapshot =>
        {
            if (snapshot.Tasks.Any(item => item.Id == task.Id))
                throw new InvalidOperationException($"传输任务 ID 已存在：{task.Id}");
            return snapshot with { Tasks = snapshot.Tasks.Append(task).ToArray() };
        }, task.Id, cancellationToken).ConfigureAwait(false);
        await PumpAsync().ConfigureAwait(false);
    }

    public async Task<TransferBatchRecord> CreateBatchAsync(
        TransferBatchRecord batch,
        CancellationToken cancellationToken = default)
    {
        var now = _clock();
        batch = batch with
        {
            TaskIds = Array.Empty<Guid>(),
            DiscoveryCompleted = false,
            SkippedCount = 0,
            CancellationRequested = false,
            CreatedAt = batch.CreatedAt == default ? now : batch.CreatedAt,
            UpdatedAt = now
        };
        batch.Validate();
        await MutateAsync(snapshot =>
        {
            if (snapshot.Batches.Any(item => item.Id == batch.Id))
                throw new InvalidOperationException($"传输批次 ID 已存在：{batch.Id}");
            return snapshot with { Batches = snapshot.Batches.Append(batch).ToArray() };
        }, null, cancellationToken).ConfigureAwait(false);
        return batch;
    }

    public async Task AddBatchTasksAsync(
        Guid batchId,
        IReadOnlyCollection<TransferTaskRecord> tasks,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Count == 0) return;
        var now = _clock();
        var prepared = tasks.Select(task => task with
        {
            BatchId = batchId,
            Kind = TransferTaskKind.FolderBatchItem,
            State = TransferTaskState.Queued,
            AttemptCount = 0,
            Failure = null,
            NextAttemptAt = null,
            TransferredBytes = 0,
            CreatedAt = task.CreatedAt == default ? now : task.CreatedAt,
            UpdatedAt = now,
            StartedAt = null,
            CompletedAt = null
        }).ToArray();
        if (prepared.Select(task => task.Id).Distinct().Count() != prepared.Length)
            throw new InvalidOperationException("批次子任务 ID 重复。");
        foreach (var task in prepared) task.Validate();

        await MutateAsync(snapshot =>
        {
            var batch = FindBatch(snapshot, batchId);
            if (batch.CancellationRequested)
                throw new InvalidOperationException("批次已请求取消，不能继续加入任务。");
            if (batch.DiscoveryCompleted)
                throw new InvalidOperationException("批次发现已完成，不能继续加入任务。");
            if (prepared.Any(task =>
                task.ProfileId != batch.ProfileId ||
                task.Direction != batch.Direction ||
                !string.Equals(task.Bucket, batch.Bucket, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("批次子任务的连接、方向或 Bucket 与父批次不一致。");
            }
            if (snapshot.Tasks.Any(existing => prepared.Any(task => task.Id == existing.Id)))
                throw new InvalidOperationException("批次子任务 ID 已存在。");

            var updatedBatch = batch with
            {
                TaskIds = batch.TaskIds.Concat(prepared.Select(task => task.Id)).ToArray(),
                UpdatedAt = now
            };
            var next = snapshot with { Tasks = snapshot.Tasks.Concat(prepared).ToArray() };
            return ReplaceBatch(next, batchId, _ => updatedBatch);
        }, null, cancellationToken).ConfigureAwait(false);
        await PumpAsync().ConfigureAwait(false);
    }

    public Task CompleteBatchDiscoveryAsync(
        Guid batchId,
        int skippedCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (skippedCount < 0) throw new ArgumentOutOfRangeException(nameof(skippedCount));
        return MutateAsync(snapshot => ReplaceBatch(snapshot, batchId, batch => batch with
        {
            DiscoveryCompleted = true,
            SkippedCount = checked(batch.SkippedCount + skippedCount),
            UpdatedAt = _clock()
        }), null, cancellationToken);
    }

    public async Task<int> RetryBatchFailuresAsync(
        Guid batchId,
        IReadOnlyCollection<Guid>? selectedTaskIds = null,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedTaskIds?.ToHashSet();
        var retried = 0;
        var now = _clock();
        await MutateAsync(snapshot =>
        {
            var batch = FindBatch(snapshot, batchId);
            var childIds = batch.TaskIds.ToHashSet();
            if (selected is not null && selected.Any(id => !childIds.Contains(id)))
                throw new InvalidOperationException("选中的任务不属于该批次。");

            var tasks = snapshot.Tasks.Select(task =>
            {
                if (task.BatchId != batchId ||
                    task.State != TransferTaskState.Failed ||
                    task.Failure?.Retryable != true ||
                    (selected is not null && !selected.Contains(task.Id)))
                {
                    return task;
                }

                retried++;
                var retry = TransferTaskStateMachine.Transition(task, TransferTaskState.RetryPending, now);
                return retry with
                {
                    AttemptCount = 0,
                    Failure = null,
                    NextAttemptAt = now,
                    CompletedAt = null
                };
            }).ToArray();
            var updated = snapshot with { Tasks = tasks };
            return ReplaceBatch(updated, batchId, current => current with
            {
                CancellationRequested = false,
                UpdatedAt = now
            });
        }, null, cancellationToken).ConfigureAwait(false);
        await PumpAsync().ConfigureAwait(false);
        return retried;
    }

    public async Task CancelBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var batch = FindBatch(_snapshot, batchId);
            var childIds = batch.TaskIds.ToHashSet();
            foreach (var pair in _running.Where(pair => childIds.Contains(pair.Key)).ToArray())
            {
                pair.Value.RequestedStop = TransferTaskState.Cancelled;
                pair.Value.Cancellation.Cancel();
            }

            var now = _clock();
            var tasks = _snapshot.Tasks.Select(task =>
            {
                if (task.BatchId != batchId ||
                    task.State is not (TransferTaskState.Queued or TransferTaskState.Paused or
                        TransferTaskState.RetryPending or TransferTaskState.Interrupted))
                {
                    return task;
                }
                return TransferTaskStateMachine.Transition(task, TransferTaskState.Cancelled, now);
            }).ToArray();
            var next = ReplaceBatch(_snapshot with { Tasks = tasks }, batchId, current => current with
            {
                CancellationRequested = true,
                DiscoveryCompleted = true,
                UpdatedAt = now
            });
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish();
        await PumpAsync().ConfigureAwait(false);
    }

    public Task PauseAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        StopOrTransitionAsync(taskId, TransferTaskState.Paused, cancellationToken);

    public Task CancelAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        StopOrTransitionAsync(taskId, TransferTaskState.Cancelled, cancellationToken);

    public async Task ResumeAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(snapshot => ReplaceTask(snapshot, taskId, task =>
        {
            if (task.State == TransferTaskState.Queued) return task;
            if (task.State is not (TransferTaskState.Paused or TransferTaskState.Interrupted or TransferTaskState.Failed or TransferTaskState.RetryPending))
                throw new InvalidOperationException($"任务状态 {task.State} 不允许继续。");
            return TransferTaskStateMachine.Transition(task, TransferTaskState.Queued, _clock()) with
            {
                Failure = null,
                NextAttemptAt = null,
                CompletedAt = null
            };
        }), taskId, cancellationToken).ConfigureAwait(false);
        await PumpAsync().ConfigureAwait(false);
    }

    public async Task RetryAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(snapshot => ReplaceTask(snapshot, taskId, task =>
        {
            if (task.State != TransferTaskState.Failed)
                throw new InvalidOperationException("只有失败任务可以重试。");
            var retry = TransferTaskStateMachine.Transition(task, TransferTaskState.RetryPending, _clock());
            return retry with
            {
                AttemptCount = 0,
                Failure = null,
                NextAttemptAt = _clock(),
                CompletedAt = null
            };
        }), taskId, cancellationToken).ConfigureAwait(false);
        await PumpAsync().ConfigureAwait(false);
    }

    public async Task RetryAllFailedAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock();
        await MutateAsync(snapshot => snapshot with
        {
            Tasks = snapshot.Tasks.Select(task =>
            {
                if (task.State != TransferTaskState.Failed) return task;
                var retry = TransferTaskStateMachine.Transition(task, TransferTaskState.RetryPending, now);
                return retry with { AttemptCount = 0, Failure = null, NextAttemptAt = now, CompletedAt = null };
            }).ToArray()
        }, null, cancellationToken).ConfigureAwait(false);
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
            var now = _clock();
            var next = _snapshot with
            {
                Tasks = _snapshot.Tasks.Select(task =>
                    task.State is TransferTaskState.Queued or TransferTaskState.RetryPending
                        ? TransferTaskStateMachine.Transition(task, TransferTaskState.Paused, now)
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
            var now = _clock();
            var next = _snapshot with
            {
                Tasks = _snapshot.Tasks.Select(task =>
                    task.State is TransferTaskState.Queued or TransferTaskState.Paused or TransferTaskState.RetryPending or
                        TransferTaskState.Interrupted or TransferTaskState.Failed or TransferTaskState.CleanupPending
                        ? TransferTaskStateMachine.Transition(task, TransferTaskState.Cancelled, now)
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

    public Task RemoveCompletedAsync(CancellationToken cancellationToken = default) => MutateAsync(
        snapshot => snapshot with
        {
            Tasks = snapshot.Tasks.Where(task =>
                task.BatchId is not null ||
                task.State is not (TransferTaskState.Completed or TransferTaskState.Cancelled)).ToArray()
        }, null, cancellationToken);

    public Task MarkMultipartCleanedAsync(
        Guid profileId,
        string bucket,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(snapshot => snapshot with
        {
            Tasks = snapshot.Tasks.Select(task =>
            {
                var checkpoint = task.MultipartCheckpoint;
                if (task.ProfileId != profileId ||
                    checkpoint is null ||
                    !string.Equals(checkpoint.UploadId, uploadId, StringComparison.Ordinal) ||
                    !string.Equals(task.Bucket, bucket, StringComparison.Ordinal) ||
                    !string.Equals(task.ObjectKey, objectKey, StringComparison.Ordinal))
                {
                    return task;
                }

                if (task.State == TransferTaskState.CleanupPending)
                {
                    return TransferTaskStateMachine.Transition(task, TransferTaskState.Cancelled, _clock()) with
                    {
                        MultipartCheckpoint = null,
                        Failure = null
                    };
                }

                return task with
                {
                    MultipartCheckpoint = null,
                    UpdatedAt = _clock()
                };
            }).ToArray()
        }, null, cancellationToken);

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (ActiveCount > 0)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopOrTransitionAsync(Guid taskId, TransferTaskState target, CancellationToken cancellationToken)
    {
        var publish = false;
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
            if (task.State == target) return;
            if (!TransferTaskStateMachine.CanTransition(task.State, target))
                throw new InvalidOperationException($"任务状态 {task.State} 不允许切换到 {target}。");
            var next = ReplaceTask(_snapshot, taskId, current => TransferTaskStateMachine.Transition(current, target, _clock()));
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
            publish = true;
        }
        finally
        {
            _mutex.Release();
        }
        if (publish) Publish(taskId);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            TransferTaskRecord? task = null;
            RuntimeTransfer? runtime = null;
            DateTimeOffset? nextRetryAt = null;
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_initialized || _disposed || _running.Count >= _maxConcurrency) return;
                var now = _clock();
                var candidate = _snapshot.Tasks.FirstOrDefault(item =>
                    item.State == TransferTaskState.Queued ||
                    (item.State == TransferTaskState.RetryPending && (item.NextAttemptAt is null || item.NextAttemptAt <= now)));
                if (candidate is null)
                {
                    nextRetryAt = _snapshot.Tasks
                        .Where(item => item.State == TransferTaskState.RetryPending && item.NextAttemptAt > now)
                        .Select(item => item.NextAttemptAt)
                        .Min();
                }
                else
                {
                    var runningTask = TransferTaskStateMachine.Transition(candidate, TransferTaskState.Running, now);
                    await CommitLockedAsync(ReplaceTask(_snapshot, candidate.Id, _ => runningTask), CancellationToken.None)
                        .ConfigureAwait(false);
                    runtime = new RuntimeTransfer { Cancellation = new CancellationTokenSource() };
                    _running.Add(candidate.Id, runtime);
                    task = runningTask;
                }
            }
            finally
            {
                _mutex.Release();
            }

            if (task is null)
            {
                if (nextRetryAt is not null) ScheduleRetryWakeup(nextRetryAt.Value);
                return;
            }
            Publish(task.Id);
            _ = ExecuteAsync(task, runtime!);
        }
    }

    private async Task ExecuteAsync(TransferTaskRecord task, RuntimeTransfer runtime)
    {
        var context = new ExecutionContext(this, task.Id);
        try
        {
            await _executor.ExecuteAsync(context, runtime.Cancellation.Token).ConfigureAwait(false);
            await CompleteRunningAsync(task.Id, TransferTaskState.Completed, null, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            var current = FindTask(_snapshot, task.Id);
            if (runtime.RequestedStop == TransferTaskState.Cancelled && current.MultipartCheckpoint is { } checkpoint)
            {
                try
                {
                    await _executor.AbortMultipartAsync(context, CancellationToken.None).ConfigureAwait(false);
                    await CompleteRunningAsync(
                        task.Id, TransferTaskState.Cancelled, null, null, null, overrideMultipartCheckpoint: true)
                        .ConfigureAwait(false);
                }
                catch (Exception abortException)
                {
                    var failure = TransferFailureClassifier.Classify(abortException);
                    await CompleteRunningAsync(
                        task.Id,
                        TransferTaskState.CleanupPending,
                        failure,
                        null,
                        checkpoint with { CleanupPending = true },
                        overrideMultipartCheckpoint: true).ConfigureAwait(false);
                }
            }
            else
            {
                await CompleteRunningAsync(task.Id, runtime.RequestedStop, null, null).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            var failure = TransferFailureClassifier.Classify(exception);
            var current = FindTask(_snapshot, task.Id);
            if (failure.Retryable && current.AttemptCount < current.MaxAttempts)
            {
                var nextAttempt = _clock().Add(RetryBackoff.Calculate(current.RetryBaseDelaySeconds, current.AttemptCount));
                await CompleteRunningAsync(task.Id, TransferTaskState.RetryPending, failure, nextAttempt).ConfigureAwait(false);
            }
            else
            {
                await CompleteRunningAsync(task.Id, TransferTaskState.Failed, failure, null).ConfigureAwait(false);
            }
        }
        finally
        {
            runtime.Cancellation.Dispose();
        }
    }

    private async Task CompleteRunningAsync(
        Guid taskId,
        TransferTaskState state,
        TransferFailureInfo? failure,
        DateTimeOffset? nextAttemptAt,
        MultipartUploadCheckpoint? multipartCheckpoint = null,
        bool overrideMultipartCheckpoint = false)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            _running.Remove(taskId);
            var next = ReplaceTask(_snapshot, taskId, task =>
            {
                var transitioned = TransferTaskStateMachine.Transition(task, state, _clock(), failure);
                if (state == TransferTaskState.RetryPending)
                    transitioned = transitioned with { NextAttemptAt = nextAttemptAt };
                if (overrideMultipartCheckpoint)
                    transitioned = transitioned with { MultipartCheckpoint = multipartCheckpoint };
                return transitioned;
            });
            await CommitLockedAsync(next, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish(taskId);
        await PumpAsync().ConfigureAwait(false);
    }

    private async Task UpdateCheckpointAsync(
        Guid taskId,
        long transferredBytes,
        DownloadCheckpoint? downloadCheckpoint,
        MultipartUploadCheckpoint? multipartCheckpoint,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = FindTask(_snapshot, taskId);
            if (task.State != TransferTaskState.Running) return;
            var bounded = Math.Clamp(transferredBytes, 0, task.TotalBytes);
            var next = ReplaceTask(_snapshot, taskId, current => current with
            {
                TransferredBytes = bounded,
                DownloadCheckpoint = downloadCheckpoint,
                MultipartCheckpoint = multipartCheckpoint,
                UpdatedAt = _clock()
            });
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish(taskId);
    }

    private async Task UpdateDestinationSnapshotAsync(
        Guid taskId,
        bool destinationExistedBeforeTransfer,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = FindTask(_snapshot, taskId);
            if (task.State != TransferTaskState.Running || task.DestinationExistedBeforeTransfer is not null)
                return;
            var next = ReplaceTask(_snapshot, taskId, current => current with
            {
                DestinationExistedBeforeTransfer = destinationExistedBeforeTransfer,
                UpdatedAt = _clock()
            });
            await CommitLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish(taskId);
    }

    private void ScheduleRetryWakeup(DateTimeOffset nextAttemptAt)
    {
        var delay = nextAttemptAt - _clock();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _ = WakeRetryAsync(delay, _lifetime.Token);
    }

    private async Task WakeRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            await PumpAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
            await CommitLockedAsync(mutation(_snapshot), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
        Publish(changedTaskId);
    }

    private async Task CommitLockedAsync(TransferStoreSnapshot next, CancellationToken cancellationToken)
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
            if (task.Id != taskId) return task;
            found = true;
            return update(task);
        }).ToArray();
        if (!found) throw new KeyNotFoundException($"找不到传输任务：{taskId}");
        return snapshot with { Tasks = tasks };
    }

    private static TransferStoreSnapshot ReplaceBatch(
        TransferStoreSnapshot snapshot,
        Guid batchId,
        Func<TransferBatchRecord, TransferBatchRecord> update)
    {
        var found = false;
        var batches = snapshot.Batches.Select(batch =>
        {
            if (batch.Id != batchId) return batch;
            found = true;
            return update(batch);
        }).ToArray();
        if (!found) throw new KeyNotFoundException($"找不到传输批次：{batchId}");
        return snapshot with { Batches = batches };
    }

    private static TransferBatchRecord FindBatch(TransferStoreSnapshot snapshot, Guid batchId) =>
        snapshot.Batches.FirstOrDefault(batch => batch.Id == batchId)
        ?? throw new KeyNotFoundException($"找不到传输批次：{batchId}");

    private static TransferTaskRecord FindTask(TransferStoreSnapshot snapshot, Guid taskId) =>
        snapshot.Tasks.FirstOrDefault(task => task.Id == taskId)
        ?? throw new KeyNotFoundException($"找不到传输任务：{taskId}");

    private static bool IsActive(TransferTaskRecord task) =>
        task.State is TransferTaskState.Queued or TransferTaskState.Running or TransferTaskState.RetryPending;

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("传输队列尚未初始化。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await PauseAllAsync().ConfigureAwait(false);
        await WaitForIdleAsync().ConfigureAwait(false);
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _mutex.Dispose();
    }
}

public static class TransferFailureClassifier
{
    public static TransferFailureInfo Classify(Exception exception)
    {
        if (exception is TransferExecutionException transfer) return transfer.Failure;
        var category = exception switch
        {
            TimeoutException => TransferFailureCategory.Timeout,
            TaskCanceledException => TransferFailureCategory.Timeout,
            HttpRequestException request when request.StatusCode is { } status && (int)status >= 500 =>
                TransferFailureCategory.Service,
            HttpRequestException => TransferFailureCategory.Network,
            SocketException => TransferFailureCategory.Network,
            IOException io when io.InnerException is SocketException => TransferFailureCategory.Network,
            UnauthorizedAccessException => TransferFailureCategory.LocalIo,
            FileNotFoundException or DirectoryNotFoundException => TransferFailureCategory.NotFound,
            IOException => TransferFailureCategory.LocalIo,
            ArgumentException => TransferFailureCategory.Validation,
            OperationCanceledException => TransferFailureCategory.Cancelled,
            _ => TransferFailureCategory.Unknown
        };
        var retryable = category is TransferFailureCategory.Network or TransferFailureCategory.Timeout or
            TransferFailureCategory.Service or TransferFailureCategory.Unknown;
        return new TransferFailureInfo(
            SensitiveDataRedactor.Redact(exception.Message),
            category,
            Retryable: retryable);
    }
}
