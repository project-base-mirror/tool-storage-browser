using System.Security.Cryptography;
using System.Text;

namespace S3Explorer.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    public const string ApplicationInstanceKey = "S3Explorer.App";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly bool _ownsMutex;
    private Task? _listener;
    private int _listenerStarted;
    private int _disposed;

    private SingleInstanceCoordinator(
        Mutex mutex,
        EventWaitHandle activationEvent,
        bool ownsMutex)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static SingleInstanceCoordinator Acquire(string instanceKey)
    {
        if (string.IsNullOrWhiteSpace(instanceKey))
            throw new ArgumentException("Instance key is required.", nameof(instanceKey));

        var suffix = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey.Trim())));
        var activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $@"Local\S3Explorer.Activate.{suffix}");
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(
                initiallyOwned: true,
                $@"Local\S3Explorer.Instance.{suffix}",
                out var createdNew);
            if (!createdNew)
                activationEvent.Set();
            return new SingleInstanceCoordinator(mutex, activationEvent, createdNew);
        }
        catch
        {
            mutex?.Dispose();
            activationEvent.Dispose();
            throw;
        }
    }

    public void StartListening(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsPrimary)
            throw new InvalidOperationException("Only the primary instance can listen for activation.");
        if (Interlocked.Exchange(ref _listenerStarted, 1) != 0)
            throw new InvalidOperationException("The activation listener has already started.");

        _listener = Task.Run(() => Listen(activationRequested));
    }

    private void Listen(Action activationRequested)
    {
        var handles = new WaitHandle[] { _activationEvent, _shutdown.Token.WaitHandle };
        while (WaitHandle.WaitAny(handles) == 0)
        {
            if (_shutdown.IsCancellationRequested)
                return;
            activationRequested();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        if (_listener is not null)
        {
            try { _listener.GetAwaiter().GetResult(); }
            catch (ObjectDisposedException) { }
        }
        if (_ownsMutex)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activationEvent.Dispose();
        _shutdown.Dispose();
    }
}
